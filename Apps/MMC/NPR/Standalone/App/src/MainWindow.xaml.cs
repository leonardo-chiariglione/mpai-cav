using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

using AIF.Controller;   // AifError

using Mpai.Core;
using Mpai.UaKit;       // AvatarUaHost
using Mpai.Aif.Api;     // ControllerApi, AvatarUtterance
using Mpai.Ua;          // WorkflowInterpreter, DeviceRegistry
using Mpai.Wdl;         // WorkflowReader

using Mpai.Mas.Client;       // RemoteControllerApi: the Controller API of a Controller that is a program of its own

namespace MmcNpr;

// MMC-NPR User Agent - Newspaper Reading, on its own: no MAS, no launcher. It does not follow its own steps: it executes the
// Workflow Description of the App (Apps/MMC/NPR/MMC-NPR.orch) with the interpreter of the User Agent, handing it the
// Controller API of a Controller that runs the Module in this process. The workflow starts the Module, acquires and presents the
// page, says to click on a column, and for every click offers the page, the Point and the machine's ID and presents the machine's
// Portable Avatar. This window supplies only the devices that serve the Data Types (the file chooser, the click on the page,
// the stage, the screen, the avatar) and Stop.
public partial class MainWindow : Window
{
    private const string BVO = "OSD-BVO-V1.5";
    private const string BTO = "OSD-BTO-V1.5";
    private const string PNT = "OSD-PNT-V1.5";
    private const string PAV = "PAF-PAV-V1.6";
    private const string MachineName = "Thalia";

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    // The workflow of the App: the one source, read where the repository keeps it.
    private static string WorkflowPath => Path.Combine(Mpai.Core.MpaiPaths.Root, "Apps", "MMC", "NPR", "MMC-NPR.orch");

    private IControllerApi?           _north;
    private AvatarUaHost?             _avatar;
    private MachineVoice?             _voice;
    private CancellationTokenSource?  _stopping;
    private Task?                     _running;
    private bool                      _closing;

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
            _voice = new MachineVoice(_avatar, MachineName, () => new RemoteControllerApi(Program.ControllerUrl), Program.Record);
            var workflow = new WorkflowReader().Read(File.ReadAllText(WorkflowPath));
            _north = new RemoteControllerApi(Program.ControllerUrl);
            await Task.Delay(TimeSpan.FromSeconds(2.0));   // let the avatar/WebView settle before speaking

            _stopping = new CancellationTokenSource();
            StopButton.IsEnabled = true;
            SetStatus("Running.");
            var interpreter = new WorkflowInterpreter(_north!.Async(), Devices(), SetStatus);
            _running = interpreter.RunAsync(workflow, _stopping.Token);
            try { await _running; }
            catch (OperationCanceledException) { }
            if (!_closing) SetStatus("Stopped.");
        }
        catch (Exception fatal) { Program.Record("workflow", fatal); SetStatus("stopped: " + fatal.Message); }
        finally { StopButton.IsEnabled = false; }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        _avatar?.Silence();
        _stopping?.Cancel();
    }

    // Closing ends the workflow first (its On Stop asks the Controller to stop the Module), then the window closes.
    protected override async void OnClosing(CancelEventArgs e)
    {
        if (_closing || _running is null || _running.IsCompleted) { base.OnClosing(e); return; }
        e.Cancel = true;
        _closing = true;
        _avatar?.Silence();
        _stopping?.Cancel();
        try { await Task.WhenAny(_running, Task.Delay(TimeSpan.FromSeconds(10))); } catch { }
        Close();
    }

    protected override void OnClosed(EventArgs e)
    {
        try { _voice?.Dispose(); } catch { }
        try { (_north as IDisposable)?.Dispose(); } catch { }
        base.OnClosed(e);
    }

    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);
    private void Instruct(string s)  => Dispatcher.Invoke(() => InstructionText.Text = s);

    // ---- the devices of this User Agent ---------------------------------------------------------------------------

    private DeviceRegistry Devices()
    {
        var devices = new DeviceRegistry();
        var stop = _stopping!.Token;

        // THE PAGE. The avatar asks for it, and the person chooses a file; its picture is the Basic Visual Object.
        devices.RegisterAcquire(BVO, async (_, _) =>
        {
            Instruct("Choose a picture.");
            await (_voice?.SayAsync("Choose a picture.") ?? Task.CompletedTask);
            return await StandardDevices.PictureAsync(Dispatcher);
        });

        // A POINT ON THE PAGE: the click, in pixels of the picture; waits until the person clicks, or the App is stopped.
        devices.RegisterAcquire(PNT, async (_, _, abandon) =>
        {
            var clicked = new TaskCompletionSource<(double X, double Y)?>(TaskCreationOptions.RunContinuationsAsynchronously);
            MouseButtonEventHandler onClick = (_, e) =>
            {
                if (PixelOfPicture(e.GetPosition(PageView)) is { } pixel) clicked.TrySetResult(pixel);
            };
            await Dispatcher.InvokeAsync(() => { PageView.MouseLeftButtonDown += onClick; PageView.Cursor = Cursors.Cross; });
            using var onStop    = stop.Register(() => clicked.TrySetResult(null));
            using var onAbandon = abandon.Register(() => clicked.TrySetResult(null));
            var at = await clicked.Task;
            await Dispatcher.InvokeAsync(() => { PageView.MouseLeftButtonDown -= onClick; PageView.Cursor = null; });
            if (at is not { } p) return null;
            SetStatus($"reading at {p.X:0},{p.Y:0}...");
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{{\"Header\":\"OSD-PNT-V1.5\",\"Coordinates\":{{\"X\":{p.X:0.#},\"Y\":{p.Y:0.#}}}}}");
        });

        // THE STAGE: the page, shown.
        devices.RegisterPresent("stage", data =>
        {
            foreach (var kv in data)
            {
                if (!kv.Key.Contains("OSD-BVO", StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    var visual = MpaiJson.FromJson<BasicVisualObject>(kv.Value);
                    if (visual?.Data is not { Length: > 0 }) continue;
                    Dispatcher.Invoke(() =>
                    {
                        var image = new BitmapImage();
                        image.BeginInit();
                        image.StreamSource = new MemoryStream(visual.Data);
                        image.CacheOption  = BitmapCacheOption.OnLoad;
                        image.EndInit();
                        PageView.Source = image;
                        ColumnText.Clear();
                    });
                }
                catch { /* what cannot be shown is still what was given */ }
            }
            return Task.CompletedTask;
        });

        // THE SCREEN: a prompt is shown and said.
        devices.RegisterPresent("screen", async data =>
        {
            if (!data.TryGetValue("Prompt", out var words)) return;
            Instruct(words);
            if (_voice is not null) await _voice.SayAsync(words);
        });

        // THE AVATAR: the machine's Portable Avatar. The words of the column are shown; the utterance is presented on the avatar and
        // the step ends when she has finished speaking. A Portable Avatar that carries text and no speech is spoken from its words.
        devices.RegisterPresent("avatar", async data =>
        {
            if (!data.TryGetValue(PAV, out var pav)) return;
            var words = AvatarUtterance.TextOfPortableAvatar(pav) ?? "";
            Dispatcher.Invoke(() => ColumnText.Text = words);
            SetStatus("done.");
            if (AvatarUtterance.FromPortableAvatar(pav) is { } utterance) await _voice!.PresentAsync(utterance);
            else if (words.Length > 0) await _voice!.SayAsync(words);
            else SetStatus("I could not read any text there.");
        });

        return devices;
    }

    // The pixel of the picture that a position over the Image is on; null outside the picture. The Image keeps the picture's
    // proportions (Stretch=Uniform), so it is centred with bands at its sides or above and below.
    private (double X, double Y)? PixelOfPicture(Point at)
    {
        if (PageView.Source is not BitmapSource page) return null;
        double scale = Math.Min(PageView.ActualWidth / page.PixelWidth, PageView.ActualHeight / page.PixelHeight);
        double x = (at.X - (PageView.ActualWidth - page.PixelWidth * scale) / 2) / scale;
        double y = (at.Y - (PageView.ActualHeight - page.PixelHeight * scale) / 2) / scale;
        if (x < 0 || y < 0 || x >= page.PixelWidth || y >= page.PixelHeight) return null;
        return (x, y);
    }
}
