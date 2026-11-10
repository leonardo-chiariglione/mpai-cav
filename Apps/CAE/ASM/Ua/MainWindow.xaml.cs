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
using Mpai.Mas.Client;         // RemoteControllerApi: the Controller API of a Controller that is a program of its own
using Mpai.Core;
using Mpai.Core.OSD;
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

    private IControllerApi? _north;                     // the Controller API of the Controller (a program of its own): the User Agent holds no Controller, no Module and no AIM
    private const string AsmModule = "1CAE-ASM-V1.0-I01";
    private AvatarUaHost? _avatar;
    private SpeechListener? _ears;
    private VoiceDevice? _voiceDevice;
    private readonly GivingDevice _soundIn = new("CAE-UCM-V1.0"), _sceneCommand = new("CAE-UCM-V1.0"), _sceneIn = new("OSD-ASD-V1.5"), _typedIn = new("OSD-BTO-V1.5");
    private int _answers;                                // how many answers have been written (a script waits for the next)
    private string _sceneDescription = "no sounds, no room";
    private CancellationTokenSource? _stopping;
    private Task? _running;
    private bool _closing;
    private volatile bool _microphoneOn = true;
    private static readonly System.Windows.Media.Brush IdleStop = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xB0, 0x00, 0x20)), SpeakingStop = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1E, 0x3A, 0x8A));
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
        SideColumn.SizeChanged += (_, args) => Web.Height = Math.Max(150, (args.NewSize.Height - 195) * 0.866);
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { StopButton_Click(this, new RoutedEventArgs()); args.Handled = true; } };
        StopButton.Background = IdleStop;                              // off (red) until she speaks
        TextModeButton.IsChecked = true; ApplyMode();          // the window starts in Text: the microphone off, and she does not speak
        Room.ListenerDropped += (x, y, yaw) => Quiet(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"move me to x {x + 0.0:0.0#}, y {y + 0.0:0.0#}, facing {yaw + 0.0:0} degrees"));
        Room.Dropped += (name, x, y, z) => Quiet(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"move {name} to x {x + 0.0:0.0#}, y {y + 0.0:0.0#}, z {z + 0.0:0.0#}"));
        InputBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Submit(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            LoadOutput();
            SetStatus("Wait while the avatar loads.");
            if (TextModeButton.IsChecked == true) InputBox.Focus();          // typing works while she loads: what is typed waits for the Module
            AvatarUaHost.Diagnose = App.Log;                                  // what the page of the Face meets is in the log
            _avatar = new AvatarUaHost(Web, Dispatcher, AmdDir, AssetsDir);
            await _avatar.InitAsync(faceName: "ASM");
            _ears = new SpeechListener(_avatar);
            await Task.Delay(TimeSpan.FromSeconds(2.0));          // let the avatar settle before it speaks

            // THE WORKFLOW IS READ FIRST; what it says is done: it asks the Controller to start the Module (the L3), and binds the devices to its Ports. The Controller, a program of its own, starts
            // the Module and its AIMs as the L3 says and keeps the Module's Shared Storage where it was told.
            SetStatus("Wait while the audio scene module starts. The first time takes a minute.");
            LoadSaved();
            _sceneName = "stage";                          // the Controller was started with the same name (--scene)
            var workflow = new WorkflowReader().Read(File.ReadAllText(Path.Combine(MpaiPaths.Root, "Apps", "CAE", "ASM", "ASM-VCI.orch")));
            _north = new RemoteControllerApi(App.ControllerUrl);
            var started = new TaskCompletionSource<bool>();
            _stopping = new CancellationTokenSource();
            var interpreter = new WorkflowInterpreter(_north.Async(), Devices(), line => { App.Log("[workflow] " + line); if (line.StartsWith("[C] started", StringComparison.Ordinal)) started.TrySetResult(true); });
            _running = interpreter.RunAsync(workflow, _stopping.Token);
            if (await Task.WhenAny(started.Task, _running) != started.Task)
            {
                await _running;                            // the workflow ended before the Module started: say why
                throw new InvalidOperationException("The Controller would not start the module. Is the Controller running at " + App.ControllerUrl + "?");
            }
            Dispatcher.Invoke(RestoreKeep);
            _ready = true; LoadButton.IsEnabled = HearButton.IsEnabled = RoomButton.IsEnabled = true; ApplyMode();
            SetStatus("Ready.");
            // NOTHING KEEPS FROM ONE SESSION TO THE NEXT UNLESS IT WAS SAVED: what the last session left and the person did not save is deleted, then the scene saved last is opened.
            _purging = true; _ = PurgeAndOpenAsync();
            WarmUpIfVoice();                       // Text needs no warming up: what is typed is answered at once, and nothing hides its answer: its first spoken request must not be the cold one
            if (App.Script is { } script) _ = RunScriptAsync(script);
            try { await _running; }
            catch (OperationCanceledException) { }
            if (!_closing) SetStatus("Stopped.");
        }
        catch (Exception fatal) { App.Record("workflow", fatal); SetStatus("Stopped: " + fatal.Message); }
    }

    // ---- the devices ----------------------------------------------------------------------------------------------------------------------------

    private DeviceRegistry Devices()
    {
        _voiceDevice = new VoiceDevice(_ears!, () => _microphoneOn, words => _ = TellAsync(words));
        _voiceDevice.TimedOut = () => _ = OnRecogniserStuckAsync();
        var devices = new DeviceRegistry();
        devices.RegisterDevice("Voice", _voiceDevice);
        devices.RegisterDevice("SoundIn", _soundIn);
        devices.RegisterDevice("SceneIn", _sceneIn);
        devices.RegisterDevice("SceneCommand", _sceneCommand);
        devices.RegisterDevice("TypedIn", _typedIn);

        // THE WORDS HEARD: shown; the Module is now working out the command, which takes a few seconds.
        devices.RegisterDevice("Heard", new ActingDevice("Heard", json =>
        {
            _voiceDevice?.Heard();
            if (_warming) return Task.CompletedTask;
            var heard = TextOf(json);
            if (IsNoise(heard)) { App.Log("[window] noise ignored: " + heard); SetStatus("Ready."); return Task.CompletedTask; }
            Dispatcher.Invoke(() => { HeardText.Text = heard; AnswerText.Text = ""; });
            SetStatus("Thinking...");
            return Task.CompletedTask;
        }));

        // THE RESPONSE IN TEXT: shown.
        Func<string, Task> showReply = json =>
        {
            if (_warming) { _warmed++; return Task.CompletedTask; }
            var said = TextOf(json);
            if (_quietPending > 0 && DateTime.UtcNow - _quietSince < TimeSpan.FromSeconds(5)) { _quietPending--; App.Log("[window] the answer to a command given for the person: " + said); return Task.CompletedTask; }
            Interlocked.Increment(ref _textsToSpeak); Interlocked.Increment(ref _answers);
            App.Log("[window] answer shown (and to be spoken): " + said);
            Dispatcher.Invoke(() => AnswerText.Text = said);
            _stuck = 0;
            SetStatus("Ready.");
            return Task.CompletedTask;
        };
        devices.RegisterDevice("ReplyText", new ActingDevice("ReplyText", showReply));

        // THE RESPONSE IN SPEECH: said by the avatar; the step ends when she has finished.
        Func<string, Task> emitReply = async json =>
        {
            if (_warming) return;
            // WHAT IS SAID IS WHAT IS WRITTEN: speech is said only for an answer shown in the box; speech that has no answer written for it is not said.
            if (Interlocked.Decrement(ref _textsToSpeak) < 0) { Interlocked.Exchange(ref _textsToSpeak, 0); App.Log("[window] speech without an answer written for it: NOT said"); return; }
            if (App.Silent || _textOnly) { App.Log(App.Silent ? "[window] (silent) the answer is not played" : "[window] text mode: the answer is written, not said"); return; }
            try
            {
                var wav = MpaiJson.FromJson<BasicSpeechObject>(json)?.Data;
                App.Log("[window] speaking the answer: " + (wav is null ? 0 : AvatarUaHost.WavDurationSeconds(wav)).ToString("0.0") + " s");
                if (wav is { Length: > 0 })
                {
                    // Played at once; the step lasts as long as she speaks, unless Stop speaking (or Esc) cuts it.
                    _speaking = new CancellationTokenSource();
                    Dispatcher.Invoke(() => StopButton.Background = SpeakingStop);                          // on (deep blue) while she speaks
                    await _avatar!.PresentAsync(new AvatarUtterance(wav, null));
                    try { await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.3), _speaking.Token); } catch (OperationCanceledException) { }
                    Dispatcher.Invoke(() => StopButton.Background = IdleStop);
                }
            }
            catch (Exception e) { App.Record("response speech", e); }
        };
        devices.RegisterDevice("ReplySpeech", new ActingDevice("ReplySpeech", emitReply));

        // THE AUDIO SCENE: drawn from above, by the names and the places the scene gives; kept to be heard.
        devices.RegisterDevice("Scene", new ActingDevice("Scene", json =>
        {
            _lastScene = json;
            App.Log("[window] the scene as heard: " + (Mpai.SpatialAudio.AudioRendering.RoomOf(json) is { } heard ? $"room {heard.X1 - heard.X0} x {heard.Y1 - heard.Y0} x {heard.Z1 - heard.Z0}, absorption {heard.Absorption}, time {heard.Time:0.00} s" : "no room acoustics (profile in the scene: " + json.Contains("\"AcousticProfile\"") + ", walls: " + json.Contains("RightParallelepiped") + ", keys: " + string.Join(",", (JsonNode.Parse(json) as JsonObject)?.Select(k => k.Key) ?? [])+ ")"));
            RememberSceneName(json);
            KeepSavedMembers(json);
            var sounds = SoundsOf(json); var room = RoomOf(json);
            foreach (var s in sounds) WriteHint(s.Name);
            App.Log($"[window] scene: {sounds.Count} sound(s), room " + (room is null ? "none" : $"{room.Width:0.##} x {room.Depth:0.##} m, ceiling {room.Height:0.##} m"));
            _sceneSounds = sounds.Select(x => x.Name).ToHashSet(StringComparer.Ordinal);
            _sceneDescription = (sounds.Count == 0 ? "no sounds" : string.Join("; ", sounds.Select(x => FormattableString.Invariant($"{x.Name} at {x.X:0.##}, {x.Y:0.##}, {x.Z:0.##}, facing {x.Yaw:0.##}")))) + ", "
                                + (room is null ? "no room" : FormattableString.Invariant($"room {room.Width:0.##} x {room.Depth:0.##} m, ceiling {room.Height:0.##} m")) + (ListenerOf(json) is { } lis && (lis.X != 0 || lis.Y != 0 || lis.Yaw != 0) ? FormattableString.Invariant($", listener at {lis.X:0.##}, {lis.Y:0.##} looking {lis.Yaw:0.##}") : "");
            Dispatcher.Invoke(() => Room.Show(sounds, room, ListenerOf(json)));
            return Task.CompletedTask;
        }));
        devices.RegisterDevice("Unused", new ActingDevice("Unused", _ => Task.CompletedTask));
        // THE REPORTS OF THE OTHER EDITING AIMS (scenes, speech, multimodal): a scene saved or opened, and the lists of what the Module keeps.
        foreach (var reporting in new[] { "SceneReport", "SpeechReport", "SpeechSceneRep", "MultimodalRep" })
            devices.RegisterDevice(reporting, new ActingDevice(reporting, json => { TakeReport(json); return Task.CompletedTask; }));

        // THE MODULE'S REPORT ON A SOUND THE PERSON LOADED: told at once, written and said the same; then where to put it is asked.
        devices.RegisterDevice("ObjectReport", new ActingDevice("ObjectReport", async json =>
        {
            if (TakeReport(json)) return;
            var loading = _loading;
            App.Log("[window] report on an object command: " + json.Length + " bytes" + (loading is null ? "" : ", waiting for the report on " + loading.Value.Name));
            if (loading is null || LoadSaid(json, loading.Value.Id, loading.Value.Name) is not { } said) return;
            _loading = null;
            // THE NAME THE MODULE GAVE: a name that is taken is given another ("pippo 2"); the sound is placed, and spoken of, under the name it has.
            var actual = StoredNameOf(json) ?? loading.Value.Name;
            if (actual != loading.Value.Name) { said = $"Loaded as {actual}, because {loading.Value.Name} is already kept, at the centre. Drag it, or tell me where."; WriteHint(actual); }
            if (said.StartsWith("Loaded ", StringComparison.Ordinal)) PlaceAtTheCentre(actual, true);      // at the centre: the person drags it, or says where
            await TellAsync(said);
        }));

        // THE SCREEN: what the workflow says is shown and said.
        devices.RegisterPresent("screen", async data =>
        {
            if (data.TryGetValue("Prompt", out var words)) await TellAsync(words);
            // What the Module answered to a request of the workflow: shown, and emitted through the same devices as any response.
            if (data.TryGetValue("OSD-BTO-V1.5", out var written)) await showReply(written);
            if (data.TryGetValue("OSD-BSO-V1.5", out var spoken)) await emitReply(spoken);
        });
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

    // The Audio Scene Descriptors with the one sound that is named: the others are left out, the room and the listener stay.
    internal static string OnlyMember(string json, string name)
    {
        try
        {
            var root = JsonNode.Parse(json);
            void Walk(JsonNode? node)
            {
                switch (node)
                {
                    case JsonObject o: foreach (var kv in o.ToList()) Walk(kv.Value); break;
                    case JsonArray array:
                        foreach (var element in array.ToList())
                            if (element is JsonObject m && m["AudioObjectSpaceTime"] is not null && (m["AObjectIDOrAObject"] as JsonArray)?.FirstOrDefault()?["BasicAudioObjectID"]?.GetValue<string>() is { } id)
                            { if (id != name) array.Remove(element); }
                            else Walk(element);
                        break;
                }
            }
            Walk(root);
            return root!.ToJsonString();
        }
        catch (Exception) { return json; }
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

    // Where the scene says the listener is (its UserPoV), in metres: x to the right, y ahead; null when it says nothing.
    internal static (double X, double Y, double Yaw)? ListenerOf(string json)
    {
        try
        {
            var pov = JsonNode.Parse(json)?["UserPoV"];
            return pov?["CartPosition"] is JsonArray c && c.Count >= 2 ? (c[0]!.GetValue<double>(), c[1]!.GetValue<double>(), pov["Orientation"] is JsonArray o && o.Count >= 3 ? o[2]!.GetValue<double>() : 0) : null;
        }
        catch (Exception) { return null; }
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
            await Task.Delay(App.Live ? 4000 : 1500);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var words in App.Live ? new[] { "what is in the scene", "rearrange everything so that it sounds nicer" } : new[] { "what is in the scene" })
            {
                var before = _warmed;
                _typedIn.Give(MpaiJson.ToJson(BasicTextObject.FromText(words)));
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
            var workflow = new WorkflowReader().Read(File.ReadAllText(Path.Combine(MpaiPaths.Root, "Apps", "CAE", "ASM", "ASM-VCI.orch")));
            _stopping = new CancellationTokenSource();
            var again = new TaskCompletionSource<bool>();
            var interpreter = new WorkflowInterpreter(_north!.Async(), Devices(), line => { App.Log("[workflow] " + line); if (line.StartsWith("[C] started", StringComparison.Ordinal)) again.TrySetResult(true); });
            _running = interpreter.RunAsync(workflow, _stopping.Token);
            await Task.WhenAny(again.Task, _running);
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
        if (_naming is not null) InputBox.Clear(); else InputBox.SelectAll();       // a command stays where it was typed: it can be corrected and sent again
        if (_naming is { } naming) { naming.TrySetResult(words); return; }       // the name of a sound, not a command
        HeardText.Text = words;
        if (System.Text.RegularExpressions.Regex.IsMatch(words.ToLowerInvariant().TrimEnd('.', '!'), @"^(?:please )?(?:close|hide|remove)(?: the)? (?:acoustic )?(?:profile|properties|panel|room acoustics)(?: panel| window)?$"))
        { _panel?.Hide(); AnswerText.Text = "Done: the acoustic properties are closed."; return; }
        if (TryOutputWords(words)) return;                                        // the sound output is the User Agent's: it does not go to the Module
        SetStatus("Thinking...");
        GiveText(words);
        WatchForAnAnswer();
    }

    // ---- HOW THE SCENE IS HEARD. The way it is rendered - headphones, loudspeakers - is the Module's: the Rendering Mode of the Acoustic Profile of the scene, said to it ("play on headphones") like any other
    // property of the scene; the renderer reads it from the scene. The sound output of the machine it is played on is the User Agent's (the Loudspeaker Unit, the AOD): chosen here, kept for the next run, and not told to the Module. ----
    private string? _deviceName;                                   // null: the output Windows uses
    private OutputPanel? _outputPanel;
    private static string OutputFile => System.IO.Path.Combine(AppContext.BaseDirectory, "asm-output.json");

    private void LoadOutput()
    {
        try { if (JsonNode.Parse(File.ReadAllText(OutputFile)) is JsonObject saved) _deviceName = (string?)saved["Device"]; }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException or UnauthorizedAccessException) { }          // none kept yet: the output Windows uses
    }

    private void SaveOutput()
    {
        try { File.WriteAllText(OutputFile, new JsonObject { ["Device"] = _deviceName }.ToJsonString()); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.Log("[window] the sound output could not be kept: " + e.Message); }
    }

    // How the scene in the room is rendered now, as its Acoustic Profile says (binaural, the renderer's own, where it says nothing).
    private Mpai.SpatialAudio.SpatialRenderer.Layout LayoutNow() => (_lastScene is { } scene ? Mpai.SpatialAudio.AudioRendering.LayoutOf(scene) : null) ?? Mpai.SpatialAudio.SpatialRenderer.Layout.Binaural;

    private static string LayoutLabel(Mpai.SpatialAudio.SpatialRenderer.Layout layout) => OutputPanel.Layouts.First(l => l.Layout == layout).Label;

    // A way of rendering chosen in the panel: said to the Module as the command a person could say.
    private void GiveRendering(Mpai.SpatialAudio.SpatialRenderer.Layout layout)
    {
        var (mode, channels) = layout switch
        {
            Mpai.SpatialAudio.SpatialRenderer.Layout.Mono => ("Mono", (int?)null),
            Mpai.SpatialAudio.SpatialRenderer.Layout.Stereo => ("Stereo", null),
            Mpai.SpatialAudio.SpatialRenderer.Layout.Quad => ("Multichannel", 4),
            Mpai.SpatialAudio.SpatialRenderer.Layout.Surround51 => ("Multichannel", 6),
            Mpai.SpatialAudio.SpatialRenderer.Layout.Surround71 => ("Multichannel", 8),
            _ => ("Binaural", null)
        };
        App.Log("[window] the scene is to be rendered for: " + LayoutLabel(layout));
        _sceneCommand.Give(MpaiJson.ToJson(new UserCommand
        {
            UserCommandID = Guid.NewGuid().ToString("N")[..8], UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow),
            UserCommandRQ = new UserCommandRQ { ModifiedScene = new SceneChange { RenderingMode = mode, ChannelCount = channels } }
        }));
    }

    private void OutputButton_Click(object sender, RoutedEventArgs e)
    {
        if (_outputPanel is null)
            _outputPanel = new OutputPanel(this, LayoutNow(), _deviceName, GiveRendering,
                device => { _deviceName = device; SaveOutput(); App.Log("[window] the scene is played through: " + (device ?? OutputPanel.DefaultDevice)); });
        else _outputPanel.Fill(LayoutNow(), _deviceName);
        _outputPanel.Show(); _outputPanel.Activate();
    }

    // "play on <the name of an output>" and "which sound outputs are there": the User Agent's, answered here. "play on headphones" and the like are the Module's (the grammar has them). False when the words are not about this.
    private bool TryOutputWords(string words)
    {
        var text = words.ToLowerInvariant().Trim().TrimEnd('.', '!', '?');
        if (System.Text.RegularExpressions.Regex.IsMatch(text, @"^(?:please )?(?:which|what|list|show)(?: me)?(?: the| are the| is the)?(?: available)? (?:sound |audio )?(?:outputs?|output devices?|devices?)(?: are there| do you know| are available| can i use| i can use)?$"))
        {
            var devices = OutputPanel.Devices();
            AnswerText.Text = "The sound outputs are: " + (devices.Count == 0 ? "none found" : string.Join("; ", devices)) + ". Now: " + (_deviceName ?? OutputPanel.DefaultDevice)
                            + ". The scene is rendered for " + LayoutLabel(LayoutNow()).ToLowerInvariant() + ". Say for example: play on " + (devices.Count > 0 ? devices[0].ToLowerInvariant() : "headphones") + ", or play on stereo speakers.";
            return true;
        }
        var play = System.Text.RegularExpressions.Regex.Match(text, @"^(?:please )?(?:play|hear|listen|render|output)(?: it| the scene| the sound)?(?: on| through| in| with| to| over)(?: the| my)? (?<what>.+)$");
        if (!play.Success) return false;
        var what = play.Groups["what"].Value.Trim();
        if (OutputPanel.Layouts.Any(l => l.Words.Contains(what))) return false;     // a way of rendering: the Module's
        var device = OutputPanel.Devices().FirstOrDefault(d => d.ToLowerInvariant().Contains(what));
        if (device is null) return false;                                          // not an output of this machine: the Module is asked, as for any words
        _deviceName = device; SaveOutput(); _outputPanel?.Fill(LayoutNow(), _deviceName);
        AnswerText.Text = "Done: the scene is played through " + device + ".";
        return true;
    }

    // A REQUEST THAT GETS NO WRITTEN ANSWER does not leave the window on "Thinking...": after 20 s the person is told, and the log says it (what came back, if anything, is in the log above this line).
    private int _asked;
    private void WatchForAnAnswer()
    {
        var mine = ++_asked; var before = _answers;
        _ = Task.Run(async () =>
        {
            await Task.Delay(20_000);
            if (_asked != mine || _answers != before) return;
            App.Log("[window] no written answer came in 20 s to request " + mine);
            Dispatcher.Invoke(() => { if (StatusText.Text.StartsWith("Thinking", StringComparison.Ordinal)) SetStatus("No written answer came: say it again."); });
        });
    }

    // THE ACOUSTIC PROFILE OF THE ROOM, in its panel: asked for by the button or by words (the answer comes with the reports), and asked for again when a property is set, if the panel is open.
    private AcousticPanel? _panel;

    private void RoomButton_Click(object sender, RoutedEventArgs e) => AskProfile();

    // The request the Room acoustics button gave, so that its answer is known when it comes: the whole answer is logged, and when it holds no Acoustic Profile of the scene the person is told why (it was silent).
    private string? _profileAsk;

    private void AskProfile()
    {
        _profileAsk = Guid.NewGuid().ToString("N")[..8];
        _sceneCommand.Give(MpaiJson.ToJson(new UserCommand { UserCommandID = _profileAsk, UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandRQ = new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Acoustic Profile", ObjectID = _sceneName } } }));
    }

    private void GiveText(string words)
    {
        if (_purging) { lock (_held) _held.Add(() => GiveText(words)); return; }          // what is typed while the old session is cleared waits for it
        _typedIn.Give(MpaiJson.ToJson(BasicTextObject.FromText(words)));                  // the typed words are the text, at once (OSD-BTO #1)
    }

    // A COMMAND THE WINDOW GIVES FOR THE PERSON (a sound placed at the centre when it is loaded, a sound dragged with the mouse): the same words a person could say, through the same path, so
    // it is checked as any is; its answer is not shown or said - what the person sees is the scene.
    // A SOUND JUST LOADED GOES TO THE CENTRE. The JSON Interface learns of the new sound from the next listing it asks the application for, which it does when a request follows
    // the report of the load: the placement is given a moment after the report, so that it is that request.
    private string? _sceneName;

    // WHAT THE PERSON SAVED (the window keeps it, for itself, in a file beside the Module's storage): the scenes saved, each with the names of the sounds in it, and the one saved or opened last.
    // Everything else the Module holds is of the session that made it, and is deleted at the next start.
    private sealed class SavedIndex
    {
        public Dictionary<string, List<string>> Scenes { get; set; } = new();
        public string? Last { get; set; }
    }

    private SavedIndex _saved = new();
    private bool _legacy;
    private volatile bool _purging;
    private readonly List<Action> _held = [];
    private string StorageRoot => (App.Storage ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", "ASM", "module-storage")).TrimEnd(Path.DirectorySeparatorChar, '/');
    private string IndexFile => StorageRoot + ".saved.json";

    private void LoadSaved()
    {
        try
        {
            if (!File.Exists(IndexFile)) { _legacy = true; _saved = new(); return; }
            _saved = System.Text.Json.JsonSerializer.Deserialize<SavedIndex>(File.ReadAllText(IndexFile)) ?? new();
        }
        catch (Exception e) when (e is IOException or System.Text.Json.JsonException) { _saved = new(); }
    }

    private void WriteSaved()
    {
        try { File.WriteAllText(IndexFile, System.Text.Json.JsonSerializer.Serialize(_saved, new System.Text.Json.JsonSerializerOptions { WriteIndented = true })); }
        catch (IOException e) { App.Record("saved index", e); }
    }

    // The first time this rule applies, what the Module holds is a copy kept aside before anything is deleted.
    private void BackUpLegacyStorage()
    {
        try
        {
            if (!_legacy || !Directory.Exists(StorageRoot) || Directory.GetFileSystemEntries(StorageRoot).Length == 0) return;
            var aside = StorageRoot + ".before-saving-" + DateTime.Now.ToString("yyyyMMdd-HHmmss");
            CopyFolder(StorageRoot, aside);
            App.Log("[window] what the Module held, copied aside to " + aside);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { App.Record("backup", e); }
    }

    private static void CopyFolder(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from)) File.Copy(file, Path.Combine(to, Path.GetFileName(file)), true);
        foreach (var dir in Directory.GetDirectories(from)) CopyFolder(dir, Path.Combine(to, Path.GetFileName(dir)));
    }

    private void RememberSceneName(string json)
    {
        try
        {
            if (JsonNode.Parse(json)?["AudioSceneDescriptorsID"]?.GetValue<string>() is not { Length: > 0 } name || name == _sceneName) return;
            _sceneName = name; App.Log("[window] the scene open is now " + name);
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { }
    }

    // The sounds of a saved scene follow it: what is added to it, and is not taken out, is kept with it.
    private void KeepSavedMembers(string json)
    {
        if (_sceneName is not { } name || !_saved.Scenes.ContainsKey(name)) return;
        var members = SoundsOf(json).Select(s => s.Name).ToList();
        if (_saved.Scenes[name].SequenceEqual(members)) return;
        _saved.Scenes[name] = members; WriteSaved();
    }

    // THE REPORTS THAT ARE NOT ABOUT A SOUND LOADED: the list the purge asked for, and a scene saved or opened (by name). True when the report was taken.
    private string? _listId;
    private readonly List<JsonObject> _listed = [];
    private bool TakeReport(string json)
    {
        try
        {
            if (JsonNode.Parse(json) is not JsonObject report || report["UserCommandRS"] is not JsonObject rs) return false;
            if (_profileAsk is { } asked && (string?)report["UserCommandID"] == asked)
            {
                App.Log("[window] the answer to the Room acoustics request: " + json);
                var has = rs["ListedObjects"] is JsonArray some && some.OfType<JsonObject>().Any(o => (string?)o["Kind"] == "Acoustic Profile");
                if (!has) { _profileAsk = null; Dispatcher.Invoke(() => AnswerText.Text = "The room's acoustics could not be shown: " + ((string?)rs["Outcome"] ?? "no answer") + ". The answer is in the log."); return true; }
            }
            if (rs["ListedObjects"] is JsonArray found && found.OfType<JsonObject>().FirstOrDefault(o => (string?)o["Kind"] == "Acoustic Profile") is { } listed)
            {
                var profile = listed["AcousticProfile"] as JsonObject; var copy = (JsonObject?)profile?.DeepClone();
                App.Log("[window] the acoustic profile listed: " + (profile?.ToJsonString() ?? "none"));
                Dispatcher.Invoke(() => { _panel ??= new AcousticPanel(this); _panel.Show(copy); });
                return true;
            }
            if ((string?)report["UserCommandID"] is { } id && id == _listId)
            {
                App.Log("[window] a list came: " + (rs["ListedObjects"] as JsonArray)?.Count + " item(s), kinds " + string.Join(",", (rs["ListedObjects"] as JsonArray ?? []).Select(x => (string?)x?["Kind"]).Distinct()));
                if (rs["ListedObjects"] is JsonArray items) lock (_listed) _listed.AddRange(items.OfType<JsonObject>().Select(o => (JsonObject)o.DeepClone()));
                return true;
            }
            if (rs["Actions"] is JsonArray actions)
                foreach (var a in actions.OfType<JsonObject>().Where(a => (string?)a["Outcome"] == "Done"))
                    switch ((string?)a["Action"])
                    {
                        case "NamedScene" when (string?)a["ObjectID"] is { Length: > 0 } saved:
                            if (_sceneName is { } before && before != saved) _saved.Scenes.Remove(before);
                            _saved.Scenes[saved] = _sceneSounds.ToList(); _saved.Last = saved; WriteSaved();
                            App.Log("[window] scene saved: " + saved);
                            break;
                        case "ModifiedScene" when _panel is { IsVisible: true }:
                            AskProfile();
                            break;
                        case "OpenedScene" when (string?)a["ObjectID"] is { Length: > 0 } opened && _saved.Scenes.ContainsKey(opened):
                            _saved.Last = opened; WriteSaved();
                            break;
                        // PLAYED: the Module says what is to be played - the scene, or one sound in it - and this window, which has the loudspeakers, plays it.
                        case "DeliveredObject" when (string?)a["ObjectID"] is { Length: > 0 } played:
                            App.Log("[window] to be played: " + played);
                            var only = _sceneSounds.Contains(played) ? played : null;
                            Dispatcher.InvokeAsync(() => HearAsync(only));
                            break;
                    }
        }
        catch (Exception e) when (e is System.Text.Json.JsonException or InvalidOperationException) { }
        return false;
    }

    private static readonly string[] SceneKinds = ["Audio Scene", "Speech Scene", "Multimodal Scene"];

    // THE START: what the Module holds is listed; what is not in a saved scene (and no saved scene itself) is deleted - the scenes first, then the objects; then the scene saved last is opened.
    private async Task PurgeAndOpenAsync()
    {
        try
        {
            await Task.Delay(800);
            _listId = Guid.NewGuid().ToString("N")[..8];
            _soundIn.Give(MpaiJson.ToJson(new UserCommand { UserCommandID = _listId, UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandRQ = new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "All" } } }));
            _sceneCommand.Give(MpaiJson.ToJson(new UserCommand { UserCommandID = _listId, UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow), UserCommandRQ = new UserCommandRQ { ListedObjects = new ObjectListing { Kind = "Audio Scene" } } }));
            await Task.Delay(1800);
            List<JsonObject> items; lock (_listed) items = [.. _listed];
            var savedScenes = _saved.Scenes.Keys.ToHashSet(StringComparer.Ordinal);
            var savedSounds = _saved.Scenes.Values.SelectMany(v => v).ToHashSet(StringComparer.Ordinal);
            string? Name(JsonObject o) => (string?)o["ObjectID"];
            bool IsScene(JsonObject o) => SceneKinds.Contains((string?)o["Kind"] ?? "");
            var scenes = items.Where(IsScene).Select(Name).OfType<string>().Where(n => !savedScenes.Contains(n)).Distinct().ToList();
            var sounds = items.Where(o => !IsScene(o)).Select(Name).OfType<string>().Where(n => !savedScenes.Contains(n) && !savedSounds.Contains(n)).Distinct().ToList();
            App.Log($"[window] the session before left {scenes.Count} scene(s) and {sounds.Count} sound(s) that were not saved: deleted");
            foreach (var (names, port) in new[] { (scenes, _sceneCommand), (sounds, _soundIn) })
            {
                if (names.Count == 0) continue;
                port.Give(MpaiJson.ToJson(new UserCommand
                {
                    UserCommandID = Guid.NewGuid().ToString("N")[..8], UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow),
                    UserCommandRQ = new UserCommandRQ { DeletedObjects = new ObjectPlacements { Objects = names.Select(n => new ObjectPlacement { ObjectID = new ManagedObject { ObjectID = n } }).ToList() } }
                }));
                await Task.Delay(900);
            }
            // WHAT WAS DELETED IS NO LONGER SHOWN: the window showed the scene the Module held at the start, and the Module has none now - left on the screen, a scene that is not there is one the person
            // goes on working with (and cannot save).
            if (scenes.Count > 0 || sounds.Count > 0)
            {
                _lastScene = null; _sceneSounds = new HashSet<string>(StringComparer.Ordinal); _sceneDescription = "no sounds, no room";
                Dispatcher.Invoke(() => Room.Show(new List<SceneView.Sound>()));
            }
            // what was saved is kept, and opened only when the person asks for it
            WriteSaved();                                                     // from now on there is an index: what it names is kept
        }
        catch (Exception e) { App.Record("purge", e); }
        finally
        {
            _purging = false;
            List<Action> held; lock (_held) { held = [.. _held]; _held.Clear(); }
            foreach (var action in held) Dispatcher.Invoke(action);
        }
    }

    private void PlaceAtTheCentre(string name, bool learnFirst = false)
    {
        App.Log("[window] the sound " + name + " is placed at the centre");
        _ = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 4 && !_sceneSounds.Contains(name); attempt++)
            {
                await Task.Delay(attempt == 0 ? 700 : 1500);
                if (_sceneSounds.Contains(name)) return;
                if (learnFirst && attempt == 0) { Dispatcher.Invoke(() => Quiet("what have i loaded")); await Task.Delay(1500); }      // a sound just loaded is not known to the interface yet: it is told what the Module keeps first, then the sound is put at the centre
                Dispatcher.Invoke(() => Quiet($"put {name} at the centre"));      // a sound the Module knows is placed; one it does not know yet is not guessed at: it is asked again
            }
            await Task.Delay(1500);
            if (!_sceneSounds.Contains(name)) await TellAsync($"{name} is loaded. Tell me where to put it.");
        });
    }

    private int _quietPending; private DateTime _quietSince;
    private void Quiet(string words)
    {
        if (DateTime.UtcNow - _quietSince > TimeSpan.FromSeconds(5)) _quietPending = 0;          // an answer that never came is not waited for
        _quietPending++; _quietSince = DateTime.UtcNow;
        App.Log("[window] a command for the person, quietly: " + words);
        GiveText(words);
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => Submit();

    // THE MICROPHONE, ON OR OFF, UNMISTAKABLY: off, nothing it hears reaches the Module.
    // HOW THE PERSON TALKS TO HER: Voice or Text. Neither chosen (both red): both work. Voice chosen (blue): the microphone is on and the typing is disabled. Text chosen (blue): the microphone
    // is off and cannot be turned on, and the Voice button is disabled. The same button again gives both back.
    private bool _ready;
    private volatile bool _textOnly;                  // Text chosen: what she answers is written, never said
    private volatile HashSet<string> _sceneSounds = [];      // the sounds the scene has, by name, as the Module last said
    private void ModeButton_Click(object sender, RoutedEventArgs e)
    {
        TextModeButton.IsChecked = sender == TextModeButton;          // the one pressed is on, the other is off
        App.Log("[window] mode: " + (TextModeButton.IsChecked == true ? "text" : "voice"));
        ApplyMode();
    }

    // For a picture of the window in a mode: `--window-png <file> --mode voice|text`.
    internal void ShowMode(string mode)
    {
        _ready = true;
        TextModeButton.IsChecked = mode != "voice";
        ApplyMode();
    }

    private void Silence() { try { _avatar?.Silence(); } catch (Exception) { } try { _speaking?.Cancel(); } catch (Exception) { } }

    // Exactly one of Voice and Text is on (deep blue), the other off (red). Voice: the microphone is on, what she says is said, typing is disabled. Text: the microphone is off, she writes
    // and does not speak, typing works.
    // WARMING UP is for the voice (recogniser, voice): only when Voice is on, and only once; never in a scripted test, which starts at once.
    private bool _warmStarted;
    private void WarmUpIfVoice()
    {
        if (_warmStarted || App.Script is not null || !_ready || TextModeButton.IsChecked == true) return;
        _warmStarted = true; _ = WarmUpAsync();
    }

    private void ApplyMode()
    {
        var text = TextModeButton.IsChecked == true; var voice = !text;
        VoiceModeButton.IsChecked = voice;
        VoiceModeButton.Content = voice ? "Voice on" : "Voice off"; TextModeButton.Content = text ? "Text on" : "Text off";
        _textOnly = text;
        if (text) Silence();
        InputBox.IsEnabled = SendButton.IsEnabled = text || _naming is not null;          // Text: typing works from the start, before the Module is ready (what is typed waits and is answered when it is); a name asked for is always typed
        MicButton.IsChecked = voice; SetMicrophone(voice);
        if (text && InputBox.IsEnabled) InputBox.Focus();
        WarmUpIfVoice();
    }

    private void MicButton_Click(object sender, RoutedEventArgs e)
    {
        if (TextModeButton.IsChecked == true) { MicButton.IsChecked = false; return; }          // in Text the microphone stays off
        SetMicrophone(MicButton.IsChecked == true);
    }

    private void SetMicrophone(bool on)
    {
        _microphoneOn = on;
        MicButton.Content = _microphoneOn ? "Microphone on" : "Microphone off";
        App.Log("[window] microphone " + (_microphoneOn ? "on" : "off"));
        if (TextModeButton.IsChecked == true) SetStatus("Text: type and press Enter.");
        else if (!_microphoneOn) SetStatus("The microphone is off.");
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
            ApplyMode();
            InputBox.Text = Path.GetFileNameWithoutExtension(dialog.FileName); InputBox.SelectAll(); InputBox.Focus();
            _ = TellAsync("What do I call it? Type the name and press Enter.");
            var name = await _naming.Task;
            _naming = null;
            ApplyMode();
            InputBox.Clear();
            if (string.IsNullOrWhiteSpace(name)) return;
            LoadSound(name, bytes);
        }
        catch (Exception ex) { _naming = null; App.Record("load a sound", ex); SetStatus("The sound could not be loaded: " + ex.Message); }
    }

    // A sound with its name, sent to the Module in a User Command that acquires it.
    private void LoadSound(string name, byte[] bytes)
    {
        if (_purging) { lock (_held) _held.Add(() => LoadSound(name, bytes)); return; }          // a sound loaded while the old session is cleared would be cleared with it
        var sound = new BasicAudioObject { BasicAudioObjectID = name, DescrMetadata = name, BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(bytes))] };
        var command = new UserCommand
        {
            UserCommandID = Guid.NewGuid().ToString("N")[..8], UserCommandTime = SimpleTime.At(DateTimeOffset.UtcNow),
            UserCommandRQ = new UserCommandRQ { AcquiredObject = new ManagedObject { BasicAudioObject = sound } }
        };
        _loading = (command.UserCommandID!, name);
        WriteHint(name);
        _soundIn.Give(MpaiJson.ToJson(command));
        SetStatus($"The sound {name} is sent.");
    }

    // THE SCENE ON THE LOUDSPEAKERS: the Audio Scene Descriptors the Module gave, rendered as the listener hears them.
    private async void HearButton_Click(object sender, RoutedEventArgs e) => await HearAsync(null);

    // The scene as the listener hears it - or, with a name, the one sound of it that is named, where it is.
    private async Task HearAsync(string? only)
    {
        var scene = _lastScene;
        if (scene is null) { await TellAsync("There is nothing in the room to hear yet."); return; }
        if (only is not null) scene = OnlyMember(scene, only);
        try
        {
            SetStatus(only is null ? "Rendering the scene..." : $"Rendering {only}...");
            var deviceName = _deviceName;
            var wav = await Task.Run(() => new Mpai.SpatialAudio.AudioRendering(AppContext.BaseDirectory).Render(scene).Data);          // rendered as the scene says (its Acoustic Profile)
            if (wav is not { Length: > 0 }) { await TellAsync("The scene gave no sound."); return; }
            SetStatus(only is null ? "Playing the scene." : $"Playing {only}.");
            _hearing = new CancellationTokenSource(); var heardToken = _hearing.Token;
            await Task.Run(() =>
            {
                using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(wav));
                using var output = new NAudio.Wave.WaveOutEvent { DeviceNumber = Mpai.PhysicalLayer.LoudspeakerUnit.DeviceNumberOf(deviceName) };
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
        StopButton.Background = IdleStop;
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
        try { (_north as IDisposable)?.Dispose(); } catch { }
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

    // HOW LONG WHAT IS STORED IS KEPT is the Controller's: it is set where the Controller is started (--keep-until). The routes of the Controller API that carry a remote User Agent have no
    // SharedStorage_Keep, so the window does not ask.
    private void ApplyKeep() { }
    private bool _keptADate;

    // ---- showing and saying -----------------------------------------------------------------------------------------------------------------------

    private void SetStatus(string s) { App.Log("[status] " + s); Dispatcher.Invoke(() => StatusText.Text = s); }

    // An instruction is shown AND said; the step goes on when she has finished saying it.
    private int _textsToSpeak;
    // A NOISE THE RECOGNISER WROTE WORDS FOR, which the window does not show as heard: a caption of a sound (*snoring*, [Click]), a filler it makes up of silence ("you", "thank you for watching"), a lone
    // word of fewer than four letters. (The Module judges the same for itself; this is only what the window puts on the screen.)
    private static readonly HashSet<string> Fillers = new(StringComparer.OrdinalIgnoreCase)
    { "you", "thank you", "thanks", "thank you for watching", "thanks for watching", "bye", "bye bye", "okay", "ok", "um", "uh", "hmm", "mm", "the", "so", "yeah" };

    private static bool IsNoise(string text)
    {
        var trimmed = text.Trim().TrimEnd('.');
        if (trimmed.Length > 1 && ((trimmed[0] == '*' && trimmed[^1] == '*') || (trimmed[0] == '[' && trimmed[^1] == ']') || (trimmed[0] == '(' && trimmed[^1] == ')'))) return true;
        var plain = new string(text.Where(c => char.IsLetterOrDigit(c) || c == ' ' || c == (char)39).ToArray()).Trim();
        if (plain.Length == 0) return true;
        if (Fillers.Contains(plain)) return true;
        var words = plain.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length < 2 && plain.Length < 4;
    }

    private (string Id, string Name)? _loading;

    // THE NAMES OF THE SOUNDS are not given to the recogniser by a file: the recogniser is an AIM of the Module, and the User Agent has no way to it but the Controller API. (It was told them
    // through a file in the temporary folder; that was the User Agent reaching an AIM.) The Controller gives it the likely words it was started with.
    private void WriteHint(string? name = null) { }

    // What is said when the Module has answered a command that acquires a sound: that it is loaded and where to put it, or why it is not. Null if the report is not about that command.
    // The name the Module keeps the sound under, from its report: the ObjectID of the action that acquired it.
    internal static string? StoredNameOf(string json)
    {
        try
        {
            return ((JsonNode.Parse(json)?["UserCommandRS"]?["Actions"] as JsonArray)?.OfType<JsonObject>()
                .FirstOrDefault(a => (string?)a["Action"] == "AcquiredObject" && (string?)a["Outcome"] == "Done")?["ObjectID"])?.GetValue<string>();
        }
        catch (Exception) { return null; }
    }

    internal static string? LoadSaid(string json, string id, string name)
    {
        try
        {
            var report = JsonNode.Parse(json) as JsonObject;
            if (report?["UserCommandRS"] is not JsonObject rs) return null;
            if ((string?)report["UserCommandID"] is { } rid && rid != id && (string?)rs["UserCommandID"] != id) return null;
            if ((string?)rs["Outcome"] == "Done" || (string?)rs["Outcome"] == "Succeeded")
                return $"Loaded {name}, at the centre. Drag it, or tell me where.";
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
        // WRITTEN, NOT SPOKEN: the User Agent has no voice of its own. What is spoken is the speech the Module sends (ResponseSpeech), emitted by the ReplySpeech device.
        await Task.CompletedTask;
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
