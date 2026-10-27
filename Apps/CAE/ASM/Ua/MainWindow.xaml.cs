using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

using Microsoft.Win32;

using Mpai.Aif.Api;          // ControllerApi, AvatarUtterance
using Mpai.Cae.AsmVoice;     // AsmVoiceModules
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Providers;        // AmqProvider (the leaves of the machine's own voice)
using Mpai.Ua;               // WorkflowInterpreter, DeviceRegistry
using Mpai.UaKit;            // AvatarUaHost, SpeechListener, MachineVoice
using Mpai.Wdl;              // WorkflowReader

namespace Mpai.Cae.AsmUa;

// AUDIO SCENE BY VOICE - THE USER AGENT. It does not follow steps of its own: it runs the Workflow Description of the App (Apps/CAE/ASM/ASM-VCI.orch) with the interpreter of the User
// Agent, against a Controller that holds the Module (Audio Scene Management, with the Voice Command Interface in it). The window supplies only DEVICES, each bound by the workflow to
// a Port of the Module by Data Type and Port Number:
//   what the person says     the microphone (voice-activity detection) or the text box, whose words the machine's voice speaks: a Speech Object, OSD-BSO #1
//   a sound                  a file chosen from disk, named by the person, sent in a User Command (AcquiredObject), CAE-UCM #1
//   the words heard, the response in text, the response in speech, the audio scene   shown, said by the avatar, drawn from above, and heard on the loudspeakers
// It reads no storage, names no AIM, and asks the Controller only through the Controller API. Everything it tells the person it also shows and says.
public partial class MainWindow : Window
{
    private static readonly string AmdDir = MpaiPaths.Amds, SettingsPath = MpaiPaths.Settings, AssetsDir = MpaiPaths.Assets;
    private const string MachineName = "Thalia";

    private ControllerApi? _north;
    private AsmVoiceModules.Prepared? _prepared;
    private AvatarUaHost? _avatar;
    private SpeechListener? _ears;
    private MachineVoice? _voice;
    private VoiceDevice? _voiceDevice;
    private readonly GivingDevice _soundIn = new("CAE-UCM-V1.0");
    private CancellationTokenSource? _stopping;
    private Task? _running;
    private bool _closing;
    private volatile bool _microphoneOn = true;
    private TaskCompletionSource<string?>? _naming;      // the name the person is giving a sound
    private string? _lastScene;                          // the last Audio Scene Descriptors the Module gave

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // Her face is 0.866 of the height it had (the column less the room the words took) and of the width it had, so its surface is three quarters of what it was.
        SideColumn.SizeChanged += (_, args) => Web.Height = Math.Max(150, (args.NewSize.Height - 150) * 0.866);
        InputBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Submit(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            SetStatus("Wait while the avatar loads.");
            _avatar = new AvatarUaHost(Web, Dispatcher, AmdDir, AssetsDir);
            await _avatar.InitAsync();
            _ears = new SpeechListener(_avatar);
            _voice = new MachineVoice(_avatar, MachineName, () => new ControllerApi(AmdDir, SettingsPath, store => new AmqProvider(store)), App.Record);
            await Task.Delay(TimeSpan.FromSeconds(2.0));          // let the avatar settle before it speaks

            if (!await OllamaAnswersAsync())
            {
                await TellAsync("The language model does not answer. Start it with the Ollama launcher, and start me again.");
                SetStatus("Ollama is not running.");
                return;
            }

            // The Controller, and where the Module keeps its Objects and Scenes: the Controller's business, asked through the Controller API.
            SetStatus("Wait while the audio scene module starts. The first time takes a minute.");
            var downloads = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ASM");
            var run = Path.Combine(downloads, "run-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            Directory.CreateDirectory(run);
            await Task.Run(() =>
            {
                _prepared = AsmVoiceModules.Prepare(MpaiPaths.Root, Path.Combine(run, "module"));
                _north = AsmVoiceModules.Controller(_prepared);
                var started = _north.StartFlow(AsmVoiceModules.Asm);
                if (started != AIF.Controller.AifError.OK) throw new InvalidOperationException("The Controller would not start the module: " + started);
                _north.SharedStorageInit(AsmVoiceModules.Asm, Path.Combine(run, "storage"));
            });

            var workflow = new WorkflowReader().Read(File.ReadAllText(Path.Combine(MpaiPaths.Root, "Apps", "CAE", "ASM", "ASM-VCI.orch")));
            _stopping = new CancellationTokenSource();
            StopButton.IsEnabled = LoadButton.IsEnabled = HearButton.IsEnabled = InputBox.IsEnabled = SendButton.IsEnabled = true;
            SetStatus("Ready.");
            var interpreter = new WorkflowInterpreter(_north!.Async(), Devices(), line => App.Log(line));
            _running = interpreter.RunAsync(workflow, _stopping.Token);
            try { await _running; }
            catch (OperationCanceledException) { }
            if (!_closing) SetStatus("Stopped.");
        }
        catch (Exception fatal) { App.Record("workflow", fatal); SetStatus("Stopped: " + fatal.Message); }
        finally { StopButton.IsEnabled = false; }
    }

    private static async Task<bool> OllamaAnswersAsync()
    {
        try { using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) }; return (await http.GetStringAsync("http://127.0.0.1:11434/api/tags")).Contains("llama3.2"); }
        catch { return false; }
    }

    // ---- the devices ----------------------------------------------------------------------------------------------------------------------------

    private DeviceRegistry Devices()
    {
        _voiceDevice = new VoiceDevice(_ears!, words => _voice!.SynthesizeAsync(words), () => _microphoneOn, words => _ = TellAsync(words));
        var devices = new DeviceRegistry();
        devices.RegisterDevice("Voice", _voiceDevice);
        devices.RegisterDevice("SoundIn", _soundIn);

        // THE WORDS HEARD: shown; the Module is now working out the command, which takes a few seconds.
        devices.RegisterDevice("Heard", new ActingDevice(json =>
        {
            Dispatcher.Invoke(() => { HeardText.Text = TextOf(json); AnswerText.Text = ""; });
            SetStatus("Thinking...");
            return Task.CompletedTask;
        }));

        // THE RESPONSE IN TEXT: shown.
        devices.RegisterDevice("ReplyText", new ActingDevice(json =>
        {
            Dispatcher.Invoke(() => AnswerText.Text = TextOf(json));
            SetStatus("Ready.");
            return Task.CompletedTask;
        }));

        // THE RESPONSE IN SPEECH: said by the avatar; the step ends when she has finished.
        devices.RegisterDevice("ReplySpeech", new ActingDevice(async json =>
        {
            try
            {
                var wav = MpaiJson.FromJson<BasicSpeechObject>(json)?.Data;
                if (wav is { Length: > 0 }) await _voice!.PresentAsync(new AvatarUtterance(wav, null));
            }
            catch (Exception e) { App.Record("response speech", e); }
        }));

        // THE AUDIO SCENE: drawn from above, by the names and the places the scene gives; kept to be heard.
        devices.RegisterDevice("Scene", new ActingDevice(json =>
        {
            _lastScene = json;
            var sounds = SoundsOf(json);
            Dispatcher.Invoke(() => Room.Show(sounds));
            return Task.CompletedTask;
        }));
        devices.RegisterDevice("Unused", new ActingDevice(_ => Task.CompletedTask));

        // THE SCREEN: what the workflow says is shown and said.
        devices.RegisterPresent("screen", async data => { if (data.TryGetValue("Prompt", out var words)) await TellAsync(words); });
        return devices;
    }

    private static string TextOf(string json)
    {
        try { return MpaiJson.FromJson<BasicTextObject>(json)?.GetText() ?? ""; }
        catch { return ""; }
    }

    // The sounds of the scene, with where they are: read from the Audio Scene Descriptors.
    private static List<SceneView.Sound> SoundsOf(string json)
    {
        var found = new List<SceneView.Sound>();
        void Walk(JsonNode? node)
        {
            switch (node)
            {
                case JsonObject o:
                    if (o["AudioObjectSpaceTime"]?["SpatialAttitude1"] is JsonObject attitude && attitude["Position"]?["CartPosition"] is JsonArray p && p.Count >= 3
                        && (o["AObjectIDOrAObject"] as JsonArray)?.FirstOrDefault()?["BasicAudioObjectID"]?.GetValue<string>() is { } name)
                    {
                        var yaw = attitude["Orientation"]?["Orientation"] is JsonArray a && a.Count >= 3 ? a[2]!.GetValue<double>() : 180;
                        found.Add(new SceneView.Sound(name, p[0]!.GetValue<double>(), p[1]!.GetValue<double>(), p[2]!.GetValue<double>(), yaw));
                    }
                    foreach (var kv in o) Walk(kv.Value);
                    break;
                case JsonArray array:
                    foreach (var element in array) Walk(element);
                    break;
            }
        }
        try { Walk(JsonNode.Parse(json)); } catch (Exception) { }
        return found;
    }

    // ---- what the person does ---------------------------------------------------------------------------------------------------------------------

    private void Submit()
    {
        var words = InputBox.Text.Trim();
        if (words.Length == 0) return;
        InputBox.Clear();
        if (_naming is { } naming) { naming.TrySetResult(words); return; }       // the name of a sound, not a command
        HeardText.Text = words;
        SetStatus("Thinking...");
        _voiceDevice?.Type(words);
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => Submit();

    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        _microphoneOn = MicButton.IsChecked == true;
        MicButton.Content = _microphoneOn ? "Microphone is on" : "Microphone is off";
    }

    // A SOUND FROM A FILE: chosen by the person, named by them (she asks, shown and said), and sent to the Module in a User Command that acquires it.
    private async void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new OpenFileDialog { Title = "Choose a sound", Filter = "Sounds (*.wav)|*.wav" };
            if (dialog.ShowDialog(this) != true) return;
            var bytes = await File.ReadAllBytesAsync(dialog.FileName);
            _naming = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            InputBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName); InputBox.SelectAll(); InputBox.Focus();
            _ = TellAsync("What do I call it? Type the name and press Enter.");
            var name = await _naming.Task;
            _naming = null;
            InputBox.Clear();
            if (string.IsNullOrWhiteSpace(name)) return;
            var sound = new BasicAudioObject { BasicAudioObjectID = name, DescrMetadata = name, BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(bytes))] };
            var command = new UserCommand
            {
                UserCommandID = Guid.NewGuid().ToString("N")[..8], UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow),
                UserCommandRQ = new UserCommandRQ { AcquiredObject = new ManagedObject { BasicAudioObject = sound } }
            };
            _soundIn.Give(MpaiJson.ToJson(command));
            SetStatus($"The sound {name} is sent.");
        }
        catch (Exception ex) { _naming = null; App.Record("load a sound", ex); SetStatus("The sound could not be loaded: " + ex.Message); }
    }

    // THE SCENE ON THE LOUDSPEAKERS: the Audio Scene Descriptors the Module gave, rendered as the listener hears them.
    private async void HearButton_Click(object sender, RoutedEventArgs e)
    {
        var scene = _lastScene;
        if (scene is null) { await TellAsync("There is nothing in the room to hear yet."); return; }
        try
        {
            SetStatus("Rendering the scene...");
            var wav = await Task.Run(() => new Mpai.SpatialAudio.AudioRendering(AppContext.BaseDirectory).Render(scene).Data);
            if (wav is not { Length: > 0 }) { await TellAsync("The scene gave no sound."); return; }
            SetStatus("Playing the scene.");
            await Task.Run(() =>
            {
                using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(wav));
                using var output = new NAudio.Wave.WaveOutEvent();
                output.Init(reader); output.Play();
                while (output.PlaybackState == NAudio.Wave.PlaybackState.Playing) Thread.Sleep(100);
            });
            SetStatus("Ready.");
        }
        catch (Exception ex) { App.Record("hear the scene", ex); SetStatus("The scene could not be heard: " + ex.Message); }
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        StopButton.IsEnabled = false;
        _avatar?.Silence();
        _stopping?.Cancel();
    }

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
        try { _north?.Dispose(); } catch { }
        base.OnClosed(e);
    }

    // ---- showing and saying -----------------------------------------------------------------------------------------------------------------------

    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);

    // An instruction is shown AND said; the step goes on when she has finished saying it.
    private async Task TellAsync(string words)
    {
        Dispatcher.Invoke(() => InstructionText.Text = words);
        if (_voice is not null) await _voice.SayAsync(words);
    }
}
