using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

using AIF.Controller;   // AifError

using Mpai.Core;
using Mpai.UaKit;       // AvatarUaHost
using Mpai.Aif.Api;     // ControllerApi, AvatarUtterance

using Mpai.Providers;

namespace MmcNpr;

// MMC-NPR User Agent - Newspaper Reading, on its own: no MAS, no launcher. It follows the workflow of the App
// (Apps/MMC/NPR/MMC-NPR.orch), driven through the type-addressed Controller API:
//   start the Module once; the person chooses the page (OSD-BVO), which is shown; a click at the top of a column is the Point
//   (OSD-PNT, in pixels of the picture); each click offers the page, the Point and the machine's ID (string); the Module
//   answers with the machine's Portable Avatar (PAF-PAV), which carries the text of the column.
// The window shows the text and renders the Portable Avatar on the avatar it keeps. The workflow is the one the App has under
// MPAI-MAS: she asks for a picture, then says to click on a column, and reads it; the two prompts are spoken through PAF-RSR.
public partial class MainWindow : Window
{
    private const string Module = "1MMC-NPR-V2.5-I01";

    private const string BVO = "OSD-BVO-V1.5";
    private const string BTO = "OSD-BTO-V1.5";
    private const string PNT = "OSD-PNT-V1.5";
    private const string PAV = "PAF-PAV-V1.6";
    private const string MachineName = "Thalia";

    // The words of the workflow (Apps/MMC/NPR/MMC-NPR.orch under the launcher).
    private const string AskForPicture = "Choose a picture.";
    private const string AskForColumn  = "Click on a column and I will read it.";
    private const string RsrModule     = "1PAF-RSR-V1.6-I01";

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    private ControllerApi? _north;
    private AvatarUaHost?  _avatar;
    private byte[]?        _pageBytes;
    private string?        _pagePath;
    private bool           _busy;
    private ControllerApi? _say;
    private bool           _rsrStarted;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("Wait while the avatar loads");
            _avatar = new AvatarUaHost(Web, Dispatcher, AmdDir, AssetsDir);
            await _avatar.InitAsync();
            AifError started = AifError.OK;
            await Task.Run(() =>
            {
                _north = new ControllerApi(AmdDir, SettingsPath, store => new NprProvider(store));
                started = _north.StartFlow(Module);
            });
            if (started != AifError.OK) { SetStatus("could not start " + Module + ": " + started); return; }
            LoadButton.IsEnabled = true;
            InstructionText.Text = "Choose the image of a newspaper page.";
            SetStatus("Ready.");
            await Task.Delay(TimeSpan.FromSeconds(2.0));   // let the avatar/WebView settle before speaking
            await SayAsync(AskForPicture);
        }
        catch (Exception fatal) { Program.Record("startup", fatal); SetStatus("startup failed: " + fatal.Message); }
    }

    // Speak a line through PAF-RSR (text -> machine speech and face), presented on the avatar. A line that cannot be spoken is
    // reported in the status and does not stop the App.
    private async Task SayAsync(string words)
    {
        try
        {
            if (_avatar is null) return;
            _say ??= new ControllerApi(AmdDir, SettingsPath, store => new AmqProvider(store));
            if (!_rsrStarted)
            {
                var s = await Task.Run(() => _say!.StartFlow(RsrModule));
                if (s != AifError.OK) { Program.Record("say", new Exception("could not start " + RsrModule + ": " + s)); return; }
                _rsrStarted = true;
            }
            // PAF-RSR takes the Text Object twice (#1 to Text-To-Speech, #2 to Generative Face Description), the Avatar Model and the machine's ID.
            var inputs = new List<ControllerApi.Datum>
            {
                new ControllerApi.Datum(BTO, 1, MpaiJson.ToJson(BasicTextObject.FromText(words))),
                new ControllerApi.Datum(BTO, 2, MpaiJson.ToJson(BasicTextObject.FromText(words))),
                new ControllerApi.Datum("OSD-B3O-V1.5", 1, AvatarUtterance.AvatarDatum("cav-avatar.glb")),
                new ControllerApi.Datum("string", 1, System.Text.Json.JsonSerializer.Serialize(MachineName))
            };
            var r = await Task.Run(() => _say!.Advance(RsrModule, inputs));
            if (!r.Ok) { Program.Record("say", new Exception(r.Error.ToString())); return; }
            if (AvatarUtterance.FromPortableAvatar(r.ByType(PAV)) is not { } utterance) return;
            await _avatar.PresentAsync(utterance);
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(utterance.MachineSpeechWav) + 0.3));
        }
        catch (Exception ex) { Program.Record("say", ex); }
    }

    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _pagePath  = dlg.FileName;
            _pageBytes = File.ReadAllBytes(_pagePath);
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(_pageBytes))
            { bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); }
            PageView.Source = bmp;
            PageView.Cursor = Cursors.Cross;
            ColumnText.Clear();
            InstructionText.Text = "Click at the top of a column and the avatar will read it.";
            SetStatus("Page loaded.");
        }
        catch (Exception ex) { Program.Record("load", ex); SetStatus("could not load the page: " + ex.Message); return; }
        await SayAsync(AskForColumn);
    }

    // A click is the Point, in pixels of the picture (the page is drawn uniformly into the window).
    private async void PageView_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_north is null || _avatar is null || _pageBytes is null || _busy || PageView.Source is not BitmapSource page) return;
        var at = e.GetPosition(PageView);
        double scale = Math.Min(PageView.ActualWidth / page.PixelWidth, PageView.ActualHeight / page.PixelHeight);
        double x = (at.X - (PageView.ActualWidth - page.PixelWidth * scale) / 2) / scale;
        double y = (at.Y - (PageView.ActualHeight - page.PixelHeight * scale) / 2) / scale;
        if (x < 0 || y < 0 || x >= page.PixelWidth || y >= page.PixelHeight) return;

        _busy = true;
        try
        {
            SetStatus($"reading at {x:0},{y:0}...");
            var point = string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{{\"Header\":\"OSD-PNT-V1.5\",\"Coordinates\":{{\"X\":{x:0.#},\"Y\":{y:0.#}}}}}");
            var picture = BasicVisualObject.FromFile(_pagePath ?? "page.png", _pageBytes, "Picture");
            var inputs = new List<ControllerApi.Datum>
            {
                new ControllerApi.Datum(BVO, 1, MpaiJson.ToJson(picture)),
                new ControllerApi.Datum(PNT, 1, point),
                new ControllerApi.Datum("string", 1, System.Text.Json.JsonSerializer.Serialize(MachineName))
            };
            var r = await Task.Run(() => _north!.Advance(Module, inputs));
            if (!r.Ok) { Program.Record("advance", new Exception(r.Error.ToString())); SetStatus("could not read: " + r.Error); return; }

            var pav = r.ByType(PAV);
            ColumnText.Text = AvatarUtterance.TextOfPortableAvatar(pav) ?? "";
            SetStatus("done.");
            if (AvatarUtterance.FromPortableAvatar(pav) is { } utterance)
            {
                await _avatar.PresentAsync(utterance);
                await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(utterance.MachineSpeechWav) + 0.3));
            }
            else SetStatus("the Portable Avatar carries no speech.");
        }
        catch (Exception ex) { Program.Record("read", ex); SetStatus("error: " + ex.Message); }
        finally { _busy = false; }
    }

    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);

    protected override void OnClosed(EventArgs e)
    { try { _north?.StopFlow(Module); } catch { } base.OnClosed(e); }
}
