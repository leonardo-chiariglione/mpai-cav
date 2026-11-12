using System;
using System.ComponentModel;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;

using Mpai.Core;
using Mpai.UaKit;       // AvatarUaHost, SpeechListener, TypedInput, MachineVoice, StandardDevices
using Mpai.Aif.Api;     // ControllerApi, AvatarUtterance
using Mpai.Ua;          // WorkflowInterpreter, DeviceRegistry
using Mpai.Wdl;         // WorkflowReader

using Mpai.Mas.Client;       // RemoteControllerApi: the Controller API of a Controller that is a program of its own

namespace MmcAmq;

// MMC-AMQ User Agent - Answer to Multimodal Question, on its own: no MAS, no launcher. It does not follow its own steps: it executes the
// Workflow Description of the App (Apps/MMC/AMQ/MMC-AMQ.orch) with the interpreter of the User Agent, handing it the Controller API of a
// Controller that runs the Module in this process. The workflow asks whether the person wants to ask about a picture, has the person
// choose it (it is shown), takes the question (spoken or typed, whichever comes first), offers the Module the picture, the question and
// the machine's ID, presents the Portable Avatar that carries the answer, and asks about another picture. This window supplies only the
// devices that serve the Data Types (the file chooser, the stage, the microphone, the text box, the screen and the avatar) and Stop.
// Every prompt and instruction is shown and said by the avatar. Typed: the yes or no. Speech is turned into words inside the Module only,
// and this client has no Module that turns a spoken yes or no into a Text Object.
public partial class MainWindow : Window
{
    private const string BVO = "OSD-BVO-V1.5";   // visual object (the picture)
    private const string BSO = "OSD-BSO-V1.5";   // speech object (the spoken question)
    private const string BTO = "OSD-BTO-V1.5";   // text object (typed question, typed yes or no)
    private const string PAV = "PAF-PAV-V1.6";
    private const string MachineName = "MPAI as a Service"; // the machine's ID: the Portable Avatar the Module gives is this machine's

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    // The workflow of the App: the one source, read where the repository keeps it.
    private static string WorkflowPath => Path.Combine(Mpai.Core.MpaiPaths.Root, "Apps", "MMC", "AMQ", "MMC-AMQ.orch");

    private IControllerApi?           _north;
    private AvatarUaHost?             _avatar;
    private SpeechListener?           _ears;
    private MachineVoice?             _voice;
    private readonly TypedInput       _typed;
    private CancellationTokenSource?  _stopping;
    private Task?                     _running;
    private bool                      _closing;
    private bool                      _told;     // the instruction of this question has been said

    public MainWindow()
    {
        InitializeComponent();
        _typed = new TypedInput(QuestionBox, AskButton);
        Loaded += OnLoaded;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("Wait while the avatar loads");
            _avatar = new AvatarUaHost(Web, Dispatcher, AmdDir, AssetsDir);
            await _avatar.InitAsync();
            _ears  = new SpeechListener(_avatar);
            _voice = new MachineVoice(_avatar, MachineName, () => new RemoteControllerApi(Program.ControllerUrl), Program.Record);
            var workflow = new WorkflowReader().Read(File.ReadAllText(WorkflowPath));
            // THE CONTROLLER IS A PROGRAM OF ITS OWN (MmcController): this User Agent only speaks the Controller API to it, and the workflow, read above, asks it to start the Module.
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

    // An instruction or a prompt is shown AND said; the step goes on when she has finished saying it.
    private async Task TellAsync(string words)
    {
        Dispatcher.Invoke(() => InstructionText.Text = words);
        if (_voice is not null) await _voice.SayAsync(words);
    }

    // ---- the devices of this User Agent ---------------------------------------------------------------------------

    private DeviceRegistry Devices()
    {
        var devices = new DeviceRegistry();
        var stop = _stopping!.Token;

        // THE PICTURE, chosen by the person from the image folder (the prompt that precedes it has said so).
        devices.RegisterAcquire(BVO, (_, _) => StandardDevices.PictureAsync(Dispatcher));

        // THE QUESTION, SPOKEN: from the microphone; it ends when the person stops speaking. The instruction is said once per question.
        devices.RegisterAcquire(BSO, async (_, _, abandon) =>
        {
            if (!_told) { _told = true; await TellAsync("Ask your question by speaking, or type it and press Enter."); }
            return await _ears!.ListenAsync(stop, abandon) is { } speech ? MpaiJson.ToJson(speech) : null;
        });

        // TEXT TYPED: the question, or the yes or no the workflow asks for (a Text whose role is YesNo).
        devices.RegisterAcquire(BTO, async (_, wanted, abandon) =>
        {
            if ((wanted ?? "").Contains("YesNo", StringComparison.OrdinalIgnoreCase))
                await TellAsync("Type yes or no, and press Enter.");
            var words = await _typed.AskAsync(stop, abandon);
            return words is null ? null : MpaiJson.ToJson(BasicTextObject.FromText(words));
        });

        // THE STAGE: the picture, shown.
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
                        ImageView.Source = image;
                        AnswerText.Text = "";
                    });
                    _told = false;   // a new picture, a new question
                }
                catch { /* what cannot be shown is still what was given */ }
            }
            return Task.CompletedTask;
        });

        // THE SCREEN: a prompt is shown and said.
        devices.RegisterPresent("screen", async data =>
        {
            if (data.TryGetValue("Prompt", out var words)) await TellAsync(words);
        });

        // THE AVATAR: the machine's Portable Avatar, which carries the answer as a Text Object and as a Speech Object. The words are shown;
        // the utterance is presented and the step ends when she has finished speaking. A Portable Avatar that carries text and no speech
        // is spoken from its words.
        devices.RegisterPresent("avatar", async data =>
        {
            if (!data.TryGetValue(PAV, out var pav)) return;
            var words = AvatarUtterance.TextOfPortableAvatar(pav) ?? "";
            Dispatcher.Invoke(() => AnswerText.Text = words);
            SetStatus("done.");
            if (AvatarUtterance.FromPortableAvatar(pav) is { } utterance) await _voice!.PresentAsync(utterance);
            else if (words.Length > 0) await _voice!.SayAsync(words);
            else SetStatus("The Module gave no answer.");
        });

        return devices;
    }
}
