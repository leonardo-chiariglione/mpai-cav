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
// The window shows the text and renders the Portable Avatar on the avatar it keeps; it makes no speech of its own.
public partial class MainWindow : Window
{
    private const string Module = "1MMC-NPR-V2.5-I01";

    private const string BVO = "OSD-BVO-V1.5";
    private const string PNT = "OSD-PNT-V1.5";
    private const string PAV = "PAF-PAV-V1.6";
    private const string MachineName = "Thalia";

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    private ControllerApi? _north;
    private AvatarUaHost?  _avatar;
    private byte[]?        _pageBytes;
    private string?        _pagePath;
    private bool           _busy;

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
        }
        catch (Exception fatal) { Program.Record("startup", fatal); SetStatus("startup failed: " + fatal.Message); }
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
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
        catch (Exception ex) { Program.Record("load", ex); SetStatus("could not load the page: " + ex.Message); }
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
