using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Threading;

using Microsoft.Web.WebView2.Wpf;
using Microsoft.Web.WebView2.Core;

using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Aif.Api;          // AvatarUtterance
using Mpai.PhysicalLayer;    // the Units: Microphone, Avatar

namespace Mpai.UaKit;

// AvatarUaHost - the reusable plumbing shared by User Agent applications that
// present a Portable Avatar. It drives two Units of the User Agent's Physical Layer
// (MPAI-AIF V3.0) - never an AIM:
//   - the Avatar Unit: the WebView-hosted 3D page, to which it delivers a Speaking
//     Avatar (speech + Face and Body Descriptors) for playback and lip-sync;
//   - the Microphone Unit: speech with voice-activity auto-stop, driving a
//     continuous listen loop.
// The application supplies only two things: the WebView2 control to render into, and
// a per-turn handler that takes the captured Speech Object and returns the Speaking
// Avatar to present (typically a single call to the HCI API - Converse, Translate, ...).
// This removes the duplicated WebView/capture/present code from each UA app.
public sealed class AvatarUaHost
{
    private readonly WebView2   _web;
    private readonly Dispatcher _ui;
    private readonly string     _amdDir;
    private readonly string     _assetsHost;   // virtual host name (e.g. "cavapp.local")
    private readonly string     _assetsDir;    // folder mapped to the virtual host (viewer + avatar)

    private AvatarUnit? _renderer;
    private readonly MicrophoneUnit _microphone = new();
    private volatile bool           _running;

    // Raised when the listen loop starts or stops (true = running), on the UI thread,
    // so the app can update its Listen/Stop button.
    public event Action<bool>? RunningChanged;

    public bool IsRunning => _running;

    public AvatarUaHost(
        WebView2   web,
        Dispatcher ui,
        string     amdDir,
        string     assetsDir,
        string     assetsHost = "cavapp.local")
    {
        _web        = web;
        _ui         = ui;
        _amdDir     = amdDir;
        _assetsDir  = assetsDir;
        _assetsHost = assetsHost;
    }

    // Initialise the WebView2 renderer: allow audio autoplay (the speech plays from a
    // message handler, not an in-page click), map the assets virtual host, and load
    // the avatar viewer. Call once, after the window is loaded.
    public async Task InitAsync(string viewerPage = "cav-webview.html")
    {
        // A DEDICATED user-data folder per application. WebView2 reuses an existing
        // environment when the user-data folder matches, and the FIRST environment's
        // browser arguments win - so a shared (default) folder makes the autoplay
        // flag below be ignored, and the avatar's speech is silently blocked (the
        // face still animates - only audio is autoplay-gated). A per-app folder makes
        // the --autoplay-policy flag actually take effect, so the avatar can speak
        // without a preceding user gesture.
        var appName = System.Reflection.Assembly.GetEntryAssembly()?.GetName().Name ?? "UaKit";
        var userDataFolder = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), "MpaiUaKit", appName);
        System.IO.Directory.CreateDirectory(userDataFolder);

        var env = await CoreWebView2Environment.CreateAsync(null, userDataFolder,
            new CoreWebView2EnvironmentOptions("--autoplay-policy=no-user-gesture-required"));
        await _web.EnsureCoreWebView2Async(env);
        _web.CoreWebView2.Settings.IsWebMessageEnabled = true;
        _web.CoreWebView2.IsMuted = false;   // never mute the avatar's voice
        _web.CoreWebView2.SetVirtualHostNameToFolderMapping(
            _assetsHost, _assetsDir, CoreWebView2HostResourceAccessKind.Allow);
        _web.CoreWebView2.Navigate($"https://{_assetsHost}/{viewerPage}");

        _renderer = new AvatarUnit(json =>
            _ui.InvokeAsync(() => _web.CoreWebView2.PostWebMessageAsJson(json)).Task);
    }

    // Present one utterance of the avatar on the renderer (used for a single turn, e.g. typed input).
    public async Task PresentAsync(AvatarUtterance avatar)
    {
        if (_renderer is null) return;
        // The microphone is deaf to her own voice until it has ended (and for a moment after: the loudspeaker is behind the sound).
        Mpai.PhysicalLayer.RingMicrophone.IgnoreBefore = DateTime.UtcNow + TimeSpan.FromSeconds(WavDurationSeconds(avatar.MachineSpeechWav));
        await _ui.InvokeAsync(() => _renderer.DeliverAsync(avatar.MachineSpeechWav, avatar.FaceDescriptors, avatar.BodyDescriptors));
    }

    // THE AVATAR FALLS SILENT. What she is saying is cut off at once: the audio plays in the renderer's page, and a workflow that is
    // stopped stops waiting for her, not her speaking. Left to finish, a long reading goes on - and the microphone, listening for the
    // answer to the next question, takes her own voice for the person's.
    public void Silence()
    {
        Mpai.PhysicalLayer.RingMicrophone.IgnoreBefore = DateTime.UtcNow.AddMilliseconds(300);   // she stopped: the microphone is deaf only for the loudspeaker's last moment
        try
        {
            _ = _ui.InvokeAsync(() => _web.CoreWebView2?.ExecuteScriptAsync(
                "(function(){var a=document.getElementById('responseAudio'); if(a){a.pause(); a.currentTime=0;}})()"));
        }
        catch { }
    }

    // Start the continuous listen loop: capture a spoken turn (VAD auto-stop) -> hand it
    // to the app's handler -> present the returned Portable Avatar -> wait for the avatar
    // to finish speaking -> listen again, until StopLoop. Empty captures (nobody spoke)
    // simply listen again. The handler runs off the UI thread.
    public void StartLoop(Func<BasicSpeechObject, AvatarUtterance> handleTurn)
    {
        if (_running) return;
        _running = true;
        RunningChanged?.Invoke(true);
        _ = Task.Run(() => LoopAsync(handleTurn));
    }

    public void StopLoop()
    {
        if (!_running) return;
        _running = false;
        RunningChanged?.Invoke(false);
    }

    private async Task LoopAsync(Func<BasicSpeechObject, AvatarUtterance> handleTurn)
    {
        try
        {
            while (_running)
            {
                var speech = CaptureSpeech();
                if (!_running) break;
                if (speech is null || speech.Data.Length == 0) continue;

                var avatar = await Task.Run(() => handleTurn(speech));
                await PresentAsync(avatar);

                // Let the avatar finish speaking before listening again, so the mic
                // does not capture its own voice.
                var speakSeconds = WavDurationSeconds(avatar.MachineSpeechWav);
                await Task.Delay(TimeSpan.FromSeconds(speakSeconds + 0.6));
            }
        }
        catch (Exception ex)
        {
            await _ui.InvokeAsync(() => System.Windows.MessageBox.Show(ex.ToString(), "UA error"));
        }
        finally { StopLoop(); }
    }

    // Capture one spoken turn from the microphone as a Speech Object, with voice-
    // activity auto-stop. The recognition happens inside the Module, not here.
    public BasicSpeechObject? CaptureSpeech()
    {
        var speech = _microphone.AcquireSpeechAsync().GetAwaiter().GetResult();
        return speech.Data.Length == 0 ? null : speech;
    }

    // CaptureAudio - the AUDIO ear. Unlike CaptureSpeech (which asserts speech),
    // this captures raw sound and returns a Basic AUDIO Object (OSD-BAO): whether it
    // is speech is decided downstream (the HCI Module's ASI). A fixed window.
    public BasicAudioObject? CaptureAudio(double seconds = 5.0) =>
        _microphone.AcquireAudioAsync(TimeSpan.FromSeconds(seconds)).GetAwaiter().GetResult();

    // Duration of a 16-bit PCM WAV, in seconds, from its bytes.
    public static double WavDurationSeconds(byte[] wav)
    {
        try
        {
            if (wav.Length < 44) return 1.0;
            int channels   = BitConverter.ToInt16(wav, 22);
            int sampleRate = BitConverter.ToInt32(wav, 24);
            int bits       = BitConverter.ToInt16(wav, 34);
            if (channels <= 0 || sampleRate <= 0 || bits <= 0) return 1.0;
            int pos = 12, dataLen = wav.Length - 44;
            while (pos + 8 <= wav.Length)
            {
                string id = System.Text.Encoding.ASCII.GetString(wav, pos, 4);
                int len = BitConverter.ToInt32(wav, pos + 4);
                if (id == "data") { dataLen = Math.Min(len, wav.Length - (pos + 8)); break; }
                pos += 8 + len + (len & 1);
            }
            double bytesPerSec = sampleRate * channels * (bits / 8.0);
            return bytesPerSec > 0 ? dataLen / bytesPerSec : 1.0;
        }
        catch { return 1.0; }
    }
}
