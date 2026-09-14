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
using Mpai.Hci.Api;     // NorthApi, SpeakingAvatar

namespace MmcAmq;

// MMC-AMQ User Agent - Answer to Multimodal Question, driven through the
// type-addressed North API. The UA presents an image and a question (typed or
// spoken); the Module answers. One run per question:
//   supply OSD-BVO (image) + OSD-BTO (typed question) OR OSD-BSO (spoken);
//   read OSD-BTO (answer text), OSD-BSO (spoken answer), OSD-BVO (image back).
// The answer is shown as text, spoken by the avatar, and the image is re-shown.
// Acquisition/presentation are the UA's; the Module is ASR + TIQ + TTS.
public partial class MainWindow : Window
{
    private const string AmqModule = "MMC-AMQ-V2.5";

    private const string BVO = "OSD-BVO-V1.5";   // visual object (image)
    private const string BSO = "OSD-BSO-V1.5";   // speech object (spoken q / spoken answer)
    private const string BTO = "OSD-BTO-V1.5";   // text object (typed q / answer text)

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    private NorthApi?     _north;
    private AvatarUaHost? _avatar;
    private byte[]?       _imageBytes;
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
            SetStatus("loading...");
            _avatar = new AvatarUaHost(Web, Dispatcher, AmdDir, AssetsDir);
            await _avatar.InitAsync();
            await Task.Run(() => _north = new NorthApi(AmdDir, SettingsPath, store => new AmqProvider(store)));
            LoadButton.IsEnabled = true;
            SetStatus("Ready.");
            await SpeakWelcomeAsync();
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

    private const string RsrModule = "PAF-RSR-V1.6";

    // Speak a line through PAF-RSR (text -> machine speech + face), presented on the avatar.
    private async Task RenderPromptAsync(string words)
    {
        if (_north is null || _avatar is null) return;
        var inputs = new List<NorthApi.Datum> { new NorthApi.Datum(BTO, MpaiJson.ToJson(BasicTextObject.FromText(words))) };
        var r = await Task.Run(() => _north!.Advance(RsrModule, inputs));
        if (!r.Ok) { Diag("welcome RSR err=" + r.Error); return; }
        byte[] wav = SpeechOf(r.ByType(BSO));
        var fdoJson = r.ByType("PAF-FDO-V1.6");
        Mpai.Core.OSD.FaceDescriptorsObject? fdo = null;
        try { if (!string.IsNullOrWhiteSpace(fdoJson)) fdo = MpaiJson.FromJson<Mpai.Core.OSD.FaceDescriptorsObject>(fdoJson); } catch { }
        if (wav.Length > 0 || fdo is not null)
        {
            await _avatar!.PresentAsync(new SpeakingAvatar(wav, fdo));
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(wav) + 0.3));
        }
    }

    private async Task SpeakWelcomeAsync()
    {
        InstructionText.Text = "Select an image, then ask a question about it (type or speak).";
        try { await RenderPromptAsync(Welcome); } catch (Exception ex) { Diag("welcome ex=" + ex.Message); }
    }

    private async Task RunAsync(BasicSpeechObject? spokenQuestion, string? typedQuestion)
    {
        if (_north is null || _imageBytes is null) return;
        SetStatus("thinking...");

        var image = BasicVisualObject.FromFile(_imagePath ?? "image.jpg", _imageBytes, "Image");
        var inputs = new List<NorthApi.Datum> { new NorthApi.Datum(BVO, MpaiJson.ToJson(image)) };
        if (spokenQuestion is not null)
            inputs.Add(new NorthApi.Datum(BSO, MpaiJson.ToJson(spokenQuestion)));
        else if (typedQuestion is not null)
            inputs.Add(new NorthApi.Datum(BTO, MpaiJson.ToJson(BasicTextObject.FromText(typedQuestion))));

        var started = await Task.Run(() => _north!.StartFlow(AmqModule));
        Diag("StartFlow AMQ -> " + started);
        if (started != AifError.OK) { SetStatus("could not start " + AmqModule); return; }

        Diag("Advance AMQ: inputs=[" + string.Join(",", inputs.ConvertAll(d => d.DataType)) + "]" + (spokenQuestion!=null?(" speechBytes="+spokenQuestion.Data.Length):"") + (typedQuestion!=null?(" typed='"+typedQuestion+"'"):""));
        var r = await Task.Run(() => _north!.Advance(AmqModule, inputs));
        Diag("Advance AMQ done ok=" + r.Ok + " err=" + r.Error + " BTO=[" + (r.ByType(BTO) ?? "nil")?.Substring(0, System.Math.Min(120,(r.ByType(BTO)??"").Length)) + "] BSObytes=" + SpeechOf(r.ByType(BSO)).Length);
        await Task.Run(() => _north!.StopFlow(AmqModule));
        if (!r.Ok) { Diag("AMQ err=" + r.Error); SetStatus("no answer: " + r.Error); return; }

        var answer   = TextOf(r.ByType(BTO));
        var replyWav = SpeechOf(r.ByType(BSO));
        Diag("answer='" + (answer ?? "") + "' replyWav=" + replyWav.Length);

        AnswerText.Text = answer ?? "(no text answer)";
        SetStatus("done.");

        // speak the answer through the avatar with a face (via PAF-RSR).
        if (!string.IsNullOrWhiteSpace(answer))
            await RenderPromptAsync(answer!);
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
