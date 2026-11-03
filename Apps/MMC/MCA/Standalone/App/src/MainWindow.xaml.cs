using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Mpai.Core;
using Mpai.UaKit;       // AvatarUaHost, SpeechListener, TypedInput, StandardDevices
using Mpai.Aif.Api;     // ControllerApi, AvatarUtterance
using Mpai.Ua;          // WorkflowInterpreter, DeviceRegistry
using Mpai.Wdl;         // WorkflowReader

using Mpai.Mas.Client;       // RemoteControllerApi: the Controller API of a Controller that is a program of its own

namespace MmcMca;

// MMC-MCA User Agent - Multimodal Conversation Avatar, on its own: no MAS, no launcher. It does not follow its own steps: it executes the
// Workflow Description of the App (Apps/MMC/MCA/MMC-MCA.orch) with the interpreter of the User Agent, handing it the Controller API of a
// Controller that runs the Module in this process. The workflow starts the Module, takes the Avatar Model, and each turn acquires the
// person's speech or typed text, offers them with the Avatar Model, the machine's ID and the memory of the conversation, and presents the
// Portable Avatar the Module answers with. This window supplies only the devices that serve the Data Types (the microphone, the text box,
// the Avatar Model, the avatar) and Stop. Ollama must be running for the dialogue (Entity Dialogue Processing).
public partial class MainWindow : Window
{
    private const string BSO = "OSD-BSO-V1.5";
    private const string BTO = "OSD-BTO-V1.5";
    private const string B3O = "OSD-B3O-V1.5";
    private const string PAV = "PAF-PAV-V1.6";
    private const string MachineName = "Thalia";

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    // The workflow of the App: the one source, read where the repository keeps it.
    private static string WorkflowPath => Path.Combine(Mpai.Core.MpaiPaths.Root, "Apps", "MMC", "MCA", "MMC-MCA.orch");

    private IControllerApi?           _north;
    private AvatarUaHost?             _avatar;
    private SpeechListener?           _ears;
    private MachineVoice?             _voice;
    private bool                      _told;     // the instruction of this turn has been said
    private TypedInput?               _typed;
    private CancellationTokenSource?  _stopping;
    private Task?                     _running;
    private bool                      _closing;

    public MainWindow()
    {
        InitializeComponent();
        _typed = new TypedInput(TextBoxIn, SendButton);
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

    private void AddLine(string who, string words) => Dispatcher.Invoke(() =>
    {
        Transcript.AppendText($"{who}: {words}{Environment.NewLine}{Environment.NewLine}");
        Transcript.ScrollToEnd();
    });

    // An instruction is shown AND said, and the step goes on when she has finished saying it. Once in a turn: the microphone and the text
    // box are asked for together, and a microphone that listens again does not tell again.
    private async Task TellOnceAsync(string words)
    {
        if (_told) return;
        _told = true;
        Instruct(words);
        if (_voice is not null) await _voice.SayAsync(words);
    }

    // ---- the devices of this User Agent ---------------------------------------------------------------------------

    private DeviceRegistry Devices()
    {
        var devices = new DeviceRegistry();
        var stop = _stopping!.Token;

        // THE AVATAR MODEL this window draws.
        devices.RegisterAcquire(B3O, (_, _) => Task.FromResult<string?>(StandardDevices.AvatarModel()));

        // THE PERSON'S SPEECH, from the microphone; it ends when the person stops speaking.
        devices.RegisterAcquire(BSO, async (_, _, abandon) =>
        {
            await TellOnceAsync("Speak, or type and press Enter.");
            var speech = await _ears!.ListenAsync(stop, abandon);
            if (speech is null) return null;
            AddLine("You", "(spoken)");
            return MpaiJson.ToJson(speech);
        });

        // THE PERSON'S TYPED TEXT: the box opens, and Enter - the key or the button - gives it.
        devices.RegisterAcquire(BTO, async (_, _, abandon) =>
        {
            var words = await _typed!.AskAsync(stop, abandon);
            if (words is null) return null;
            AddLine("You", words);
            return MpaiJson.ToJson(BasicTextObject.FromText(words));
        });

        // THE AVATAR: the machine's Portable Avatar. The reply is shown; the utterance is presented on the avatar and the step ends when
        // she has finished speaking.
        devices.RegisterPresent("avatar", async data =>
        {
            if (!data.TryGetValue(PAV, out var pav)) return;
            _told = false;   // the next turn tells again what to do
            AddLine(MachineName, AvatarUtterance.TextOfPortableAvatar(pav) ?? "");
            SetStatus("done.");
            if (AvatarUtterance.FromPortableAvatar(pav) is { } utterance) await StandardDevices.PresentAsync(_avatar!, utterance);
            else SetStatus("the Portable Avatar carries no speech.");
        });

        return devices;
    }
}
