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
using Mpai.Aims.Visual;
using Mpai.Hci.Api;
using Mpai.Mas.Client;
using Mpai.Rca;
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
    private TaskCompletionSource?    _awaiting;

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
        StopButton.Click  += (_, _) => _stopping?.Cancel();
        SendButton.Click  += (_, _) => SendTyped();
        TypedBox.KeyDown  += (_, e) => { if (e.Key == System.Windows.Input.Key.Enter) SendTyped(); };
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        try
        {
            Status("starting the avatar...");
            _avatar = new AvatarUaHost(Web, Dispatcher, AmdDir, AssetsDir);
            await _avatar.InitAsync();
            await Task.Delay(TimeSpan.FromSeconds(1.5));

            // A workflow may be named on the command line, so that a shipped
            // client can be started on the one it is meant to run.
            var args = Environment.GetCommandLineArgs();
            if (args.Length > 1 && File.Exists(args[1])) Load(args[1]);

            WorkflowText.Text = $"Service: {ServiceUrl}";
            Status("ready");

            // A CLIENT THAT HOLDS NO APPLICATION HAS NOTHING ELSE TO SHOW.
            // Asking the Service what it offers is the first thing it does.
            await SpeakWelcomeAsync();
            await ShowAppsAsync();
        }
        catch (Exception fatal)
        {
            Program.Record("startup", fatal);
            Status("startup failed: " + fatal.Message);
        }
    }

    // ---- the workflow ------------------------------------------------------

    // THE CLIENT SPEAKS BEFORE IT HAS AN APPLICATION. It drives PAF-RSR on the
    // Service directly - start, give the words at both Text Object Ports,
    // collect the speech and the face descriptors, present them, stop - which is
    // exactly what a workflow's welcome does. That this is possible with no App
    // chosen is the point: the client holds nothing but the means to render.
    private const string RsrModule = "PAF-RSR-V1.6";

    private Task SpeakWelcomeAsync() =>
        SpeakAsync("Welcome to MPAI as a Service. Select an app and enjoy.");

    private async Task SpeakAsync(string words)
    {
        try
        {
            using var north = new RemoteNorthApi(ServiceUrl, ServiceToken);
            if (north.StartFlow(RsrModule) != AifError.OK) return;

            var said = await Task.Run(() => north.Advance(RsrModule, new List<NorthApi.Datum>
            {
                new NorthApi.Datum("OSD-BTO-V1.5", 1, MpaiJson.ToJson(BasicTextObject.FromText(words))),
                new NorthApi.Datum("OSD-BTO-V1.5", 2, MpaiJson.ToJson(BasicTextObject.FromText(words)))
            }));
            north.StopFlow(RsrModule);

            if (!said.Ok) return;

            var speech = said.ByType("OSD-BSO-V1.5");
            var face   = said.ByType("PAF-FDO-V1.6");
            if (string.IsNullOrWhiteSpace(speech)) return;

            var wav = MpaiJson.FromJson<BasicSpeechObject>(speech)?.Data ?? Array.Empty<byte>();
            var fdo = string.IsNullOrWhiteSpace(face)
                ? null : MpaiJson.FromJson<FaceDescriptorsObject>(face);

            await _avatar!.PresentAsync(new SpeakingAvatar(wav, fdo, null));
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.3));
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

    private async Task ShowAppsAsync()
    {
        try
        {
            Status("asking the Service what it offers...");
            using var directory = new AppDirectory(ServiceUrl, ServiceToken);
            var apps = await directory.ListAsync();

            if (apps.Count == 0)
            {
                AppPanelHint.Text = "This Service offers no Apps. Open a Workflow Description from a file instead.";
                AppList.ItemsSource = null;
                ShowPanel(true); AppColumn.Width = new GridLength(320);
                Status("no Apps offered");
                return;
            }

            var shown = new List<Offered>();
            foreach (var a in apps)
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

        try
        {
            Status($"fetching {chosen.Name}...");
            using var directory = new AppDirectory(ServiceUrl, ServiceToken);
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
            await RunAsync();
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

    // CHOOSING IS STARTING. A person who has picked what they want should not
    // then have to announce it, so the row is the instruction.
    private void AppList_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (AppList.SelectedItem is not Offered chosen) return;
        AppList.SelectedItem = null;
        Status($"chose {chosen.Name}");
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
            InitialDirectory = Path.Combine(MpaiPaths.Root, "UAs", "Orchestration")
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
            using var north = new RemoteNorthApi(ServiceUrl, ServiceToken);

            var interpreter = new WorkflowInterpreter(north, Devices(), Status);
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
            StopButton.IsEnabled  = false;
            TypedBox.IsEnabled = SendButton.IsEnabled = false;
        }
    }
    // ---- the devices -------------------------------------------------------

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
        devices.RegisterAcquire("OSD-BSO-V1.5", async viaVad =>
        {
            Instruct("Speak when you are ready.");
            var speech = await Task.Run(() => _avatar!.CaptureSpeech());
            return speech is null || speech.Data.Length == 0
                ? null
                : MpaiJson.ToJson(speech);
        });

        // A PICTURE, CHOSEN FROM DISK. The same Data Type as a webcam frame and a
        // different Qualifier: the difference is not in the data, and the format
        // is where it is recorded. A workflow asks for one or the other by
        // naming the format, and never by naming a device.
        devices.RegisterAcquire("OSD-BVO-V1.5", "image/png", async _ =>
        {
            Instruct("Choose a picture.");
            var chosen = await Dispatcher.InvokeAsync(() =>
            {
                var dialog = new OpenFileDialog
                {
                    Title  = "Choose a picture",
                    Filter = "Pictures (*.png;*.jpg;*.jpeg;*.bmp)|*.png;*.jpg;*.jpeg;*.bmp|All files (*.*)|*.*"
                };
                return dialog.ShowDialog() == true ? dialog.FileName : null;
            });
            if (chosen is null) return null;

            var bytes = await File.ReadAllBytesAsync(chosen);
            return MpaiJson.ToJson(
                BasicVisualObject.FromFile(Path.GetFileName(chosen), bytes, "Picture"));
        });

        // A FACE, FROM THE CAMERA.
        devices.RegisterAcquire("OSD-BVO-V1.5", "image/jpeg", async _ =>
        {
            Instruct("Look at the camera.");
            var frame = await Task.Run(() =>
                new WebcamVisualAcquisition()
                    .AcquireAsync(new VisualAcquisitionRequest { VisualObjectType = "Face" })
                    .GetAwaiter().GetResult().Data);

            return frame is { Length: > 0 }
                ? MpaiJson.ToJson(BasicVisualObject.FromFile("webcam.jpg", frame, "Face"))
                : null;
        });

        // WORDS, FROM THE KEYBOARD. The window opens its text box and waits.
        devices.RegisterAcquire("OSD-BTO-V1.5", async _ =>
        {
            var words = await TypedAsync();
            return string.IsNullOrWhiteSpace(words)
                ? null
                : MpaiJson.ToJson(BasicTextObject.FromText(words));
        });

        // A TIME, FROM THE CLOCK.
        devices.RegisterAcquire("OSD-STM-V1.5", _ =>
            Task.FromResult<string?>(MpaiJson.ToJson(Now())));

        // THE AVATAR. Speech and face descriptors are one utterance, not two, so a
        // presenter is offered everything being presented and takes what it knows.
        devices.RegisterPresent("avatar", async data =>
        {
            byte[] wav = Array.Empty<byte>();
            FaceDescriptorsObject? fdo = null;

            if (data.TryGetValue("OSD-BSO-V1.5", out var sj) && !string.IsNullOrWhiteSpace(sj))
                wav = MpaiJson.FromJson<BasicSpeechObject>(sj)?.Data ?? Array.Empty<byte>();

            if (data.TryGetValue("PAF-FDO-V1.6", out var fj) && !string.IsNullOrWhiteSpace(fj))
                fdo = MpaiJson.FromJson<FaceDescriptorsObject>(fj);

            if (wav.Length == 0 && fdo is null) return;

            await _avatar!.PresentAsync(new SpeakingAvatar(wav, fdo, null));
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.3));
        });

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
        devices.RegisterPresent("screen", data =>
        {
            foreach (var kv in data)
            {
                // SHE SAYS IT, AND THE SCREEN SHOWS IT. A prompt is what the machine
                // asks of a person; an avatar that stays silent while text appears is
                // the thing this whole arrangement exists to avoid.
                if (kv.Key == "Prompt") { Instruct(kv.Value); _ = SpeakAsync(kv.Value); continue; }

                if (kv.Key.StartsWith("Display:OSD-BTO", StringComparison.OrdinalIgnoreCase) ||
                    kv.Key.Equals("OSD-BTO-V1.5", StringComparison.OrdinalIgnoreCase))
                { Instruct(Words(kv.Value)); continue; }

                if (kv.Key.StartsWith("Display", StringComparison.OrdinalIgnoreCase))
                    Instruct(kv.Value);
            }
            return Task.CompletedTask;
        });

        return devices;
    }

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
        TypedBox.IsEnabled = SendButton.IsEnabled = false;
        var waiting = _typed;
        _typed = null;
        waiting.TrySetResult(words);
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