using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

using AIF.Controller;   // AifError

using Mpai.Core;
using Mpai.UaKit;       // AvatarUaHost
using Mpai.Aif.Api;     // ControllerApi, AvatarUtterance

using Mpai.Providers;

namespace MmcMts;

// MMC-MTS User Agent - Multimodal Translation Service, on its own: no MAS, no launcher. It follows the workflow of the App
// (Apps/MMC/MTS/MMC-MTS.orch), driven through the type-addressed Controller API:
//   start the Module once; each turn offers the Language Selector (the language the person speaks and the one the translation is
//   to be in, OSD-SEL), the person's speech (OSD-BSO) or typed text (OSD-BTO#1), the Avatar Model this window draws (OSD-B3O) and
//   the machine's ID (string); the Module answers with the translation as a Text Object and as the machine's Portable Avatar
//   (PAF-PAV), which carries it as text and as speech.
// The window shows the translation and renders the Portable Avatar on the avatar it keeps; the speech is made inside the Module.
public partial class MainWindow : Window
{
    private const string Module = "1MMC-MTS-V2.5-I01";

    private const string BSO = "OSD-BSO-V1.5";
    private const string BTO = "OSD-BTO-V1.5";
    private const string SEL = "OSD-SEL-V1.5";
    private const string B3O = "OSD-B3O-V1.5";
    private const string PAV = "PAF-PAV-V1.6";
    private const string MachineName = "Thalia";

    // The languages the speech engines speak and hear.
    private static readonly (string Code, string Name)[] Languages =
    {
        ("en", "English"), ("it", "Italiano"), ("es", "Espanol"), ("pt", "Portugues"), ("fr", "Francais"), ("de", "Deutsch")
    };

    private static readonly string AmdDir       = Mpai.Core.MpaiPaths.Amds;
    private static readonly string SettingsPath = Mpai.Core.MpaiPaths.Settings;
    private static readonly string AssetsDir    = Mpai.Core.MpaiPaths.Assets;

    private ControllerApi? _north;
    private AvatarUaHost?  _avatar;

    public MainWindow()
    {
        InitializeComponent();
        foreach (var (code, name) in Languages)
        {
            FromBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
            ToBox.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        }
        FromBox.SelectedIndex = 0; ToBox.SelectedIndex = 1;
        Loaded += OnLoaded;
    }

    private string From => (string)((ComboBoxItem)FromBox.SelectedItem).Tag;
    private string To   => (string)((ComboBoxItem)ToBox.SelectedItem).Tag;

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
                _north = new ControllerApi(AmdDir, SettingsPath, store => new MtsProvider(store));
                started = _north.StartFlow(Module);
            });
            if (started != AifError.OK) { SetStatus("could not start " + Module + ": " + started); return; }
            SpeakButton.IsEnabled = TranslateButton.IsEnabled = true;
            InstructionText.Text = "Choose the languages, then press Speak and say a sentence, or type it and press Enter.";
            SetStatus("Ready.");
        }
        catch (Exception fatal) { Program.Record("startup", fatal); SetStatus("startup failed: " + fatal.Message); }
    }

    private async void SpeakButton_Click(object sender, RoutedEventArgs e)
    {
        if (_avatar is null) return;
        var from = From;
        SetBusy(true); SetStatus("listening...");
        try
        {
            var speech = await Task.Run(() => _avatar!.CaptureSpeech());
            if (speech is null || speech.Data.Length == 0) { SetStatus("no speech captured."); return; }
            await TurnAsync(new ControllerApi.Datum(BSO, MpaiJson.ToJson(WithLanguage(speech, from))), "(spoken)");
        }
        catch (Exception ex) { Program.Record("speak", ex); SetStatus("error: " + ex.Message); }
        finally { SetBusy(false); }
    }

    private void TranslateButton_Click(object sender, RoutedEventArgs e) => _ = SendAsync();

    private void TextBoxIn_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && TranslateButton.IsEnabled) _ = SendAsync();
    }

    private async Task SendAsync()
    {
        var words = TextBoxIn.Text?.Trim();
        if (string.IsNullOrWhiteSpace(words)) return;
        var from = From;
        TextBoxIn.Clear();
        SetBusy(true);
        try { await TurnAsync(new ControllerApi.Datum(BTO, 1, MpaiJson.ToJson(WordsIn(words, from))), words); }
        catch (Exception ex) { Program.Record("send", ex); SetStatus("error: " + ex.Message); }
        finally { SetBusy(false); }
    }

    // One turn: the languages, the sentence, the Avatar Model and the machine's ID; the translation shown, the Portable Avatar presented.
    private async Task TurnAsync(ControllerApi.Datum said, string heard)
    {
        if (_north is null || _avatar is null) return;
        var languages = MpaiJson.ToJson(BasicSelectorObject.Languages(From, To));
        SetStatus("translating...");
        var inputs = new List<ControllerApi.Datum>
        {
            new ControllerApi.Datum(SEL, languages),
            said,
            new ControllerApi.Datum(B3O, 1, AvatarUtterance.AvatarDatum("cav-avatar.glb")),
            new ControllerApi.Datum("string", 1, System.Text.Json.JsonSerializer.Serialize(MachineName))
        };
        var r = await Task.Run(() => _north!.Advance(Module, inputs));
        if (!r.Ok) { Program.Record("advance", new Exception(r.Error.ToString())); SetStatus("no translation: " + r.Error); return; }

        var pav = r.ByType(PAV);
        var translation = TextOf(r.ByType(BTO)) ?? AvatarUtterance.TextOfPortableAvatar(pav) ?? "";
        AddLine(heard, translation);
        SetStatus("done.");

        if (AvatarUtterance.FromPortableAvatar(pav) is { } utterance)
        {
            await _avatar.PresentAsync(utterance);
            await Task.Delay(TimeSpan.FromSeconds(AvatarUaHost.WavDurationSeconds(utterance.MachineSpeechWav) + 0.3));
        }
        else SetStatus("the Portable Avatar carries no speech.");
    }

    private static string? TextOf(string? json)
    { if (string.IsNullOrWhiteSpace(json)) return null; try { return MpaiJson.FromJson<BasicTextObject>(json)?.GetText(); } catch { return null; } }

    private static BasicTextObject WordsIn(string words, string language) =>
        BasicTextObject.FromText(words, new TextQualifier
        {
            TextQualifierID = Guid.NewGuid().ToString(),
            Attributes = new TextAttributes { Language = new Language { LanguageCode = language, LanguageFormat = LanguageFormat.Iso639_1 } }
        });

    // The captured speech, with the language the person says they speak in its Qualifier.
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

    private void AddLine(string heard, string translation) => Dispatcher.Invoke(() =>
    {
        Transcript.AppendText($"{heard}{Environment.NewLine}  =>  {translation}{Environment.NewLine}{Environment.NewLine}");
        Transcript.ScrollToEnd();
    });

    private void SetBusy(bool b) => Dispatcher.Invoke(() => { SpeakButton.IsEnabled = !b; TranslateButton.IsEnabled = !b; });
    private void SetStatus(string s) => Dispatcher.Invoke(() => StatusText.Text = s);

    protected override void OnClosed(EventArgs e)
    { try { _north?.StopFlow(Module); } catch { } base.OnClosed(e); }
}
