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
    private readonly GivingDevice _soundIn = new("CAE-UCM-V1.0"), _sceneIn = new("OSD-ASD-V1.5"), _typedIn = new("OSD-BTO-V1.5");
    private int _answers;                                // how many answers have been written (a script waits for the next)
    private string _sceneDescription = "no sounds, no room";
    private CancellationTokenSource? _stopping;
    private Task? _running;
    private bool _closing;
    private volatile bool _microphoneOn = true;
    private CancellationTokenSource? _speaking;         // what she is saying: Stop speaking cuts it
    private CancellationTokenSource? _hearing;          // the scene being heard on the loudspeakers
    private string? _storageLocation;                   // where the Module keeps its Objects and Scenes: the Controller's; asked how long to keep them, through the Controller API
    private TaskCompletionSource<string?>? _naming;      // the name the person is giving a sound
    private string? _lastScene;                          // the last Audio Scene Descriptors the Module gave

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        // Her face is 0.866 of the height it had (the column less the room the words took) and of the width it had, so its surface is three quarters of what it was.
        SideColumn.SizeChanged += (_, args) => Web.Height = Math.Max(150, (args.NewSize.Height - 150) * 0.866);
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { StopButton_Click(this, new RoutedEventArgs()); args.Handled = true; } };
        InputBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Submit(); };
        if (!App.Live) { _microphoneOn = false; MicButton.IsChecked = false; MicButton.Content = "Microphone is off - click to listen"; }       // the demonstration starts with the microphone off
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

            if (App.Live && !await OllamaAnswersAsync())
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
                _prepared = AsmVoiceModules.Prepare(MpaiPaths.Root, Path.Combine(run, "module"),
                    App.Live ? null : new JsonObject { [AsmVoiceModules.Jsi] = new JsonObject { ["GrammarOnly"] = "true" } },
                    App.Live ? null : "scene " + DateTime.Now.ToString("HHmmss"));      // the demonstration: the grammar alone, no language model
                _north = AsmVoiceModules.Controller(_prepared);
                var started = _north.StartFlow(AsmVoiceModules.Asm);
                if (started != AIF.Controller.AifError.OK) throw new InvalidOperationException("The Controller would not start the module: " + started);
                // WHERE THE MODULE KEEPS ITS OBJECTS AND SCENES is one place for this person, the same at every start (its Private Storage is under it): what is stored is held until it is deleted, and no duration is stated.
                _storageLocation = App.Storage ?? Path.Combine(downloads, "module-storage");
                _north.SharedStorageInit(AsmVoiceModules.Asm, _storageLocation);
            });

            Dispatcher.Invoke(RestoreKeep);
            var workflow = new WorkflowReader().Read(File.ReadAllText(Path.Combine(MpaiPaths.Root, "Apps", "CAE", "ASM", "ASM-VCI.orch")));
            _stopping = new CancellationTokenSource();
            LoadButton.IsEnabled = HearButton.IsEnabled = InputBox.IsEnabled = SendButton.IsEnabled = true;
            SetStatus("Ready.");
            var interpreter = new WorkflowInterpreter(_north!.Async(), Devices(), line => App.Log("[workflow] " + line));
            _running = interpreter.RunAsync(workflow, _stopping.Token);
            // THE SCENE OF LAST TIME, BY ITS NAME: the Module opens what it kept (the one scene this application makes is called stage).
            if (App.Live) _ = Task.Run(async () => { await Task.Delay(2000); _sceneIn.Give(OpenStub("stage")); });      // the demonstration starts with a new, empty scene: what was kept stays in storage, listed as kept
            if (App.Live) _ = WarmUpAsync();
            if (App.Script is { } script) _ = RunScriptAsync(script);
            try { await _running; }
            catch (OperationCanceledException) { }
            if (!_closing) SetStatus("Stopped.");
        }
        catch (Exception fatal) { App.Record("workflow", fatal); SetStatus("Stopped: " + fatal.Message); }
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
        _voiceDevice.TimedOut = () => _ = OnRecogniserStuckAsync();
        var devices = new DeviceRegistry();
        devices.RegisterDevice("Voice", _voiceDevice);
        devices.RegisterDevice("SoundIn", _soundIn);
        devices.RegisterDevice("SceneIn", _sceneIn);
        devices.RegisterDevice("TypedIn", _typedIn);

        // THE WORDS HEARD: shown; the Module is now working out the command, which takes a few seconds.
        devices.RegisterDevice("Heard", new ActingDevice("Heard", json =>
        {
            _voiceDevice?.Heard();
            if (_warming) return Task.CompletedTask;
            var heard = TextOf(json);
            if (Mpai.Mmc.Jsi.JsiAimProcessor.IsNoise(heard)) { App.Log("[window] noise ignored: " + heard); SetStatus("Ready."); return Task.CompletedTask; }
            Dispatcher.Invoke(() => { HeardText.Text = heard; AnswerText.Text = ""; });
            SetStatus("Thinking...");
            return Task.CompletedTask;
        }));

        // THE RESPONSE IN TEXT: shown.
        devices.RegisterDevice("ReplyText", new ActingDevice("ReplyText", json =>
        {
            if (_warming) { _warmed++; return Task.CompletedTask; }
            var said = TextOf(json);
            Interlocked.Increment(ref _textsToSpeak); Interlocked.Increment(ref _answers);
            App.Log("[window] answer shown (and to be spoken): " + said);
            Dispatcher.Invoke(() => AnswerText.Text = said);
            _stuck = 0;
            SetStatus("Ready.");
            return Task.CompletedTask;
        }));

        // THE RESPONSE IN SPEECH: said by the avatar; the step ends when she has finished.
        devices.RegisterDevice("ReplySpeech", new ActingDevice("ReplySpeech", async json =>
        {
            if (_warming) return;
            // WHAT IS SAID IS WHAT IS WRITTEN: speech is said only for an answer shown in the box; speech that has no answer written for it is not said.
            if (Interlocked.Decrement(ref _textsToSpeak) < 0) { Interlocked.Exchange(ref _textsToSpeak, 0); App.Log("[window] speech without an answer written for it: NOT said"); return; }
            if (App.Silent) { App.Log("[window] (silent) the answer is not played"); return; }
            try
            {
                var wav = MpaiJson.FromJson<BasicSpeechObject>(json)?.Data;
                App.Log("[window] speaking the answer: " + (wav is null ? 0 : AvatarUaHost.WavDurationSeconds(wav)).ToString("0.0") + " s");
                if (wav is { Length: > 0 })
                {
                    // Played at once; the step lasts as long as she speaks, unless Stop speaking (or Esc) cuts it.
                    _speaking = new CancellationTokenSource();
                    await _avatar!.PresentAsync(new AvatarUtterance(wav, null));
                    try { await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.3), _speaking.Token); } catch (OperationCanceledException) { }
                }
            }
            catch (Exception e) { App.Record("response speech", e); }
        }));

        // THE AUDIO SCENE: drawn from above, by the names and the places the scene gives; kept to be heard.
        devices.RegisterDevice("Scene", new ActingDevice("Scene", json =>
        {
            _lastScene = json;
            var sounds = SoundsOf(json); var room = RoomOf(json);
            App.Log($"[window] scene: {sounds.Count} sound(s), room " + (room is null ? "none" : $"{room.Width:0.##} x {room.Depth:0.##} m, ceiling {room.Height:0.##} m"));
            _sceneDescription = (sounds.Count == 0 ? "no sounds" : string.Join("; ", sounds.Select(x => FormattableString.Invariant($"{x.Name} at {x.X:0.##}, {x.Y:0.##}, {x.Z:0.##}, facing {x.Yaw:0.##}")))) + ", "
                                + (room is null ? "no room" : FormattableString.Invariant($"room {room.Width:0.##} x {room.Depth:0.##} m, ceiling {room.Height:0.##} m"));
            Dispatcher.Invoke(() => Room.Show(sounds, room));
            return Task.CompletedTask;
        }));
        devices.RegisterDevice("Unused", new ActingDevice("Unused", _ => Task.CompletedTask));

        // THE MODULE'S REPORT ON A SOUND THE PERSON LOADED: told at once, written and said the same; then where to put it is asked.
        devices.RegisterDevice("ObjectReport", new ActingDevice("ObjectReport", async json =>
        {
            var loading = _loading;
            App.Log("[window] report on an object command: " + json.Length + " bytes" + (loading is null ? "" : ", waiting for the report on " + loading.Value.Name));
            if (loading is null || LoadSaid(json, loading.Value.Id, loading.Value.Name) is not { } said) return;
            _loading = null;
            await TellAsync(said);
        }));

        // THE SCREEN: what the workflow says is shown and said.
        devices.RegisterPresent("screen", async data => { if (data.TryGetValue("Prompt", out var words)) await TellAsync(words); });
        return devices;
    }

    // A scene asked for by its name alone: the Module opens it from where it keeps its scenes.
    internal static string OpenStub(string name) => new JsonObject { ["Header"] = "OSD-ASD-V1.5", ["AudioSceneDescriptorsID"] = name }.ToJsonString();

    private static string TextOf(string json)
    {
        try { return MpaiJson.FromJson<BasicTextObject>(json)?.GetText() ?? ""; }
        catch { return ""; }
    }

    // The room of the scene, from its Closed Space: a box, by the corners the Audio Scene Descriptors give (first corner, the next along the width, the next along the depth, the one above the first).
    internal static SceneView.Room? RoomOf(string json)
    {
        JsonObject? box = null;
        void Walk(JsonNode? node)
        {
            if (box is not null) return;
            switch (node)
            {
                case JsonObject o:
                    if (o["RightParallelepiped"] is JsonObject r && r["CartPositions"] is JsonArray) { box = r; return; }
                    foreach (var kv in o) Walk(kv.Value);
                    break;
                case JsonArray array:
                    foreach (var element in array) Walk(element);
                    break;
            }
        }
        try
        {
            Walk(JsonNode.Parse(json));
            if (box?["CartPositions"] is not JsonArray c || c.Count < 4) return null;
            double V(int i, int k) => c[i]![k]!.GetValue<double>();
            double w = Math.Abs(V(1, 0) - V(0, 0)), d = Math.Abs(V(2, 1) - V(0, 1)), h = Math.Abs(V(3, 2) - V(0, 2));
            return new SceneView.Room((V(0, 0) + V(1, 0)) / 2, (V(0, 1) + V(2, 1)) / 2, w, d, h);
        }
        catch (Exception) { return null; }
    }

    // The sounds of the scene, with where they are: read from the Audio Scene Descriptors.
    internal static List<SceneView.Sound> SoundsOf(string json)
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

    // THE RECOGNISER DID NOT ANSWER IN TIME: the person is told to say it again; if it happens twice running, the Module is started again (its storage is where it was) so that the next
    // request works.
    // WARMING UP: before the person asks anything, two requests go through the whole chain - the recogniser, the JSON Interface and its model, the dialogue and its model, the voice - and
    // their answers are thrown away, so that the first request the person makes is not the cold one.
    private volatile bool _warming;
    private int _warmed;
    private async Task WarmUpAsync()
    {
        try
        {
            _warming = true; _warmed = 0;
            SetStatus("Warming up...");
            await Task.Delay(4000);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var words in new[] { "what is in the scene", "rearrange everything so that it sounds nicer" })
            {
                var before = _warmed;
                _voiceDevice?.Type(words);
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (_warmed == before && waited.Elapsed < TimeSpan.FromSeconds(90)) await Task.Delay(200);
                App.Log($"[window] warm-up \"{words}\": {(_warmed > before ? "answered" : "no answer")} after {waited.Elapsed.TotalSeconds:0.0} s");
            }
            App.Log($"[window] warmed up in {clock.Elapsed.TotalSeconds:0.0} s");
        }
        catch (Exception e) { App.Record("warm-up", e); }
        finally { _warming = false; SetStatus("Ready."); }
    }

    private int _stuck;
    private async Task OnRecogniserStuckAsync()
    {
        _stuck++;
        SetStatus("I did not get that.");
        _ = TellAsync("I did not get that, please repeat.");
        if (_stuck < 2 || _restarting) return;
        _restarting = true;
        try
        {
            App.Log("[window] the recogniser was stuck twice: the Module is started again");
            SetStatus("Starting the audio scene module again...");
            _stopping?.Cancel();
            try { if (_running is not null) await Task.WhenAny(_running, Task.Delay(TimeSpan.FromSeconds(15))); } catch { }
            await Task.Run(() =>
            {
                _north!.StopFlow(AsmVoiceModules.Asm);
                var started = _north.StartFlow(AsmVoiceModules.Asm);
                if (started != AIF.Controller.AifError.OK) throw new InvalidOperationException("The Controller would not start the module: " + started);
                _north.SharedStorageInit(AsmVoiceModules.Asm, _storageLocation!);
            });
            var workflow = new WorkflowReader().Read(File.ReadAllText(Path.Combine(MpaiPaths.Root, "Apps", "CAE", "ASM", "ASM-VCI.orch")));
            _stopping = new CancellationTokenSource();
            var interpreter = new WorkflowInterpreter(_north!.Async(), Devices(), line => App.Log("[workflow] " + line));
            _running = interpreter.RunAsync(workflow, _stopping.Token);
            _stuck = 0;
            SetStatus("Ready.");
            _ = TellAsync("I am ready again.");
        }
        catch (Exception e) { App.Record("restart", e); SetStatus("The module could not be started again: " + e.Message); }
        finally { _restarting = false; }
    }
    private bool _restarting;

    // ---- what the person does ---------------------------------------------------------------------------------------------------------------------

    private void Submit()
    {
        var words = InputBox.Text.Trim();
        App.Log("[window] Enter pressed, text: " + words + (_naming is not null ? " (the name of a sound)" : ""));
        if (words.Length == 0) return;
        InputBox.Clear();
        if (_naming is { } naming) { naming.TrySetResult(words); return; }       // the name of a sound, not a command
        HeardText.Text = words;
        SetStatus("Thinking...");
        if (App.Live) _voiceDevice?.Type(words);
        else _typedIn.Give(MpaiJson.ToJson(BasicTextObject.FromText(words)));                  // the demonstration: the typed words are the text, at once
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => Submit();

    // THE MICROPHONE, ON OR OFF, UNMISTAKABLY: off, nothing it hears reaches the Module.
    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        _microphoneOn = MicButton.IsChecked == true;
        MicButton.Content = _microphoneOn ? "Microphone is on" : "Microphone is off - click to listen";
        App.Log("[window] microphone " + (_microphoneOn ? "on" : "off"));
        if (!_microphoneOn) SetStatus("The microphone is off. Type, or click the red button to listen.");
        else SetStatus("Ready.");
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
            LoadSound(name, bytes);
        }
        catch (Exception ex) { _naming = null; App.Record("load a sound", ex); SetStatus("The sound could not be loaded: " + ex.Message); }
    }

    // A sound with its name, sent to the Module in a User Command that acquires it.
    private void LoadSound(string name, byte[] bytes)
    {
        var sound = new BasicAudioObject { BasicAudioObjectID = name, DescrMetadata = name, BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(bytes))] };
        var command = new UserCommand
        {
            UserCommandID = Guid.NewGuid().ToString("N")[..8], UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow),
            UserCommandRQ = new UserCommandRQ { AcquiredObject = new ManagedObject { BasicAudioObject = sound } }
        };
        _loading = (command.UserCommandID!, name);
        _soundIn.Give(MpaiJson.ToJson(command));
        SetStatus($"The sound {name} is sent.");
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
            _hearing = new CancellationTokenSource(); var heardToken = _hearing.Token;
            await Task.Run(() =>
            {
                using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(wav));
                using var output = new NAudio.Wave.WaveOutEvent();
                output.Init(reader); output.Play();
                while (output.PlaybackState == NAudio.Wave.PlaybackState.Playing && !heardToken.IsCancellationRequested) Thread.Sleep(100);
            });
            SetStatus("Ready.");
        }
        catch (Exception ex) { App.Record("hear the scene", ex); SetStatus("The scene could not be heard: " + ex.Message); }
    }

    // STOP SPEAKING: what she is saying and what is playing is cut off, and nothing else - the Module goes on, and the next request is answered as any other.
    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        App.Log("[window] Stop speaking");
        _avatar?.Silence();
        try { _speaking?.Cancel(); } catch { }
        try { _hearing?.Cancel(); } catch { }
        StopButton.IsEnabled = true;                                                   // momentary: nothing latches, the next answer is spoken as any other
        SetStatus("Ready.");
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

    // ---- how long what is stored is kept: no duration unless the person gives a date ---------------------------------------------------------------------

    private static string KeepFile => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ASM", "keep-until.txt");
    private bool _keepRestored;

    private void RestoreKeep()
    {
        try
        {
            if (File.Exists(KeepFile) && DateTime.TryParse(File.ReadAllText(KeepFile).Trim(), System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var date))
            { KeepDate.SelectedDate = date; KeepUntilDate.IsChecked = true; }
        }
        catch (Exception e) { App.Record("keep until", e); }
        _keepRestored = true;
        ApplyKeep();
    }

    private void Keep_Changed(object sender, RoutedEventArgs e) { if (_keepRestored) ApplyKeep(); }

    // The date is asked of the Controller (SharedStorage_Keep), for what the Module writes from now on; "until it is deleted" states nothing.
    private void ApplyKeep()
    {
        if (_north is null || _storageLocation is null) return;
        try
        {
            var until = KeepUntilDate.IsChecked == true && KeepDate.SelectedDate is { } date ? date.ToString("yyyy-MM-dd") : null;
            Directory.CreateDirectory(Path.GetDirectoryName(KeepFile)!);
            File.WriteAllText(KeepFile, until ?? "deleted");
            if (until is null && !_keptADate) return;
            var time = until is null ? AIF.SharedStorage.StorageTime.Scope : AIF.SharedStorage.StorageTime.Parse("until " + until);
            var done = _north.SharedStorageKeep(_storageLocation, "Data", time);
            _keptADate = until is not null;
            App.Log($"[window] keep {(until is null ? "until deleted" : "until " + until)}: {done}");
            SetStatus(until is null ? "What is stored is kept until it is deleted." : "What is stored from now on is kept until " + until + ".");
        }
        catch (Exception ex) { App.Record("keep until", ex); SetStatus("The keeping time could not be set: " + ex.Message); }
    }
    private bool _keptADate;

    // ---- showing and saying -----------------------------------------------------------------------------------------------------------------------

    private void SetStatus(string s) { App.Log("[status] " + s); Dispatcher.Invoke(() => StatusText.Text = s); }

    // An instruction is shown AND said; the step goes on when she has finished saying it.
    private int _textsToSpeak;
    private (string Id, string Name)? _loading;

    // What is said when the Module has answered a command that acquires a sound: that it is loaded and where to put it, or why it is not. Null if the report is not about that command.
    internal static string? LoadSaid(string json, string id, string name)
    {
        try
        {
            var report = JsonNode.Parse(json) as JsonObject;
            if (report?["UserCommandRS"] is not JsonObject rs) return null;
            if ((string?)report["UserCommandID"] is { } rid && rid != id && (string?)rs["UserCommandID"] != id) return null;
            if ((string?)rs["Outcome"] == "Done" || (string?)rs["Outcome"] == "Succeeded")
                return $"Loaded {name}. Where do you want to put it? For example: put the {name} at x 2, y 1, z 0, or say: two metres to the right.";
            var reasons = (rs["Actions"] as JsonArray)?.OfType<JsonObject>().Select(a => (string?)a["Reason"]).Where(r => !string.IsNullOrWhiteSpace(r)).Distinct().ToList() ?? [];
            return $"I could not load {name}" + (reasons.Count > 0 ? ": " + string.Join("; ", reasons).TrimEnd('.') : "") + ".";
        }
        catch (Exception) { return null; }
    }

    private async Task TellAsync(string words)
    {
        Interlocked.Increment(ref _answers);
        App.Log("[window] the window says: " + words);
        Dispatcher.Invoke(() => { InstructionText.Text = words; AnswerText.Text = words; });
        if (_voice is not null && !App.Silent) await _voice.SayAsync(words);
    }

    // THE REAL WINDOW, DRIVEN BY A SCRIPT: each line is `load <name> <file.wav>`, `say <words>` or `stop`, with `=> <what the answer contains> | <what the scene description contains>` to be checked.
    // What the window shows after each line (the words heard, the answer box, the status line, the scene) is written to <script>.dump.txt.
    private async Task RunScriptAsync(string path)
    {
        var dump = new System.Text.StringBuilder(); var failed = 0;
        string Shown(Func<string> read) { var r = ""; Dispatcher.Invoke(() => r = read()); return r; }
        try
        {
            await Task.Delay(2000);
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line.StartsWith('#')) continue;
                string? answerHas = null, sceneHas = null;
                var arrow = line.IndexOf("=>", StringComparison.Ordinal);
                if (arrow >= 0) { var checks = line[(arrow + 2)..].Split('|'); answerHas = checks[0].Trim(); sceneHas = checks.Length > 1 ? checks[1].Trim() : null; line = line[..arrow].Trim(); }
                var before = _answers;
                dump.AppendLine("> " + line);
                if (line.StartsWith("load ", StringComparison.Ordinal))
                {
                    var parts = line[5..].Trim().Split(' ', 2);
                    Dispatcher.Invoke(() => LoadSound(parts[0], File.ReadAllBytes(parts[1].Trim())));
                }
                else if (line.StartsWith("say ", StringComparison.Ordinal)) Dispatcher.Invoke(() => { InputBox.Text = line[4..].Trim(); Submit(); });
                else if (line == "stop") { Dispatcher.Invoke(() => StopButton_Click(this, new RoutedEventArgs())); dump.AppendLine("  (Stop speaking pressed; button enabled: " + Shown(() => StopButton.IsEnabled.ToString()) + ")"); await Task.Delay(500); continue; }
                var waited = System.Diagnostics.Stopwatch.StartNew();
                while (_answers == before && waited.Elapsed < TimeSpan.FromSeconds(90)) await Task.Delay(100);
                var timeout = _answers == before;
                await Task.Delay(1500);
                var heard = Shown(() => HeardText.Text); var answer = Shown(() => AnswerText.Text); var status = Shown(() => StatusText.Text);
                dump.AppendLine("  HEARD:  " + heard.Replace("\n", " ")).AppendLine("  ANSWER: " + answer.Replace("\n", " ")).AppendLine("  STATUS: " + status).AppendLine("  SCENE:  " + _sceneDescription).AppendLine($"  ({waited.Elapsed.TotalSeconds:0.0} s)");
                var pass = !timeout && (answerHas is null || answer.Contains(answerHas, StringComparison.OrdinalIgnoreCase)) && (sceneHas is null || _sceneDescription.Contains(sceneHas, StringComparison.OrdinalIgnoreCase));
                if (!pass) failed++;
                dump.AppendLine(pass ? "  PASS" : "  FAIL" + (timeout ? " (no answer in 90 s)" : $" (wanted answer containing '{answerHas}', scene containing '{sceneHas}')"));
            }
        }
        catch (Exception e) { dump.AppendLine("SCRIPT FAILED: " + e); failed++; }
        dump.AppendLine(failed == 0 ? "ALL PASS" : $"{failed} FAILED");
        File.WriteAllText(path + ".dump.txt", dump.ToString());
        Environment.Exit(failed == 0 ? 0 : 1);
    }
}
