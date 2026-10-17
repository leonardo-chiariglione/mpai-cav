using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

using AIF.Controller;   // AifError
using AIF.Store;        // AmdStore

using Mpai.Core;
using Mpai.UaKit;       // AvatarUaHost, CaptureSpeech
using Mpai.Aif.Api;     // ControllerApi, AvatarUtterance

using Mpai.Providers;

namespace MmcAmq;

// MMC-AMQ User Agent - Answer to Multimodal Question, driven through the
// type-addressed Controller API. The UA presents an image and a question (typed or
// spoken); the Module answers. One run per question:
//   supply OSD-BVO (image) + OSD-BTO#1 (typed question) OR OSD-BSO#1 (spoken) + the machine's ID (string);
//   read the Portable Avatar (PAF-PAV-V1.6), whose Text Object is the answer; it is spoken through PAF-RSR.
// The answer is shown as text, spoken by the avatar, and the image is re-shown.
// Acquisition/presentation are the UA's; the Module is ASR + TIQ + TTS.
public partial class MainWindow : Window
{
    private const string AmqModule = "1MMC-AMQ-V2.5-I01";

    private const string BVO = "OSD-BVO-V1.5";   // visual object (image)
    private const string BSO = "OSD-BSO-V1.5";   // speech object (spoken q / spoken answer)
    private const string BTO = "OSD-BTO-V1.5";   // text object (typed question)
    private const string MachineName = "Thalia"; // the machine's ID: the Portable Avatar the Module gives is this machine's

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    private IControllerApi?    _north;
    private AvatarUaHost? _avatar;
    private byte[]?       _imageBytes;
    private string?       _lastQuestion;
    private string?       _imagePath;

    private static void Diag(string s)
    {
        try { System.IO.File.AppendAllText(@"C:\Users\Leonardo\Downloads\amq-diag.log",
              DateTime.Now.ToString("HH:mm:ss.fff") + "  " + s + System.Environment.NewLine); } catch { }
    }

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
            await Task.Run(() =>
            {
                // THIS APPLICATION RUNS ITS MODULE IN PROCESS. Driving one over a
                // network is AmqClient, a different application for a different
                // user: it holds no model and no Framework. They were one source
                // with a compilation switch, which meant the local application
                // carried the networked path it never used.
                _north = new ControllerApi(AmdDir, SettingsPath, store => new AmqProvider(store));
            });
            LoadButton.IsEnabled = true;
            SetStatus("Ready.");
            await Task.Delay(TimeSpan.FromSeconds(2.0));   // let the avatar/WebView settle before speaking
            Diag("welcome: speaking");
            await SpeakWelcomeAsync();
            Diag("welcome: done");
        }
        catch (Exception fatal) { Program.Record("startup", fatal); SetStatus("startup failed: " + fatal.Message); }
    }

    private void LoadButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "Images|*.jpg;*.jpeg;*.png;*.bmp;*.gif|All files|*.*" };
        if (dlg.ShowDialog() != true) return;
        try
        {
            _imagePath  = dlg.FileName;
            _imageBytes = File.ReadAllBytes(_imagePath);
            var bmp = new BitmapImage();
            using (var ms = new MemoryStream(_imageBytes))
            { bmp.BeginInit(); bmp.CacheOption = BitmapCacheOption.OnLoad; bmp.StreamSource = ms; bmp.EndInit(); }
            ImageView.Source = bmp;
            AskButton.IsEnabled = true;
            SpeakButton.IsEnabled = true;
            SetStatus("Image loaded. Type or speak your question, then Ask.");
        }
        catch (Exception ex) { Program.Record("load", ex); SetStatus("could not load image: " + ex.Message); }
    }

    // Speak the question: capture speech, run AMQ with OSD-BSO (ASR transcribes inside the Module).
    private async void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        if (_imageBytes is null || _avatar is null) return;
        SetBusy(true); SetStatus("listening...");
        try
        {
            var speech = await Task.Run(() => _avatar!.CaptureSpeech());
            if (speech is null || speech.Data.Length == 0) { SetStatus("no speech captured."); return; }
            await RunAsync(spokenQuestion: speech, typedQuestion: null);
        }
        catch (Exception ex) { Program.Record("speak", ex); SetStatus("error: " + ex.Message); }
        finally { SetBusy(false); }
    }

    // Ask the typed question: run AMQ with OSD-BTO.
    private async void AskButton_Click(object sender, RoutedEventArgs e)
    {
        if (_imageBytes is null) return;
        var q = QuestionBox.Text?.Trim();
        if (string.IsNullOrWhiteSpace(q)) { SetStatus("type a question first (or use Speak Question)."); return; }
        SetBusy(true);
        try { await RunAsync(spokenQuestion: null, typedQuestion: q); }
        catch (Exception ex) { Program.Record("ask", ex); SetStatus("error: " + ex.Message); }
        finally { SetBusy(false); }
    }


    private const string Welcome =
        "Welcome to the M P A I Answer to Multimodal Question Service. " +
        "Select an image, then ask a question about it by typing or by speaking.";

    private const string RsrModule = "1PAF-RSR-V1.6-I01";

    // Speak a line through PAF-RSR (text -> machine speech + face), presented on the avatar.
    private bool _rsrStarted = false;
    private async Task RenderPromptAsync(string words)
    {
        if (_north is null || _avatar is null) return;
        if (!_rsrStarted)
        {
            var s = await Task.Run(() => _north!.StartFlow(RsrModule));
            Diag("StartFlow RSR -> " + s);
            _rsrStarted = (s == AifError.OK);
        }
        // PAF-RSR renders a Text Object as the Portable Avatar of a machine: it declares TextObject TWICE (#1 feeds
        // Text-To-Speech, #2 Generative Face Description, which turns the words into visemes - sending only #1
        // leaves the mouth with nothing to shape itself to), and takes the Avatar Model this client draws and
        // the machine's ID.
        var inputs = new List<ControllerApi.Datum>
        {
            new ControllerApi.Datum(BTO, 1, MpaiJson.ToJson(BasicTextObject.FromText(words))),
            new ControllerApi.Datum(BTO, 2, MpaiJson.ToJson(BasicTextObject.FromText(words))),
            new ControllerApi.Datum("OSD-B3O-V1.5", 1, AvatarUtterance.AvatarDatum("cav-avatar.glb")),
            new ControllerApi.Datum("string", 1, System.Text.Json.JsonSerializer.Serialize(MachineName))
        };
        var r = await Task.Run(() => _north!.Advance(RsrModule, inputs));
        if (!r.Ok) { Diag("RSR err=" + r.Error); return; }
        // The Portable Avatar: the speech, and the face and body that move with it.
        if (AvatarUtterance.FromPortableAvatar(r.ByType("PAF-PAV-V1.6")) is not { } utterance) { Diag("RSR gave no speech"); return; }
        Diag("RSR spoke words=[" + (words.Length > 40 ? words.Substring(0, 40) : words) + "] wavBytes=" + utterance.MachineSpeechWav.Length);
        await _avatar!.PresentAsync(utterance);
        await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(utterance.MachineSpeechWav) + 0.3));
    }

    private async Task SpeakWelcomeAsync()
    {
        InstructionText.Text = "Select an image, then ask a question about it (type or speak).";
        try { await RenderPromptAsync(Welcome); } catch (Exception ex) { Diag("welcome ex=" + ex.Message); }
    }

    // Compose a natural sentence from the question and BLIP's short answer, so the
    // lady says "This animal is a deer." rather than just "deer".
    private static string Phrase(string? question, string? answer)
    {
        var a = (answer ?? "").Trim().TrimEnd('.').Trim();
        if (a.Length == 0) return "I am not sure.";
        var q = (question ?? "").Trim().ToLowerInvariant();
        var al = a.ToLowerInvariant();

        // yes/no answers
        if (al == "yes") return "Yes.";
        if (al == "no")  return "No.";

        string Art(string w)  // a / an
            => (w.Length > 0 && "aeiou".IndexOf(char.ToLowerInvariant(w[0])) >= 0) ? "an " : "a ";
        bool numeric = int.TryParse(a, out _);

        // "how many ..."
        if (q.StartsWith("how many"))
        {
            var noun = q.Length > 8 ? q.Substring(8).Trim().TrimEnd('?').Trim() : "";
            return noun.Length > 0 ? $"There are {a} {noun}." : $"There are {a}.";
        }
        // "what color ..." / "what colour ..."
        if (q.Contains("color") || q.Contains("colour"))
            return $"It is {a}.";
        // "where ..."
        if (q.StartsWith("where"))
            return $"It is {a}.";
        // "what animal is this" / "what kind of animal ..."
        if (q.Contains("animal"))
            return numeric ? $"There are {a}." : $"This animal is {Art(a)}{a}.";
        // "what is this / what is in the image / what do you see"
        if (q.StartsWith("what is") || q.Contains("what is in") || q.Contains("what do you see") || q.StartsWith("what's"))
            return numeric ? $"There are {a}." : $"This is {Art(a)}{a}.";
        // generic "what ..." -> "It is a X."
        if (q.StartsWith("what"))
            return numeric ? $"There are {a}." : $"It is {Art(a)}{a}.";
        // fallback
        return $"The answer is {a}.";
    }
    private async Task RunAsync(BasicSpeechObject? spokenQuestion, string? typedQuestion)
    {
        if (_north is null || _imageBytes is null) return;
        _lastQuestion = typedQuestion;   // typed questions phrase precisely; spoken phrase generically
        SetStatus("thinking...");

        var image = BasicVisualObject.FromFile(_imagePath ?? "image.jpg", _imageBytes, "Image");
        var inputs = new List<ControllerApi.Datum> { new ControllerApi.Datum(BVO, MpaiJson.ToJson(image)) };
        if (spokenQuestion is not null)
            inputs.Add(new ControllerApi.Datum(BSO, MpaiJson.ToJson(spokenQuestion)));
        else if (typedQuestion is not null)
            inputs.Add(new ControllerApi.Datum(BTO, MpaiJson.ToJson(BasicTextObject.FromText(typedQuestion))));
        inputs.Add(new ControllerApi.Datum("string", 1, System.Text.Json.JsonSerializer.Serialize(MachineName)));

        var started = await Task.Run(() => _north!.StartFlow(AmqModule));
        Diag("StartFlow AMQ -> " + started);
        if (started != AifError.OK) { SetStatus("could not start " + AmqModule); return; }

        Diag("Advance AMQ: inputs=[" + string.Join(",", inputs.ConvertAll(d => d.DataType)) + "]" + (spokenQuestion!=null?(" speechBytes="+spokenQuestion.Data.Length):"") + (typedQuestion!=null?(" typed='"+typedQuestion+"'"):""));
        var r = await Task.Run(() => _north!.Advance(AmqModule, inputs));
        Diag("Advance AMQ done ok=" + r.Ok + " err=" + r.Error + " PAV text=[" + (AvatarUtterance.TextOfPortableAvatar(r.ByType("PAF-PAV-V1.6")) ?? "nil") + "]");
        await Task.Run(() => _north!.StopFlow(AmqModule));
        if (!r.Ok) { Diag("AMQ err=" + r.Error); SetStatus("no answer: " + r.Error); return; }

        // The Module answers with a Portable Avatar that carries the answer as a Text Object.
        var answer   = AvatarUtterance.TextOfPortableAvatar(r.ByType("PAF-PAV-V1.6"));
        Diag("answer='" + (answer ?? "") + "'");

        var spoken = Phrase(_lastQuestion, answer);
        AnswerText.Text = spoken;
        SetStatus("done.");

        // speak the composed sentence through the avatar with a face (via PAF-RSR).
        if (!string.IsNullOrWhiteSpace(spoken))
            await RenderPromptAsync(spoken);
    }

    private static string? TextOf(string? json)
    { if (string.IsNullOrWhiteSpace(json)) return null; try { return MpaiJson.FromJson<BasicTextObject>(json)?.GetText(); } catch { return null; } }
    private static byte[] SpeechOf(string? json)
    { if (string.IsNullOrWhiteSpace(json)) return Array.Empty<byte>(); try { return MpaiJson.FromJson<BasicSpeechObject>(json)?.Data ?? Array.Empty<byte>(); } catch { return Array.Empty<byte>(); } }

    private void SetBusy(bool b) => Dispatcher.Invoke(() =>
    { LoadButton.IsEnabled = !b; AskButton.IsEnabled = !b && _imageBytes is not null; SpeakButton.IsEnabled = !b && _imageBytes is not null; });
    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);

    protected override void OnClosed(EventArgs e)
    { try { _north?.StopFlow(AmqModule); } catch { } base.OnClosed(e); }
}
