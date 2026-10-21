using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;

using Microsoft.Win32;

using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.PhysicalLayer;   // CameraUnit
using Mpai.Aif.Api;
using Mpai.Mas.Client;
using Mpai.Rca;
using Mpai.Ua;
using Mpai.UaKit;
using Mpai.Wdl;

namespace MpaiRca;

// A REMOTE CLIENT APPLICATION WITH A FACE, AND NO APPLICATION.
//
// It holds the real-world edges - a microphone, a webcam, a screen and the avatar
// - and the MPAI-MAS client, and hands both to the interpreter. Which Modules
// run, which Ports they take and which they give, all come from the Workflow
// Description it is opened with. Nothing here names an application, and nothing
// here holds a model: everything that reasons is on the Service.
//
// The device registry is where the notation meets the machine. A workflow says
// "acquire UserSpeech (OSD-BSO-V1.5)" and nothing about a microphone; the entry
// for OSD-BSO is what knows. Adding a Data Type to what this client can handle is
// adding an entry, not changing a program.
public partial class MainWindow : Window
{
    private static readonly string AmdDir    = MpaiPaths.Amds;
    private static readonly string AssetsDir = MpaiPaths.Assets;

    // WHERE THE APPS COME FROM. The client is told a Service address and
    // nothing else; what it can run is whatever that Service offers.
    private static readonly string ServiceUrl =
        Environment.GetEnvironmentVariable("MPAI_MAS_SERVER") ?? "https://localhost:5005/";
    private static readonly string? ServiceToken =
        Environment.GetEnvironmentVariable("MPAI_MAS_TOKEN");

    private AvatarUaHost?         _avatar;
    private Workflow?             _workflow;
    private string?               _workflowPath;
    private CancellationTokenSource? _stopping;

    // THE APP MPAI-MAS IS RUNNING, if any. Stop ends that App and MPAI-MAS goes
    // on - it asks whether the person wants another. With no App running, Stop
    // ends MPAI-MAS itself.
    private CancellationTokenSource? _appStopping;
    private DateTime _appStoppedAt = DateTime.MinValue;

    // TYPING CLAIMS THE TURN. While the person may speak or type, the microphone
    // listens - and hears the keys. The first character typed ends the listening,
    // so the turn is the typed one.
    private CancellationTokenSource? _typingClaims;

    // THE LANGUAGE THE PERSON WILL SPEAK, once an App has asked for a Language
    // Selector. Captured speech is stamped with it, so the recogniser decodes that
    // language instead of guessing. Cleared when an App starts.
    private string? _sourceLanguage;
    private string  _lastFrom = "en";
    private string  _lastTo   = "it";

    // MPAI-MAS IS THE CLIENT'S OWN WORKFLOW. It is the container the Apps run in,
    // chosen before any App is: it is read from this client's Orchestration
    // folder, and the Service does not offer it as an App.
    private static readonly string MasWorkflowPath =
        Path.Combine(MpaiPaths.Root, "AIF", "UserAgent", "Orchestration", "MPAI-MAS.orch");
    private Task?                    _running;
    private TaskCompletionSource?    _awaiting;
    private string?                  _appId;
    private TaskCompletionSource<string?>? _choosing;
    private readonly Dictionary<string, RemoteControllerApi> _controllers = new(StringComparer.Ordinal);

    // What the workflow is waiting for the user to type, if anything.
    private TaskCompletionSource<string>? _typed;

    public MainWindow()
    {
        InitializeComponent();
        Loaded      += OnLoaded;
        // A DISCARDED TASK REPORTS NOTHING. Faulting before its first await,
        // an async method invoked with '_ =' vanishes without a trace.
        // NO BUTTON OPENS THE LIST. A client that holds no application shows what
        // the Service offers, always, and an App is started by naming it.
        // CHOOSING IS AN ACT, NOT A SELECTION. A list that runs an App the moment
        // a row is touched gives no chance to read the next line.
        // STOP ENDS THE APP THAT RUNS; ONLY A SECOND, LATER STOP ENDS THE LAUNCHER. A double click, or a second click made while the App is
        // still closing, must not take the launcher down with the App: Thalia would then have nothing to ask "another App?" with.
        StopButton.Click  += (_, _) =>
        {
            // WHEN THE LAUNCHER HAS ENDED (the person said no, or stopped it) THE BUTTON SAYS START, and starts it again: Thalia welcomes and offers the Apps.
            if (StopButton.Content as string == "Start")
            {
                StopButton.Content = "Stop";
                _stopping = new CancellationTokenSource();
                _ = RunAppAsync("MAS");
                return;
            }
            _avatar?.Silence();                                  // she stops speaking when the person stops the App
            if (_appStopping is { } app) { app.Cancel(); _appStoppedAt = DateTime.UtcNow; return; }
            if (DateTime.UtcNow - _appStoppedAt < TimeSpan.FromSeconds(4)) return;
            _stopping?.Cancel();
        };
        SendButton.Click  += (_, _) => SendTyped();
        TypedBox.KeyDown  += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) SendTyped(); };
        TypedBox.TextChanged += (_, _) => { if (_typed is not null && TypedBox.Text.Length > 0) try { _typingClaims?.Cancel(); } catch { } };

        // PRESENT WHILE OPEN, GONE WHEN CLOSED. The Service counts this client while
        // it hears from it; a request every 30 seconds keeps it counted, and closing
        // the window says goodbye.
        _ = KeepPresentAsync();
        Closing += (_, _) =>
        {
            try
            {
                using var leaving = new AppDirectory(ServiceUrl, ServiceToken, MasClientIdentity.Id, Collection);
                Task.Run(() => leaving.LeaveAsync()).Wait(TimeSpan.FromSeconds(2));
            }
            catch { }
        };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Status("starting the avatar...");
            _avatar = new AvatarUaHost(Web, Dispatcher, AmdDir, AssetsDir);
            await _avatar.InitAsync();
            // THE MICROPHONE IS OPENED AT START and stays open, keeping the last seconds it hears: a person who answers the moment she stops
            // speaking is not lost (the browser client does the same).
            try { Mpai.PhysicalLayer.RingMicrophone.EnsureOpen(); } catch (Exception ex) { Program.Record("microphone", ex); }
            await Task.Delay(TimeSpan.FromSeconds(1.5));

            // A workflow may be named on the command line, so that a shipped
            // client can be started on the one it is meant to run.
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) Load(args[1]);

            WorkflowText.Text = $"Service: {ServiceUrl}";
            Status("ready");

            // A CLIENT THAT HOLDS NO APPLICATION HAS NOTHING ELSE TO SHOW.
            // Asking the Service what it offers is the first thing it does.
            // THIS CLIENT RUNS AN APP LIKE ANY OTHER. MPAI as a Service welcomes the
            // person, offers what the Service has, runs what they choose and asks
            // whether they want another - and all of that is in its Workflow
            // Description, not in this executable.
            // AN APP OPENED DIRECTLY: "--app AMQ" runs that App, not MPAI-MAS.
            var direct = Array.IndexOf(args, "--app") is var at and >= 0 && at + 1 < args.Length ? args[at + 1] : null;

            _stopping = new CancellationTokenSource();
            await RunAppAsync(direct ?? "MAS");
        }
        catch (Exception fatal)
        {
            Program.Record("startup", fatal);
            Status("startup failed: " + fatal.Message);
        }
    }

    // ---- the workflow ------------------------------------------------------

    // THE CLIENT SPEAKS BEFORE IT HAS AN APPLICATION. It drives the MPAI-MAS
    // Module on the Service - start, offer the words at its Text Port and its
    // Avatar, ask for the Portable Avatar, present it, stop - which is exactly
    // what a workflow's welcome does. That this is possible with no App chosen
    // is the point: the client holds nothing but the means to render.
    //
    // It offers one Text datum and names no AIM. That the Module renders it with
    // Response and Scene Rendering, and that RSR sends it on to both Text-To-
    // Speech and Generative Face Description, is the Module's business.
    private const string MasModule = "1MAS-APP-V1.0-I01";

    // THE AVATAR THIS CLIENT HOLDS: the model it draws (Thalia, Ready Player Me),
    // by its identifier. A Module that renders with Response and Scene Rendering
    // takes it as its Avatar input, animates it and gives it a voice; the client
    // draws it with what comes back.
    private const string AvatarModel = "cav-avatar.glb";

    private Task SpeakWelcomeAsync() =>
        SpeakAsync("Welcome to MPAI as a Service. Select an app and enjoy.");

    // THE LAST THING THE MICROPHONE HEARD, kept as Test/Reports/mic-last.wav (16 kHz, 16 bit, mono) so that what the recogniser was given
    // can be listened to and tried: when a spoken answer is not understood, the first question is whether the audio was any good.
    private static void KeepHeard(byte[] pcm)
    {
        try
        {
            var path = Path.Combine(MpaiPaths.Root, "Test", "Reports", "mic-last.wav");
            using var w = new BinaryWriter(File.Create(path));
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write(36 + pcm.Length);
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16); w.Write((short)1); w.Write((short)1);
            w.Write(16000); w.Write(32000); w.Write((short)2); w.Write((short)16);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write(pcm.Length); w.Write(pcm);
            SpeakLog($"mic: kept {pcm.Length} bytes ({pcm.Length / 32000.0:0.0} s) in Test/Reports/mic-last.wav");
        }
        catch { }
    }

    // WHAT WAS SAID, IN WORDS: the speech goes to the launcher Module, whose recogniser gives the text.
    private async Task<string?> WordsOfAsync(BasicSpeechObject speech)
    {
        try
        {
            using var north = new RemoteControllerApi(ServiceUrl, ServiceToken);
            if (north.StartFlow(MasModule) != AifError.OK) return null;
            var said = await Task.Run(() => north.Advance(MasModule, new List<ControllerApi.Datum>
            {
                new ControllerApi.Datum("OSD-BSO-V1.5", 1, MpaiJson.ToJson(speech))
            }));
            north.StopFlow(MasModule);
            if (!said.Ok || said.ByType("OSD-BTO-V1.5") is not { } json) return null;
            var words = MpaiJson.FromJson<BasicTextObject>(json)?.GetText();
            SpeakLog($"heard: {words}");
            return words;
        }
        catch (Exception ex) { Program.Record("heard", ex); return null; }
    }

    private static void SpeakLog(string line)
    {
        try { File.AppendAllText(Program.CrashLog, $"{DateTime.Now:HH:mm:ss}  {line}{Environment.NewLine}"); } catch { }
    }

    // THE VOICES THAT SPEAK: English, French, German, Spanish, Portuguese and Italian. The Japanese and Chinese voices are
    // configured but do not speak (they read "Japanese character"), so a text in those is shown and not spoken.
    private static readonly HashSet<string> SpokenLanguages = new(StringComparer.OrdinalIgnoreCase) { "en", "fr", "de", "es", "pt", "it" };

    // The words as the Text Object the Module speaks: in their language when it is known, which chooses the voice.
    private static BasicTextObject WordsIn(string words, string? language) =>
        BasicTextObject.FromText(words, language is null || !SpokenLanguages.Contains(language) ? null : new TextQualifier
        {
            TextQualifierID = Guid.NewGuid().ToString(),
            Attributes = new TextAttributes { Language = new Language { LanguageCode = language.ToLowerInvariant(), LanguageFormat = LanguageFormat.Iso639_1 } }
        });

    private async Task SpeakAsync(string words, string? language = null)
    {
        SpeakLog($"speak ({language ?? "no language"}): " + (words.Length > 60 ? words[..60] + "..." : words));
        try
        {
            using var north = new RemoteControllerApi(ServiceUrl, ServiceToken);
            var started = north.StartFlow(MasModule);
            if (started != AifError.OK) { SpeakLog($"speak: the launcher Module did not start ({started})"); return; }

            var said = await Task.Run(() => north.Advance(MasModule, new List<ControllerApi.Datum>
            {
                new ControllerApi.Datum("OSD-BTO-V1.5", 1, MpaiJson.ToJson(WordsIn(words, language))),
                new ControllerApi.Datum("OSD-B3O-V1.5", 1, AvatarUtterance.AvatarDatum(AvatarModel))
            }));
            north.StopFlow(MasModule);

            if (!said.Ok) { SpeakLog($"speak: the launcher Module answered {said.Error}"); return; }

            // The Portable Avatar: the speech, and the face and body that move with it.
            if (AvatarUtterance.FromPortableAvatar(said.ByType("PAF-PAV-V1.6")) is not { } utterance) { SpeakLog("speak: the answer carries no speech"); return; }

            await _avatar!.PresentAsync(utterance);
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(utterance.MachineSpeechWav) + 0.8));
        }
        catch (Exception ex)
        {
            Program.Record("welcome", ex);   // a silent welcome is not a reason to stop
        }
    }

    // THE APPS THIS SERVICE OFFERS. The client knows an address and nothing
    // else: what it can run is whatever is there, and a Service that offers
    // none is answering rather than failing.
    private sealed record Offered(string Name, string Description, object? Icon, AppDirectory.App App);

    // THE COLLECTION THIS CLIENT USES, from MPAI_MAS_COLLECTION; none means the
    // Service's default offer. Only the Apps it shows depend on it.
    private static readonly string? Collection =
        Environment.GetEnvironmentVariable("MPAI_MAS_COLLECTION") is { Length: > 0 } c ? c.Trim() : null;

    private const string AllCategories = "All categories";
    private bool _fillingCategories;
    private Offered? _detail;

    private async Task<List<Offered>> BuildOfferedAsync(IEnumerable<AppDirectory.App> apps, AppDirectory directory)
    {
        var shown = new List<Offered>();
        // MPAI-MAS IS NOT AN APP. It is the container the Apps run in; the client
        // fetches it by name and does not offer it to the person.
        foreach (var a in apps.Where(a => !string.Equals(a.Id, "MAS", StringComparison.OrdinalIgnoreCase)))
        {
            object? icon = null;
            var bytes = await directory.IconAsync(a);
            if (bytes is { Length: > 0 })
            {
                try
                {
                    var image = new System.Windows.Media.Imaging.BitmapImage();
                    image.BeginInit();
                    image.StreamSource = new MemoryStream(bytes);
                    image.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                    image.EndInit();
                    icon = image;
                }
                catch { /* an App without a picture is still an App */ }
            }
            shown.Add(new Offered(a.Name, a.Description, icon, a));
        }
        return shown;
    }

    // SEARCH: the words in the box and the category chosen, among this client's Apps.
    private async Task FilterAsync()
    {
        try
        {
            using var directory = new AppDirectory(ServiceUrl, ServiceToken, MasClientIdentity.Id, Collection);
            var words    = SearchBox.Text.Trim();
            var category = CategoryBox.SelectedItem as string;
            if (category == AllCategories) category = null;
            var apps = words.Length == 0 && category is null
                ? await directory.ListAsync()
                : await directory.SearchAsync(words, category);
            var shown = await BuildOfferedAsync(apps, directory);
            AppList.ItemsSource = shown;
            Status(shown.Count == 0 ? "no App matches" : $"{shown.Count} App(s) found");
        }
        catch (Exception ex) { Status("the search failed: " + ex.Message); }
    }

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Enter) _ = FilterAsync();
    }

    private void CategoryBox_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (!_fillingCategories) _ = FilterAsync();
    }

    // AN APP'S PAGE, before it starts.
    private async Task ShowDetailAsync(Offered chosen)
    {
        _detail = chosen;
        DetailName.Text        = chosen.Name;
        DetailDescription.Text = chosen.Description;
        DetailFacts.Text = DetailAsks.Text = DetailKeeps.Text = "";
        AppDetail.Visibility   = Visibility.Visible;
        try
        {
            using var directory = new AppDirectory(ServiceUrl, ServiceToken, MasClientIdentity.Id, Collection);
            var d = await directory.DescriptorAsync(chosen.App.Id);
            if (d is null || _detail != chosen) return;
            var facts = new List<string>();
            if (d.Standard.Length > 0) facts.Add(d.Standard);
            if (d.Version.Length > 0)  facts.Add("version " + d.Version);
            if (d.Languages.Count > 0) facts.Add("languages: " + string.Join(", ", d.Languages));
            DetailFacts.Text = string.Join("  -  ", facts);
            if (d.Asks.Count > 0)   DetailAsks.Text  = "It will ask for: " + string.Join(", ", d.Asks) + ".";
            if (d.Keeps.Length > 0) DetailKeeps.Text = "What it keeps: " + d.Keeps;
        }
        catch { /* the page without its details is still the page */ }
    }

    private void OpenApp_Click(object sender, RoutedEventArgs e)
    {
        if (_detail is not { } chosen) return;
        _detail = null;
        AppDetail.Visibility = Visibility.Collapsed;
        Status($"chose {chosen.Name}");

        // CHOOSING COMPLETES AN ACQUISITION. A workflow asked for the name of an
        // Application; this is the person answering it.
        if (_choosing is { } waiting) { _choosing = null; waiting.TrySetResult(chosen.App.Id); return; }

        _appId = chosen.App.Id;
        _ = ChosenAsync(chosen);
    }

    private void CloseDetail_Click(object sender, RoutedEventArgs e)
    {
        _detail = null;
        AppDetail.Visibility = Visibility.Collapsed;
    }

    private async Task ShowAppsAsync()
    {
        try
        {
            Status("asking the Service what it offers...");
            using var directory = new AppDirectory(ServiceUrl, ServiceToken, MasClientIdentity.Id, Collection);
            var apps = await directory.ListAsync();

            if (apps.Count == 0)
            {
                AppPanelHint.Text = "This Service offers no Apps. Open a Workflow Description from a file instead.";
                AppList.ItemsSource = null;
                ShowPanel(true); AppColumn.Width = new GridLength(320);
                Status("no Apps offered");
                return;
            }

            var shown = await BuildOfferedAsync(apps, directory);

            _fillingCategories = true;
            CategoryBox.ItemsSource   = new[] { AllCategories }.Concat(await directory.CategoriesAsync()).ToList();
            CategoryBox.SelectedIndex = 0;
            _fillingCategories = false;

            AppPanelHint.Text   = "These are offered by the Service. This client holds none of them.";
            AppList.ItemsSource = shown;
            ShowPanel(true); AppColumn.Width = new GridLength(320);
            Status($"{apps.Count} Apps offered");
        }
        catch (Exception ex)
        {
            Program.Record("apps", ex);
            AppPanelHint.Text   = $"The Service could not be reached: {ex.Message}";
            AppList.ItemsSource = null;
            ShowPanel(true); AppColumn.Width = new GridLength(320);
            Status("the Service could not be reached");
        }
    }

    // AN APP OBTAINED FROM A SERVICE AND ONE OPENED FROM A FILE ARE THE SAME
    // THING. Both are read by the same reader, and nothing downstream can tell
    // which it was given.
    private async Task ChosenAsync(Offered chosen)
    {
        // ONE APP AT A TIME. Choosing a second while the first is still in its loop
        // left both running: two workflows prompting, two listening, and a file
        // dialog appearing in the middle of another App's conversation.
        if (_running is { IsCompleted: false })
        {
            Status("stopping the App that is running...");
            _stopping?.Cancel();
            try { await _running; } catch { /* it was asked to stop */ }
        }


        try
        {
            Status($"fetching {chosen.Name}...");
            using var directory = new AppDirectory(ServiceUrl, ServiceToken, MasClientIdentity.Id, Collection);
            var text = await directory.WorkflowAsync(chosen.App);

            _workflow = new WorkflowReader().Read(text);
            _workflowPath = chosen.App.WorkflowPath;
            WorkflowText.Text =
                $"{chosen.Name}   \u2014   workflow {_workflow.Name} over {string.Join(", ", _workflow.Modules)}   \u2014   from {ServiceUrl}";
            // CHOOSING AN APP IS STARTING IT. A person who has picked what they want
            // should not then have to announce it; the choice is the instruction.
            InstructionText.Text  = "";
            // THE LIST STAYS. A client that holds no application shows what the
            // Service offers for as long as it is running; choosing one App does
            // not hide the others. AppColumn.Width = new GridLength(0);
            Status("App obtained");
            _running = RunAsync();
            await _running;
        }
        catch (Exception ex)
        {
            Program.Record("fetch", ex);
            AppPanelHint.Text = $"{chosen.Name} could not be obtained: {ex.Message}";
            Status("the App could not be obtained");
        }
    }

    // THE AVATAR KEEPS HER SIZE. The window grows to make room for the list
    // rather than the list taking room from her.
    private const double PanelWidth = 380;

    private void ShowPanel(bool show)
    {
        if (show && AppPanel.Visibility == Visibility.Visible) return;
        if (!show && AppPanel.Visibility == Visibility.Collapsed) return;

        AppPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AppColumn.Width     = new GridLength(show ? PanelWidth : 0);
        Width              += show ? PanelWidth + 12 : -(PanelWidth + 12);
    }

    // CHOOSING OPENS THE APP'S PAGE, and Open starts it: a person sees what an App
    // will ask for and what it keeps before it starts.
    private void AppList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AppList.SelectedItem is not Offered chosen) return;
        AppList.SelectedItem = null;
        Status($"chose {chosen.Name}");

        // CHOOSING COMPLETES AN ACQUISITION. A workflow asked for the name of an
        // Application; this is the person answering it.
        if (_choosing is { } waiting) { _choosing = null; waiting.TrySetResult(chosen.App.Id); return; }

        _appId = chosen.App.Id;
        _ = ChosenAsync(chosen);
    }

    private void RunApp_Click(object sender, System.Windows.RoutedEventArgs e)
    {
        // THE CONTEXT MAY SIT ANYWHERE ABOVE THE LINK. A Hyperlink inside a
        // TextBlock does not always inherit the row's DataContext, so the row is
        // found rather than assumed.
        var chosen = (sender as System.Windows.FrameworkContentElement)?.DataContext as Offered
                  ?? (sender as System.Windows.FrameworkElement)?.DataContext as Offered
                  ?? AppList.Items.OfType<Offered>().FirstOrDefault(o =>
                         o.Name == ((sender as System.Windows.Documents.Hyperlink)?.Inlines
                             .OfType<System.Windows.Documents.Run>().FirstOrDefault()?.Text));

        if (chosen is null) { Status("the App could not be identified"); return; }
        Status($"chose {chosen.Name}");
        _ = ChosenAsync(chosen);
    }

    private void ChooseWorkflow()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Workflow Description",
            Filter = "Workflow Description (*.orch)|*.orch|All files (*.*)|*.*",
            InitialDirectory = Path.Combine(MpaiPaths.Root, "Apps")
        };
        if (dialog.ShowDialog() == true) Load(dialog.FileName);
    }

    private void Load(string path)
    {
        try
        {
            _workflow     = new WorkflowReader().Read(File.ReadAllText(path));
            _workflowPath = path;
            WorkflowText.Text =
                $"{Path.GetFileName(path)}   \u2014   workflow {_workflow.Name} over {string.Join(", ", _workflow.Modules)}";
            InstructionText.Text = "";
            Status("workflow read");
        }
        catch (Exception ex)
        {
            // A workflow that will not read fails here, where it is being read,
            // and not later where the consequence would show.
            WorkflowText.Text = Path.GetFileName(path) + " \u2014 " + ex.Message;
            Status("the workflow could not be read");
        }
    }

    private async Task RunAsync()
    {
        if (_workflow is null) return;

        // ONE SERVICE, ONE ADDRESS. This read the environment again and fell back
        // to a different port from the one the catalogue and the welcome use, so a
        // client could list Apps from one Service and try to run them on another.

        StopButton.IsEnabled  = true;
        _stopping = new CancellationTokenSource();

        try
        {
            // ONE CONTROLLER PER APP, KEPT. Stopping a Module releases the Module;
            // the Controller keeps the models its AIMs loaded, so returning to an App
            // already tried does not load them again. The Controller Instances are
            // released when this client closes.
            if (!_controllers.TryGetValue(_appId!, out var north))
                _controllers[_appId!] = north = new RemoteControllerApi(ServiceUrl, ServiceToken);

            var interpreter = new WorkflowInterpreter(north.Async(), Devices(), Status);
            await interpreter.RunAsync(_workflow, _stopping.Token);
            Status("the workflow finished");
        }
        catch (Exception ex)
        {
            Program.Record("workflow", ex);
            Status("stopped: " + ex.Message);
        }
        finally
        {
            // BACK TO REST. An App that has ended leaves the client as it was
            // before one was chosen: a client that holds no application shows what
            // the Service offers, and a person who has finished with one App can
            // choose another without restarting anything.
            //
            // The stage and the text box are cleared with it: what the last App was
            // working with is not what the next one is.
            if (_running is null || _running.IsCompleted)
                await Dispatcher.InvokeAsync(() =>
                {
                    _workflow            = null;
                    _appId               = null;
                    WorkflowText.Text    = $"Service: {ServiceUrl}";
                    InstructionText.Text = "";
                    StageImage.Source    = null;
                    StageTitle.Text      = "No image displayed";
                    StageText.Text       = "";
                    TypedBox.Text        = "";
                    AppList.SelectedItem = null;
                });

            // ONLY IF NOTHING ELSE HAS STARTED. Choosing a second App cancels the
            // first and starts the next; the first's cleanup would otherwise put out
            // the Stop button the second had just lit, leaving a running App with
            // no way to end it.
            if (_running is null || _running.IsCompleted)
                StopButton.IsEnabled = false;
            TypedBox.IsEnabled = SendButton.IsEnabled = false;
        }
    }
    // ---- the devices -------------------------------------------------------

    // WHAT FORMAT THE REQUEST ASKED FOR, if it said. The request is the
    // Qualifier's own JSON, so the field is read where the schema puts it and
    // nothing is invented around it.
    private string? _lastPicture;

    // The format a file is, by its extension - which is how a Visual Object
    // Acquisition states what it has read.
    private static string FormatOf(string path) =>
        Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" => "JPEG",
            ".png"            => "PNG",
            ".bmp"            => "BMP",
            _                  => ""
        };

    private static string? FormatWanted(string? qualifierJson)
    {
        if (string.IsNullOrWhiteSpace(qualifierJson)) return null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(qualifierJson);
            if (doc.RootElement.TryGetProperty("Formats", out var f) &&
                f.TryGetProperty("Content", out var c) &&
                c.TryGetProperty("2D", out var d) &&
                d.TryGetProperty("Static", out var s))
                return s.GetString();
        }
        catch { /* a request that will not parse asks for nothing in particular */ }
        return null;
    }

    private void ShowStage(string pane)
    {
        var width = pane.ToLowerInvariant() switch
        {
            "none"   => 0,
            "wide"   => 560,
            _        => 360        // normal, and anything this client does not know
        };
        StagePanel.Visibility = width > 0 ? Visibility.Visible : Visibility.Collapsed;
        StageColumn.Width     = new GridLength(width);
    }

    private void ShowApps(bool show)
    {
        AppPanel.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AppColumn.Width     = new GridLength(show ? 380 : 0);
    }

    // AN APP RUNS UNDER A CONTROLLER OF ITS OWN. One Controller, one Module: the
    // Controller that speaks for this client is not the Controller that runs an
    // App, and an App that fails cannot take the client with it.
    //
    // The Controller is kept after the App ends, so returning to an App already
    // tried does not load its models again.
    private async Task RunAppAsync(string appId)
    {
        try
        {
            Status($"obtaining {appId}...");
            using var directory = new AppDirectory(ServiceUrl, ServiceToken, MasClientIdentity.Id, Collection);
            // A Service holds Apps it does not offer, so the App is fetched by name
            // rather than looked for in the catalogue. Its name and the room it wants
            // are taken from the catalogue when it is there.
            var offered = await directory.ListAsync();
            var app     = offered.FirstOrDefault(a => a.Id == appId);

            var text = appId == "MAS"
                ? await File.ReadAllTextAsync(MasWorkflowPath)
                : await directory.WorkflowAsync(appId);
            var workflow = new WorkflowReader().Read(text);
            if (appId != "MAS") _sourceLanguage = null;

            if (!_controllers.TryGetValue(appId, out var north))
                _controllers[appId] = north = new RemoteControllerApi(ServiceUrl, ServiceToken);

            // THE ROOM AN APP ASKED FOR. An App that shows nothing gets no pane; one
            // that shows a picture gets it wide. The App says so in its manifest and
            // the Service carries it in the catalogue: how much room a thing needs to
            // be seen is the App's business, and belongs nowhere in its workflow.
            var pane = app?.Pane ?? "none";
            await Dispatcher.InvokeAsync(() =>
            {
                WorkflowText.Text = $"{app?.Name ?? appId}   \u2014   {ServiceUrl}";
                StopButton.IsEnabled = true;
                if (appId == "MAS") StopButton.Content = "Stop";
                ShowStage(pane);
            });

            var interpreter = new WorkflowInterpreter(north.Async(), Devices(), Status);
            using var appStop = appId == "MAS" ? null : new CancellationTokenSource();
            _appStopping = appStop;
            try     { await interpreter.RunAsync(workflow, appStop?.Token ?? _stopping?.Token ?? default); }
            finally
            {
                _avatar?.Silence();            // what she was still saying of the App is cut off before she asks her own question
                _appStopping = null;
                // THE LANGUAGE AN APP WAS GIVEN IS THAT APP'S: what the person says to
                // MPAI-MAS after it is in no language chosen for it.
                if (appId != "MAS") _sourceLanguage = null;
            }
            Status($"{app?.Name ?? appId} finished");
        }
        catch (Exception ex)
        {
            Program.Record("app", ex);
            Status($"{appId}: {ex.Message}");
        }
        finally
        {
            await Dispatcher.InvokeAsync(() =>
            {
                WorkflowText.Text    = $"Service: {ServiceUrl}";
                InstructionText.Text = "";
                StageImage.Source    = null;
                StageTitle.Text      = "No image displayed";
                StageText.Text       = "";
                TypedBox.Text        = "";
                ShowStage("none");
                if (appId == "MAS") { StopButton.Content = "Start"; StopButton.IsEnabled = true; }   // the launcher ended: Start begins it again
            });
        }
    }

    private bool _videoReady;

    // THE WINDOW GIVEN TO THE VIDEO: borderless, maximised, with the stage alone in it; everything else is put back as it was.
    private bool _videoFull;
    private WindowStyle _savedStyle; private WindowState _savedState; private SizeToContent _savedSize;
    private double _savedAvatar, _savedApp, _savedStage;
    private Thickness _savedMargin, _savedStageMargin, _savedDock;
    private readonly List<UIElement> _hidden = new();

    private void VideoFullScreen(bool on)
    {
        if (on == _videoFull) return;
        _videoFull = on;
        if (on)
        {
            _savedStyle = WindowStyle; _savedState = WindowState; _savedSize = SizeToContent;
            _savedAvatar = AvatarColumn.Width.Value; _savedApp = AppColumn.Width.Value; _savedStage = StageColumn.Width.Value;
            _savedMargin = RootGrid.Margin; _savedStageMargin = StagePanel.Margin; _savedDock = StageDock.Margin;
            _hidden.Clear();
            foreach (UIElement e in RootGrid.Children)
                if (System.Windows.Controls.Grid.GetRow(e) != 1 && e.Visibility == Visibility.Visible) { _hidden.Add(e); e.Visibility = Visibility.Collapsed; }
            SizeToContent = SizeToContent.Manual;
            WindowStyle = WindowStyle.None; WindowState = WindowState.Maximized;
            RootGrid.Margin = new Thickness(0); StagePanel.Margin = new Thickness(0); StageDock.Margin = new Thickness(0);
            AvatarColumn.Width = new GridLength(0); AppColumn.Width = new GridLength(0); StageColumn.Width = new GridLength(1, GridUnitType.Star);
            StageTitle.Visibility = Visibility.Collapsed; StageText.Visibility = Visibility.Collapsed; StageVideoFull.Content = "Exit full screen";
        }
        else
        {
            StageTitle.Visibility = Visibility.Visible; StageText.Visibility = Visibility.Visible;
            StageVideoFull.Content = "Full screen";
            AvatarColumn.Width = new GridLength(_savedAvatar); AppColumn.Width = new GridLength(_savedApp); StageColumn.Width = new GridLength(_savedStage);
            RootGrid.Margin = _savedMargin; StagePanel.Margin = _savedStageMargin; StageDock.Margin = _savedDock;
            foreach (var e in _hidden) e.Visibility = Visibility.Visible;
            _hidden.Clear();
            WindowState = _savedState; WindowStyle = _savedStyle; SizeToContent = _savedSize;
        }
    }

    // PLAYS A VIDEO ON THE STAGE, and returns when it has played to its end, the person has closed it, it could not be played, or Stop was
    // pressed. A YouTube link is played by the YouTube player in stage-video.html; any other link by the page's video element.
    private async Task PlayVideoAsync(string uri)
    {
        var done = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        void onMessage(object? s, Microsoft.Web.WebView2.Core.CoreWebView2WebMessageReceivedEventArgs e)
        {
            try { done.TrySetResult(e.TryGetWebMessageAsString()); } catch { done.TrySetResult("error:message"); }
        }
        void onClose(object? s, RoutedEventArgs e) => done.TrySetResult("closed");
        void onFull(object? s, RoutedEventArgs e) => VideoFullScreen(!_videoFull);

        await Dispatcher.InvokeAsync(async () =>
        {
            if (!_videoReady)
            {
                var folder = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "MpaiUaKit", "stage-video");
                Directory.CreateDirectory(folder);
                var env = await Microsoft.Web.WebView2.Core.CoreWebView2Environment.CreateAsync(null, folder,
                    new Microsoft.Web.WebView2.Core.CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
                await StageVideo.EnsureCoreWebView2Async(env);
                // THE PLAYER'S FULL SCREEN button: the page fills the control only, so the window itself is made to fill the screen.
                StageVideo.CoreWebView2.ContainsFullScreenElementChanged += (_, _) =>
                    Dispatcher.Invoke(() => VideoFullScreen(StageVideo.CoreWebView2.ContainsFullScreenElement));
                StageVideo.CoreWebView2.SetVirtualHostNameToFolderMapping("stagevideo.local", AssetsDir,
                    Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
                _videoReady = true;
            }
            StageVideo.CoreWebView2.WebMessageReceived += onMessage;
            StageVideoClose.Click += onClose;
            StageVideoFull.Click += onFull;
            StageImage.Visibility = Visibility.Collapsed;
            StageVideo.Visibility = Visibility.Visible;
            StageVideoButtons.Visibility = Visibility.Visible;
            StageTitle.Text = "About MPAI";
            StageText.Text = "";
            var id = AudioVisualLink.YouTubeId(uri);
            StageVideo.CoreWebView2.Navigate(id is not null
                ? $"https://stagevideo.local/stage-video.html?yt={id}"
                : $"https://stagevideo.local/stage-video.html?src={Uri.EscapeDataString(uri)}");
        }).Task.Unwrap();

        var stop = (_appStopping ?? _stopping)?.Token ?? CancellationToken.None;
        using var reg = stop.Register(() => done.TrySetResult("stopped"));
        var how = await done.Task;
        Status($"video: {how}");

        await Dispatcher.InvokeAsync(() =>
        {
            StageVideo.CoreWebView2.WebMessageReceived -= onMessage;
            StageVideoClose.Click -= onClose;
            StageVideoFull.Click -= onFull;
            VideoFullScreen(false);
            try { StageVideo.CoreWebView2.Navigate("about:blank"); } catch { }
            StageVideo.Visibility = Visibility.Collapsed;
            StageVideoButtons.Visibility = Visibility.Collapsed;
            StageImage.Visibility = Visibility.Visible;
            StageTitle.Text = "No image displayed";
        });
        // The video's sound reached the microphone from the loudspeakers: it is not taken for an answer.
        Mpai.PhysicalLayer.RingMicrophone.IgnoreBefore = DateTime.UtcNow.AddMilliseconds(600);
        if (how.StartsWith("error", StringComparison.Ordinal))
            await SpeakAsync("I could not play the video.");
    }

    private DeviceRegistry Devices()
    {
        var devices = new DeviceRegistry();

        // THE BUTTON TAKES ITS WORD FROM THE APP. A workflow that says
        // await "Ask" turns Start into Ask and waits: only the App knows what
        // the person is about to be invited to do.
        // 'await "<word>"' has no button in this client: choosing an App starts
        // it, and the workflows here wait on the microphone rather than on a
        // press. The verb stands; nothing in this client answers it yet.

        // SPEECH, FROM THE MICROPHONE. The capture returns the Object that Speech
        // Object Acquisition built, Qualifier and all: the sampling frequency and
        // the precision the device determined. Taking the bytes out and rebuilding
        // is what four User Agents did until this week, and it is why the voice
        // half of every enrolment failed in silence.
        devices.RegisterAcquire("OSD-BSO-V1.5", async (viaVad, wanted, abandon) =>
        {
            Instruct("Speak when you are ready.");
            // STOP DOES NOT WAIT FOR THE MICROPHONE. A capture ends only when the
            // person stops speaking; a Stop pressed while listening is answered at
            // once, and the capture is left to end on its own, its words dropped.
            var capture = Task.Run(() => _avatar!.CaptureSpeech());
            var stop    = (_appStopping ?? _stopping)?.Token ?? CancellationToken.None;
            // ...nor, when the person typed instead, for the speech that did not come.
            using var claims = new CancellationTokenSource();
            _typingClaims = claims;
            using var either = CancellationTokenSource.CreateLinkedTokenSource(stop, abandon, claims.Token);
            if (await Task.WhenAny(capture, Task.Delay(Timeout.Infinite, either.Token)) != capture)
                return null;
            var speech = await capture;
            if (speech is null || speech.Data.Length == 0) return null;
            KeepHeard(speech.Data);
            if (_sourceLanguage is { } language) speech = WithLanguage(speech, language);
            return MpaiJson.ToJson(speech);
        });

        // THE LANGUAGES, CHOSEN BY THE PERSON. A Language Selector names two: the
        // one the person will speak or write, and the one the answer is to be in.
        // Both are asked for together and returned as one datum.
        devices.RegisterAcquire("OSD-SEL-V1.5", async (_, _) =>
        {
            Instruct("Choose the input and output languages.");
            var chosen = await Dispatcher.InvokeAsync(ChooseLanguages);
            if (chosen is not { } pair) return null;
            _sourceLanguage = pair.From;
            return MpaiJson.ToJson(BasicSelectorObject.Languages(pair.From, pair.To));
        });

        // THE NAME OF AN APPLICATION, CHOSEN BY THE PERSON. A Text Object whose
        // Qualifier says its role is an App name: the User Agent shows what the
        // Service offers and returns the name of the one chosen. The workflow says
        // what it wants; how a person is asked is the User Agent's own affair.
        devices.RegisterAcquire("OSD-BTO-V1.5", async (_, wanted, abandon) =>
        {
            // HOW MANY ARE HERE. Asked for a Text whose role is Concurrency, the client
            // asks the Service how many clients are using it now. With others present
            // it answers a sentence saying so; alone, or when the Service does not
            // say, it answers nothing, and the workflow says nothing.
            if ((wanted ?? "").Contains("Concurrency", StringComparison.OrdinalIgnoreCase))
            {
                using var status = new AppDirectory(ServiceUrl, ServiceToken, MasClientIdentity.Id, Collection);
                var count = await status.ActiveClientsAsync();
                return count is int n && n > 1
                    ? MpaiJson.ToJson(BasicTextObject.FromText($"You are the {Ordinal(n)} concurrent user of the MPAI as a Service App."))
                    : null;
            }

            // A YES OR A NO, SPOKEN OR TYPED. Asked for a Text whose role is YesNo, the client listens and opens the text box, and gives
            // whichever comes first. What is spoken is turned into words by the Module that gives this client its ears (the launcher's);
            // an App that has no recogniser of its own asks its question this way.
            if ((wanted ?? "").Contains("YesNo", StringComparison.OrdinalIgnoreCase))
            {
                Instruct("Say yes or no, or type it and press Enter.");
                var stop  = (_appStopping ?? _stopping)?.Token ?? CancellationToken.None;
                using var either = CancellationTokenSource.CreateLinkedTokenSource(stop, abandon);
                var typed = TypedAsync();
                using var onStop    = stop.Register(() => Dispatcher.Invoke(CancelTyped));
                using var onAbandon = abandon.Register(() => Dispatcher.Invoke(CancelTyped));
                var heard = Task.Run(async () =>
                {
                    while (!either.IsCancellationRequested)
                    {
                        var speech = await Task.Run(() => _avatar!.CaptureSpeech());
                        if (either.IsCancellationRequested) break;
                        if (speech is null || speech.Data.Length == 0) continue;
                        var text = await WordsOfAsync(speech);
                        if (!string.IsNullOrWhiteSpace(text)) return text;
                    }
                    return (string?)null;
                });
                var first = await Task.WhenAny(typed, heard);
                either.Cancel();
                await Dispatcher.InvokeAsync(CancelTyped);
                var said = await first;
                return string.IsNullOrWhiteSpace(said) ? null : MpaiJson.ToJson(BasicTextObject.FromText(said.Trim()));
            }

            // TEXT THE PERSON TYPES. Any Text Object asked for without the role of an
            // App name is typed: the text box opens, and Enter - the key or the
            // button - gives it. When the workflow waits for speech or text,
            // whichever comes first, speaking closes the box again.
            if (!(wanted ?? "").Contains("AppName", StringComparison.OrdinalIgnoreCase))
            {
                Instruct("Speak, or type and press Enter.");
                var stop  = (_appStopping ?? _stopping)?.Token ?? CancellationToken.None;
                var typed = TypedAsync();
                using var onStop    = stop.Register(() => Dispatcher.Invoke(CancelTyped));
                using var onAbandon = abandon.Register(() => Dispatcher.Invoke(CancelTyped));
                var words = await typed;
                return string.IsNullOrWhiteSpace(words) ? null
                     : MpaiJson.ToJson(BasicTextObject.FromText(words.Trim()));
            }

            // The list is filled before it is shown: a Service may have gained or
            // lost an App since the last time it was asked.
            await ShowAppsAsync();

            var picked = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
            await Dispatcher.InvokeAsync(() => { _choosing = picked; ShowApps(true); });
            var name = await picked.Task;
            await Dispatcher.InvokeAsync(() => ShowApps(false));

            return name is null ? null
                 : MpaiJson.ToJson(BasicTextObject.FromText(name));
        });

        // RUNNING THE APP THAT WAS CHOSEN. Its Workflow Description is obtained from
        // the Service and interpreted under a Controller of its own: one Controller,
        // one Module, and this client's own Controller is untouched by it.
        devices.Run = async app => await RunAppAsync(app);

        // A PICTURE. The request is a Qualifier saying what the User Agent wants;
        // this source reads the format asked for and answers it.
        //
        // Asked for a format it can supply, it returns the Object complete. Asked
        // for one it cannot - the person chose a PNG where JPEG was wanted - it
        // returns an Object with NO DATA and a Qualifier saying what it does have,
        // so the User Agent can abandon the acquisition or ask again naming that.
        // A source that silently substituted would leave a consumer to discover
        // the difference by failing.
        devices.RegisterAcquire("OSD-BVO-V1.5", async (_, wanted) =>
        {
            var askedFor = FormatWanted(wanted);          // e.g. "JPEG", or null

            // A FACE IS LOOKED AT, NOT CHOSEN. Asked for a Visual Object whose type
            // is a Face, the User Agent takes a frame from the camera.
            if ((wanted ?? "").Contains("\"Face\"", StringComparison.Ordinal))
            {
                var face = await Task.Run(() => new CameraUnit().AcquireFaceAsync());
                return face is null ? null : MpaiJson.ToJson(face);
            }

            // ASKED AGAIN FOR WHAT WAS OFFERED, the person is not asked again. The
            // first ask was answered with a Qualifier rather than data; this is the
            // same file, now being asked for in the format it actually is.
            if (_lastPicture is { } again && askedFor is not null && FormatOf(again) == askedFor)
            {
                var had = await File.ReadAllBytesAsync(again);
                _lastPicture = null;
                return MpaiJson.ToJson(
                    BasicVisualObject.FromFile(Path.GetFileName(again), had, "Picture"));
            }

            Instruct("Choose a picture.");
            var chosen = await Dispatcher.InvokeAsync(() =>
            {
                var dialog = new OpenFileDialog
                {
                    Title  = "Choose a picture",
                    Filter = "Pictures (*.jpg;*.jpeg;*.png;*.bmp)|*.jpg;*.jpeg;*.png;*.bmp|All files (*.*)|*.*"
                };
                return dialog.ShowDialog() == true ? dialog.FileName : null;
            });
            if (chosen is null) return null;

            _lastPicture = null;
            var bytes = await File.ReadAllBytesAsync(chosen);
            var got   = BasicVisualObject.FromFile(Path.GetFileName(chosen), bytes, "Picture");
            var have  = Path.GetExtension(chosen).ToLowerInvariant() is ".jpg" or ".jpeg" ? "JPEG"
                      : Path.GetExtension(chosen).ToLowerInvariant() is ".png"            ? "PNG"
                      : Path.GetExtension(chosen).ToLowerInvariant() is ".bmp"            ? "BMP"
                      : "";

            if (askedFor is not null && have.Length > 0 &&
                !string.Equals(askedFor, have, StringComparison.OrdinalIgnoreCase))
            {
                // WHAT IT HAS, WITH NO DATA. An Object is passed either way: this one
                // carries the Qualifier of the file that was chosen and nothing else,
                // so the User Agent can ask again naming that format.
                // REMEMBERED ONLY FOR THE ASK THAT FOLLOWS. A counter-offer is about
                // to be made; the ask that answers it must reach this same file rather
                // than asking the person a second time. Any later acquisition asks
                // again, which is why nothing is remembered on the ordinary path.
                _lastPicture = chosen;
                Status($"asked for {askedFor}; this is {have}");
                var counter = BasicVisualObject.FromFile(Path.GetFileName(chosen), Array.Empty<byte>(), "Picture");
                return MpaiJson.ToJson(counter);
            }

            return MpaiJson.ToJson(got);
        });
        // A POINT ON THE PICTURE SHOWN. The person clicks on the stage; the click is turned into the
        // pixel of the picture it is on (X to the right, Y down: the page's own coordinates, those of the
        // boxes Optical Character Recognition gives) and returned as a Point (OSD-PNT-V1.5). Clicks outside
        // the picture are not points; stopping the App or abandoning the step gives nothing.
        devices.RegisterAcquire("OSD-PNT-V1.5", async (_, _, abandon) =>
        {
            Instruct("Click at the top of a column.");
            var stop    = (_appStopping ?? _stopping)?.Token ?? CancellationToken.None;
            var clicked = new TaskCompletionSource<(double X, double Y)?>(TaskCreationOptions.RunContinuationsAsynchronously);
            System.Windows.Input.MouseButtonEventHandler onClick = (_, e) =>
            {
                if (PixelOfPicture(e.GetPosition(StageImage)) is { } pixel) clicked.TrySetResult(pixel);
            };
            await Dispatcher.InvokeAsync(() => { StageImage.MouseLeftButtonDown += onClick; StageImage.Cursor = System.Windows.Input.Cursors.Cross; });
            using var onStop    = stop.Register(() => clicked.TrySetResult(null));
            using var onAbandon = abandon.Register(() => clicked.TrySetResult(null));
            var at = await clicked.Task;
            await Dispatcher.InvokeAsync(() => { StageImage.MouseLeftButtonDown -= onClick; StageImage.Cursor = null; });
            if (at is not { } p) return null;
            Status($"point {p.X:0},{p.Y:0}");
            return string.Create(System.Globalization.CultureInfo.InvariantCulture,
                $"{{\"Header\":\"OSD-PNT-V1.5\",\"Coordinates\":{{\"X\":{p.X:0.#},\"Y\":{p.Y:0.#}}}}}");
        });
        // THE AVATAR. A Portable Avatar is one utterance: the audio is played and the
        // face and body are driven from the same clock, and the step does not finish
        // until she has finished speaking.
        devices.RegisterPresent("avatar", async data =>
        {
            SpeakLog("avatar: " + string.Join(", ", data.Keys) + (data.TryGetValue("PAF-PAV-V1.6", out var seen) ? " | " + (seen.Length > 200 ? seen[..200] : seen) + " | words: " + (AvatarUtterance.TextOfPortableAvatar(seen) ?? "(none)") : ""));
            if (!data.TryGetValue("PAF-PAV-V1.6", out var sav)) return;

            if (AvatarUtterance.FromPortableAvatar(sav) is not { } utterance)
            {
                // A Portable Avatar that says its words as a Text Object and carries no speech (the answer
                // of Answer to Multimodal Question): the words are shown, and spoken by the Module that
                // gives this client its voice.
                var words = AvatarUtterance.TextOfPortableAvatar(sav);
                if (words is null)
                {
                    // The answer holds a Text Object with no words (a column that was not found, a page with no text under the point):
                    // the person is told, rather than left with silence.
                    words = "I could not read any text there. Try the top of a column.";
                }
                var language = AvatarUtterance.LanguageOfPortableAvatar(sav);
                Instruct(words);
                if (language is "ja" or "zh")
                {
                    // Shown, not spoken: those voices do not speak yet.
                    await SpeakAsync($"This text is in {(language == "ja" ? "Japanese" : "Chinese")}, which I cannot read aloud yet. It is on the screen.");
                    return;
                }
                await SpeakAsync(words, language);
                return;
            }

            await _avatar!.PresentAsync(utterance);
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(utterance.MachineSpeechWav) + 0.8));
        });

        // THE AVATAR, AS AN INPUT. A Module that renders with Response and Scene
        // Rendering asks for the Avatar to animate: the model this client holds.
        devices.RegisterAcquire("OSD-B3O-V1.5", (_, _) =>
            Task.FromResult<string?>(AvatarUtterance.AvatarDatum(AvatarModel)));

        // A BASIC AUDIOVISUAL OBJECT (OSD-BMO-V1.5) does not carry its data: it designates it by a URI. The workflow's 'acquire' obtains the
        // Object as the workflow states it; 'present' plays what the URI designates (below).
        devices.RegisterAcquire("OSD-BMO-V1.5", (_, wanted) => Task.FromResult(wanted));

        // THE VIDEO. Played on the stage until it has played to its end, the person closes it, or Stop is pressed: the step ends then.
        devices.RegisterPresent("video", async data =>
        {
            foreach (var kv in data)
            {
                if (!kv.Key.Contains("OSD-BMO", StringComparison.OrdinalIgnoreCase)) continue;
                var uri = AudioVisualLink.UriOf(kv.Value);
                if (uri is null) continue;
                await PlayVideoAsync(uri);
            }
        });

        // THE STAGE.
        // THE STAGE. What the App is working with - a picture the person chose, a
        // document it was given - shown so they can see what they handed over.
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
                        var image = new System.Windows.Media.Imaging.BitmapImage();
                        image.BeginInit();
                        image.StreamSource = new MemoryStream(visual.Data);
                        image.CacheOption  = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                        image.EndInit();
                        StageImage.Source = image;
                        StageTitle.Text   = visual.FileName ?? "Picture";
                        StageText.Text    = $"{visual.Data.Length:N0} bytes";
                    });
                }
                catch { /* what cannot be shown is still what was given */ }
            }
            return Task.CompletedTask;
        });

        // THE SCREEN.
        devices.RegisterPresent("screen", async data =>
        {
            foreach (var kv in data)
            {
                // SHE SAYS IT, AND THE SCREEN SHOWS IT. A prompt is what the machine
                // asks of a person; an avatar that stays silent while text appears is
                // the thing this whole arrangement exists to avoid. The step is done when she
                // has said it: the next thing the workflow does - a question, a listening - follows
                // her words and does not run over them.
                if (kv.Key == "Prompt") { Instruct(kv.Value); await SpeakAsync(kv.Value); continue; }

                if (kv.Key.StartsWith("Display:OSD-BTO", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals("OSD-BTO-V1.5", StringComparison.OrdinalIgnoreCase))
                { Instruct(Words(kv.Value)); continue; }

                if (kv.Key.StartsWith("Display", StringComparison.OrdinalIgnoreCase))
                    Instruct(kv.Value);
            }
        });

        return devices;
    }

    // The pixel of the picture on the stage that a position on the stage's Image is over; null outside the
    // picture. The Image keeps the picture's proportions (Stretch=Uniform), so it is centred with bands at
    // two edges.
    private (double X, double Y)? PixelOfPicture(System.Windows.Point at)
    {
        if (StageImage.Source is not System.Windows.Media.Imaging.BitmapSource bitmap) return null;
        double w = StageImage.ActualWidth, h = StageImage.ActualHeight;
        if (w <= 0 || h <= 0 || bitmap.Width <= 0 || bitmap.Height <= 0) return null;
        double scale = Math.Min(w / bitmap.Width, h / bitmap.Height);
        double drawnW = bitmap.Width * scale, drawnH = bitmap.Height * scale;
        double x = at.X - (w - drawnW) / 2, y = at.Y - (h - drawnH) / 2;
        if (x < 0 || y < 0 || x > drawnW || y > drawnH) return null;
        return (x / drawnW * bitmap.PixelWidth, y / drawnH * bitmap.PixelHeight);
    }
    private static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"),  ("it", "Italiano"), ("es", "Espanol"), ("pt", "Portugues"),
        ("fr", "Francais"), ("de", "Deutsch"),  ("ja", "Nihongo"), ("zh", "Zhongwen")
    };

    // A small window with the two languages, as a translation App offers them.
    // The previous choice is offered again.
    private (string From, string To)? ChooseLanguages()
    {
        System.Windows.Controls.ComboBox Picker(string selected)
        {
            var box = new System.Windows.Controls.ComboBox { Width = 160, Margin = new Thickness(0, 4, 0, 10) };
            foreach (var (code, name) in Languages)
                box.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = name, Tag = code });
            box.SelectedIndex = Math.Max(0, Array.FindIndex(Languages, l => l.Code == selected));
            return box;
        }

        var from = Picker(_lastFrom);
        var to   = Picker(_lastTo);
        var ok   = new System.Windows.Controls.Button { Content = "OK", Width = 80, IsDefault = true,
                                                        HorizontalAlignment = HorizontalAlignment.Right };
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Input language" });
        panel.Children.Add(from);
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = "Output language" });
        panel.Children.Add(to);
        panel.Children.Add(ok);

        var dialog = new Window
        {
            Title = "Languages", Content = panel, Owner = this,
            SizeToContent = SizeToContent.WidthAndHeight, ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterOwner
        };
        ok.Click += (_, _) => dialog.DialogResult = true;
        if (dialog.ShowDialog() != true) return null;

        string Code(System.Windows.Controls.ComboBox box) =>
            (box.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Tag as string ?? "en";
        _lastFrom = Code(from);
        _lastTo   = Code(to);
        return (_lastFrom, _lastTo);
    }

    // STATE THE LANGUAGE, CARRY EVERYTHING ELSE. Speech Object Acquisition
    // recorded the sampling frequency and the precision; the language is added to
    // the Qualifier that came with the capture, as a translation App does.
    private static BasicSpeechObject WithLanguage(BasicSpeechObject speech, string language)
    {
        var captured = speech.SpeechQualifier;
        var qualifier = new SpeechQualifier
        {
            SpeechQualifierID = Guid.NewGuid().ToString(),
            MInstanceID       = captured?.MInstanceID,
            UEnvironmentID    = captured?.UEnvironmentID,
            SubType           = captured?.SubType,
            Format            = captured?.Format,
            Attributes = new SpeechAttributes
            {
                Source                = captured?.Attributes?.Source,
                SpeechCharacteristics = captured?.Attributes?.SpeechCharacteristics,
                Structure             = captured?.Attributes?.Structure,
                Device                = captured?.Attributes?.Device,
                Metadata = new SpeechMetadata
                {
                    SpeakerProperties = captured?.Attributes?.Metadata?.SpeakerProperties,
                    Language = new Language { LanguageCode = language, LanguageFormat = LanguageFormat.Iso639_1 }
                }
            }
        };
        return BasicSpeechObject.FromData(speech.Data, qualifier);
    }


    private async Task KeepPresentAsync()
    {
        using var presence = new AppDirectory(ServiceUrl, ServiceToken, MasClientIdentity.Id, Collection);
        while (true)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            await presence.ActiveClientsAsync();
        }
    }

    // "You are the 2nd concurrent user ..." - the ordinal of a count.
    private static string Ordinal(int n) =>
        (n % 100) is 11 or 12 or 13 ? n + "th"
        : (n % 10) switch { 1 => n + "st", 2 => n + "nd", 3 => n + "rd", _ => n + "th" };

    // ---- the window --------------------------------------------------------

    private Task<string> TypedAsync()
    {
        _typed = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        Dispatcher.Invoke(() =>
        {
            TypedBox.IsEnabled = SendButton.IsEnabled = true;
            TypedBox.Clear();
            TypedBox.Focus();
        });
        return _typed.Task;
    }

    private void SendTyped()
    {
        if (_typed is null) return;
        var words = TypedBox.Text;
        if (string.IsNullOrWhiteSpace(words)) return;      // Enter on an empty box gives nothing
        TypedBox.IsEnabled = SendButton.IsEnabled = false;
        var waiting = _typed;
        _typed = null;
        waiting.TrySetResult(words);
    }

    // The text box closes without giving anything: the person spoke instead, or
    // pressed Stop.
    private void CancelTyped()
    {
        if (_typed is null) return;
        TypedBox.IsEnabled = SendButton.IsEnabled = false;
        TypedBox.Clear();
        var waiting = _typed;
        _typed = null;
        waiting.TrySetResult("");
    }

    private void Instruct(string text) =>
        Dispatcher.Invoke(() => InstructionText.Text = text);

    // THE STATUS LINE KEEPS ONLY THE LAST THING SAID. Every step the interpreter
    // takes is also written down, so that a run can be read afterwards rather
    // than watched.
    private void Status(string text)
    {
        Dispatcher.Invoke(() => StatusText.Text = text);
        try
        {
            System.IO.File.AppendAllText(Program.CrashLog,
                $"{DateTime.Now:HH:mm:ss}  {text}{Environment.NewLine}");
        }
        catch { }
    }

    private static string Words(string json)
    {
        try { return MpaiJson.FromJson<BasicTextObject>(json)?.GetText() ?? ""; }
        catch { return ""; }
    }

    private static SimpleTime Now()
    {
        var t = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        return new SimpleTime
        {
            SimpleTimeID   = Guid.NewGuid().ToString(),
            SimpleTimeData = new List<TimeSegment>
            {
                new TimeSegment { FlagsByte = 0, StartTime = t, EndTime = t, AccuracyMode = "single" }
            }
        };
    }
}