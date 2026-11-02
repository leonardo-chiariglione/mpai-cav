using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Mpai.Core;
using Mpai.UaKit;         // AvatarUaHost, SpeechListener, TypedInput, StandardDevices
using Mpai.Mas.Client;       // RemoteControllerApi: the Controller API of a Controller that is a program of its own
using Mpai.Aif.Api;       // ControllerApi, AvatarUtterance
using Mpai.Ua;            // WorkflowInterpreter, DeviceRegistry
using Mpai.Wdl;           // WorkflowReader

namespace AcrApp;

// ACR User Agent - Access Control Registration, on its own: no MAS, no launcher. It does not follow its own steps: it executes the
// Workflow Description of the App (Apps/MMC/ACR/MMC-ACR.orch) with the interpreter of the User Agent, handing it the Controller API of a
// Controller that runs the Module in this process. The workflow starts the Module, takes the Avatar Model, has the avatar welcome the
// person and ask for the name, the face and a spoken sentence (the words are the workflow's and the Module speaks them), and has the
// avatar confirm the registration. This window supplies only the devices that serve the Data Types (the name box, the camera, the
// microphone, the Avatar Model, the avatar) and Stop. Register runs the workflow once; the person may run it again.
public partial class MainWindow : Window
{
    private const string BSO = "OSD-BSO-V1.5";
    private const string BTO = "OSD-BTO-V1.5";
    private const string BVO = "OSD-BVO-V1.5";
    private const string B3O = "OSD-B3O-V1.5";
    private const string PAV = "PAF-PAV-V1.6";

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    // The workflow of the App: the one source, read where the repository keeps it.
    private static string WorkflowPath => Path.Combine(Mpai.Core.MpaiPaths.Root, "Apps", "MMC", "ACR", "MMC-ACR.orch");

    private IControllerApi?           _north;
    private AvatarUaHost?             _avatar;
    private SpeechListener?           _ears;
    private readonly TypedInput       _typed;
    private CancellationTokenSource?  _stopping;
    private Task?                     _running;
    private bool                      _closing;

    public MainWindow()
    {
        InitializeComponent();
        _typed = new TypedInput(NameBox, ConfirmNameButton);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("Wait while the avatar loads");
            _avatar = new AvatarUaHost(Web, Dispatcher, AmdDir, AssetsDir);
            await _avatar.InitAsync();
            _ears = new SpeechListener(_avatar);
            _north = new RemoteControllerApi(Program.ControllerUrl);   // the Controller is a program of its own; the workflow asks it to start the Module
            await Task.Delay(TimeSpan.FromSeconds(2.0));   // let the avatar/WebView settle before speaking

            SetStatus("Ready. Press Register to begin.");
            InstructionText.Text = "Press Register to begin.";
            StartButton.IsEnabled = true;
        }
        catch (Exception fatal) { Program.Record("startup", fatal); SetStatus("startup failed: " + fatal.Message); }
    }

    private async void StartButton_Click(object sender, RoutedEventArgs e)
    {
        if (_north is null || _avatar is null) return;
        StartButton.IsEnabled = false;
        _stopping = new CancellationTokenSource();
        StopButton.IsEnabled = true;
        try
        {
            var workflow = new WorkflowReader().Read(File.ReadAllText(WorkflowPath));
            SetStatus("Running.");
            var interpreter = new WorkflowInterpreter(_north.Async(), Devices(), SetStatus);
            _running = interpreter.RunAsync(workflow, _stopping.Token);
            try { await _running; }
            catch (OperationCanceledException) { }
            if (!_closing) SetStatus(_stopping.IsCancellationRequested ? "Stopped." : "Done.");
        }
        catch (Exception ex) { Program.Record("workflow", ex); SetStatus("stopped: " + ex.Message); }
        finally
        {
            StopButton.IsEnabled = false;
            if (!_closing)
            {
                InstructionText.Text = "Press Register to enrol another person.";
                StartButton.IsEnabled = true;
            }
        }
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
        try { (_north as IDisposable)?.Dispose(); } catch { }
        base.OnClosed(e);
    }

    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);

    // ---- the devices of this User Agent ---------------------------------------------------------------------------
    // What the avatar says is the workflow's (the Module speaks it); an acquiring device here only waits for the person after she has
    // said what to do, so nothing is said twice.

    private DeviceRegistry Devices()
    {
        var devices = new DeviceRegistry();
        var stop = _stopping!.Token;

        // THE AVATAR MODEL this window draws.
        devices.RegisterAcquire(B3O, (_, _) => Task.FromResult<string?>(StandardDevices.AvatarModel()));

        // THE NAME, TYPED (speech recognition is unreliable for bare names): the box opens, and Enter or Confirm gives it.
        devices.RegisterAcquire(BTO, async (_, _, abandon) =>
        {
            await Dispatcher.InvokeAsync(() => NamePanel.Visibility = Visibility.Visible);
            try { return await _typed.AskAsync(stop, abandon) is { } name ? MpaiJson.ToJson(BasicTextObject.FromText(name)) : null; }
            finally { await Dispatcher.InvokeAsync(() => NamePanel.Visibility = Visibility.Collapsed); }
        });

        // THE FACE, from the camera.
        devices.RegisterAcquire(BVO, async (_, _) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(1), stop);   // a moment to turn to the camera
            return await StandardDevices.FaceAsync();
        });

        // THE SPEECH, from the microphone; it ends when the person stops speaking.
        devices.RegisterAcquire(BSO, async (_, _, abandon) =>
        {
            var speech = await _ears!.ListenAsync(stop, abandon);
            return speech is null ? null : MpaiJson.ToJson(speech);
        });

        // THE AVATAR: the machine's Portable Avatar, which says what the workflow gave the Module to say; the step ends when she has
        // finished speaking.
        devices.RegisterPresent("avatar", async data =>
        {
            if (!data.TryGetValue(PAV, out var pav)) return;
            if (AvatarUtterance.TextOfPortableAvatar(pav) is { Length: > 0 } words) Dispatcher.Invoke(() => InstructionText.Text = words);
            if (AvatarUtterance.FromPortableAvatar(pav) is { } utterance) await StandardDevices.PresentAsync(_avatar!, utterance);
            else SetStatus("the Portable Avatar carries no speech.");
        });

        return devices;
    }
}
