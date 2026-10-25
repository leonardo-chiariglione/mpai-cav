using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;

using AIF.Controller;   // AifError

using Mpai.Core;
using Mpai.UaKit;       // AvatarUaHost
using Mpai.Aif.Api;     // ControllerApi, AvatarUtterance

using Mpai.Providers;

namespace MmcMca;

// MMC-MCA User Agent - Multimodal Conversation Avatar, on its own: no MAS, no launcher. It follows the workflow of the App
// (Apps/MMC/MCA/MMC-MCA.orch), driven through the type-addressed Controller API:
//   start the Module once; each turn offers the person's speech (OSD-BSO) or typed text (OSD-BTO#1), the Avatar Model this
//   window draws (OSD-B3O) and the machine's ID (string); the Module answers with the machine's Portable Avatar (PAF-PAV),
//   which carries the reply as text and as speech, and the Edited Summary (MMC-SUM), the memory of the conversation, which
//   goes back with the next turn and nowhere else.
// The window renders the Portable Avatar on the avatar it keeps and makes no speech of its own: the speech is made by
// Text-To-Speech inside the Module. Ollama must be running for the dialogue (Entity Dialogue Processing).
public partial class MainWindow : Window
{
    private const string Module = "1MMC-MCA-V2.5-I01";

    private const string BSO = "OSD-BSO-V1.5";
    private const string BTO = "OSD-BTO-V1.5";
    private const string B3O = "OSD-B3O-V1.5";
    private const string SUM = "MMC-SUM-V2.5";
    private const string PAV = "PAF-PAV-V1.6";
    private const string MachineName = "Thalia";

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    private ControllerApi? _north;
    private AvatarUaHost?  _avatar;
    private string?        _memory;          // the Edited Summary of the last turn

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
            AifError started = AifError.OK;
            await Task.Run(() =>
            {
                _north = new ControllerApi(AmdDir, SettingsPath, store => new McaProvider(store));
                started = _north.StartFlow(Module);
            });
            if (started != AifError.OK) { SetStatus("could not start " + Module + ": " + started); return; }
            SpeakButton.IsEnabled = SendButton.IsEnabled = true;
            InstructionText.Text = "Talk to the avatar: press Speak and say something, or type it and press Enter.";
            SetStatus("Ready.");
        }
        catch (Exception fatal) { Program.Record("startup", fatal); SetStatus("startup failed: " + fatal.Message); }
    }

    private async void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        if (_avatar is null) return;
        SetBusy(true); SetStatus("listening...");
        try
        {
            var speech = await Task.Run(() => _avatar!.CaptureSpeech());
            if (speech is null || speech.Data.Length == 0) { SetStatus("no speech captured."); return; }
            AddLine("You", "(spoken)");
            await TurnAsync(new ControllerApi.Datum(BSO, MpaiJson.ToJson(speech)));
        }
        catch (Exception ex) { Program.Record("speak", ex); SetStatus("error: " + ex.Message); }
        finally { SetBusy(false); }
    }

    private void SendButton_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

    private void TextBoxIn_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SendButton.IsEnabled) _ = SendAsync();
    }

    private async Task SendAsync()
    {
        var words = TextBoxIn.Text?.Trim();
        if (string.IsNullOrWhiteSpace(words)) return;
        TextBoxIn.Clear();
        SetBusy(true);
        try
        {
            AddLine("You", words);
            await TurnAsync(new ControllerApi.Datum(BTO, 1, MpaiJson.ToJson(BasicTextObject.FromText(words))));
        }
        catch (Exception ex) { Program.Record("send", ex); SetStatus("error: " + ex.Message); }
        finally { SetBusy(false); }
    }

    // One turn: the person's words, the Avatar Model, the machine's ID and the memory; the Portable Avatar presented.
    private async Task TurnAsync(ControllerApi.Datum said)
    {
        if (_north is null || _avatar is null) return;
        SetStatus("thinking...");
        var inputs = new List<ControllerApi.Datum>
        {
            said,
            new ControllerApi.Datum(B3O, 1, AvatarUtterance.AvatarDatum("cav-avatar.glb")),
            new ControllerApi.Datum("string", 1, System.Text.Json.JsonSerializer.Serialize(MachineName))
        };
        if (!string.IsNullOrWhiteSpace(_memory)) inputs.Add(new ControllerApi.Datum(SUM, 1, _memory!));

        var r = await Task.Run(() => _north!.Advance(Module, inputs));
        if (!r.Ok) { Program.Record("advance", new Exception(r.Error.ToString())); SetStatus("no reply: " + r.Error); return; }

        _memory = r.ByType(SUM) ?? _memory;
        var pav = r.ByType(PAV);
        AddLine(MachineName, AvatarUtterance.TextOfPortableAvatar(pav) ?? "");
        SetStatus("done.");

        if (AvatarUtterance.FromPortableAvatar(pav) is { } utterance)
        {
            await _avatar.PresentAsync(utterance);
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(utterance.MachineSpeechWav) + 0.3));
        }
        else SetStatus("the Portable Avatar carries no speech.");
    }

    private void AddLine(string who, string words) => Dispatcher.Invoke(() =>
    {
        Transcript.AppendText($"{who}: {words}{Environment.NewLine}{Environment.NewLine}");
        Transcript.ScrollToEnd();
    });

    private void SetBusy(bool b) => Dispatcher.Invoke(() => { SpeakButton.IsEnabled = !b; SendButton.IsEnabled = !b; });
    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);

    protected override void OnClosed(EventArgs e)
    { try { _north?.StopFlow(Module); } catch { } base.OnClosed(e); }
}
