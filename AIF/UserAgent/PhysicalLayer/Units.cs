using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.PhysicalLayer;

// THE PHYSICAL LAYER API (MPAI-AIF V3.0, User Agent 4): what a User Agent's Workflow
// interpreter calls to obtain data from the real world and to deliver data to it.
// Each Unit gives or takes an Object of a Data Type with its Qualifier. The Units
// acquire what a Qualifier asks for; which device serves it is the Unit's own matter.
// Where what is asked cannot be had, a Unit returns an Object with no data and a
// Qualifier saying what is available: nothing is substituted silently.
//
// These are the devices of THIS machine (Windows: WASAPI and WinMM through NAudio, the
// Windows camera, a WebView2 page for the avatar). A Unit never addresses an AIM and
// never reaches a Controller: what it acquires the User Agent gives to a Module's
// boundary Ports through the Controller API.

// MICROPHONE: a Basic Audio Object of a stated length, or - asserting that it is
// speech - a Basic Speech Object that ends when the speaker stops.
public sealed class MicrophoneUnit
{
    // Speech for recognition is 16 kHz mono 16-bit; other audio as the caller asks.
    public int SamplingFrequency { get; init; } = 16000;

    // A device takes a few hundred milliseconds to start, and a person answering
    // promptly speaks into that gap ("Yes, please" reached the recogniser as
    // "Please"): the wait is before the recording, so nothing spoken is lost.
    public TimeSpan Settle { get; init; } = TimeSpan.FromMilliseconds(400);

    // Audio of a stated length.
    public async Task<BasicAudioObject> AcquireAudioAsync(TimeSpan duration)
    {
        var mic = new WasapiAudioAcquisition(SamplingFrequency);
        return await mic.AcquireAsync(new AcquisitionRequest { Duration = duration });
    }

    // SPEECH, ending when the speaker stops (voice activity, by level): it waits for
    // speech to start - giving up after StartTimeout if nobody speaks - and stops when
    // the level stays low for Hangover; RunawayGuard caps the whole capture. The
    // claim that it is speech is made in the Qualifier: Source Real, Speaker Human,
    // and the Language the caller expects (only the caller knows what is about to be
    // spoken; recognition reads it from there). One capture, one fresh device: a
    // lingering one keeps buffered audio that would start the next capture at once.
    public TimeSpan StartTimeout { get; init; } = TimeSpan.FromSeconds(8);
    public TimeSpan Hangover { get; init; } = TimeSpan.FromMilliseconds(1200);
    public TimeSpan RunawayGuard { get; init; } = TimeSpan.FromSeconds(30);
    public double SpeakLevel { get; init; } = 0.004;   // the level is the RMS of a short chunk: a quiet USB microphone, whose speech peaks at -24 dBFS, gives about 0.01
    public double SilenceLevel { get; init; } = 0.0028;   // the room, at a peak of -42 dBFS, gives about 0.003

    // THE MICROPHONE STAYS OPEN (RingMicrophone), as the browser client's does: it keeps the last seconds it heard, so that a person who
    // answers the moment the avatar stops speaking - before this step has begun to listen - is not lost: what was said in the last
    // PrerollSpan is part of the capture. The avatar's own voice ended more than PrerollSpan before listening begins (the client waits
    // 0.8 s after it), so it is not taken for the person's.
    public TimeSpan PrerollSpan { get; init; } = TimeSpan.FromMilliseconds(700);

    public async Task<BasicSpeechObject> AcquireSpeechAsync(Language? language = null, CancellationToken cancel = default)
    {
        RingMicrophone.EnsureOpen(SamplingFrequency);
        var start = DateTime.UtcNow;

        // THE ROOM SETS THE LEVELS, from the quiet moments of the last seconds (a quarter of the way up their levels): speech starts well above
        // them and ends when the level is back near them. Bounded, so that a voice filling the seconds - her own - does not raise them far.
        var before = RingMicrophone.Last(TimeSpan.FromSeconds(3));
        var levels = before.Select(c => c.Level).OrderBy(l => l).ToList();
        double noise = levels.Count == 0 ? SilenceLevel : levels[levels.Count / 4];
        double speakLevel = Math.Clamp(2.0 * noise, SpeakLevel, 0.015), silenceLevel = Math.Clamp(1.4 * noise, SilenceLevel, 0.010);

        var ignoreBefore = RingMicrophone.IgnoreBefore;
        var preroll = RingMicrophone.Last(PrerollSpan).Where(c => c.At >= ignoreBefore).ToList();
        var kept = new List<byte[]>();
        bool started = preroll.Any(c => c.Level > speakLevel), early = started;
        if (started) kept.AddRange(RingMicrophone.Last(TimeSpan.FromMilliseconds(900)).Select(c => c.Pcm));   // with what lies at the edge of her last word: the person's first word may
        var lastAt = RingMicrophone.Last(TimeSpan.FromSeconds(1)).Select(c => c.At).DefaultIfEmpty(start).Max();
        double maxLevel = before.Select(c => c.Level).DefaultIfEmpty(0).Max();
        DateTime? quietSince = null;
        double speechPeak = preroll.Select(c => c.Level).DefaultIfEmpty(0).Max();
        while (!cancel.IsCancellationRequested)
        {
            await Task.Delay(25, CancellationToken.None);
            var now = DateTime.UtcNow;
            if (now - start > RunawayGuard) break;
            foreach (var c in RingMicrophone.Last(TimeSpan.FromSeconds(2)).Where(c => c.At > lastAt).ToList())
            {
                lastAt = c.At; maxLevel = Math.Max(maxLevel, c.Level);
                if (c.At < RingMicrophone.IgnoreBefore) continue;          // her own voice, still coming out of the loudspeakers
                if (!started)
                {
                    if (c.Level > speakLevel) { started = true; kept.AddRange(RingMicrophone.Last(TimeSpan.FromMilliseconds(900)).Select(x => x.Pcm)); }
                    continue;
                }
                kept.Add(c.Pcm);
                // QUIET IS RELATIVE TO THE PERSON'S OWN VOICE: a microphone with automatic gain lets the room's noise rise once the speaker stops, above a fixed
                // silence level - the capture then ran to its limit. A fifth of how loudly they spoke ends it; the room's level is the least.
                speechPeak = Math.Max(speechPeak, Math.Min(c.Level, 0.06));
                var quiet = Math.Max(silenceLevel, 0.2 * speechPeak);
                if (c.Level < quiet) { quietSince ??= c.At; if (c.At - quietSince.Value >= Hangover) goto Done; }
                else quietSince = null;
            }
            if (!started && now - start > StartTimeout) break;
        }
    Done:
        var pcm = kept.SelectMany(k => k).ToArray();
        pcm = NormaliseIfQuiet(pcm);
        // WHAT THE MICROPHONE GAVE, kept at every capture (with MPAI_DIAG=1, in the folder of Mpai.Core.MpaiDiag): the levels it was judged by, whether
        // speech was found (and whether it had begun before listening did), how long it ran, and the audio itself, playable.
        var stamp = DateTime.Now.ToString("HHmmss");
        Mpai.Core.MpaiDiag.Append("mic.log", $"{DateTime.Now:HH:mm:ss.fff}  capture {stamp}: room noise {noise:0.0000}, speech above {speakLevel:0.0000}, silence below {silenceLevel:0.0000}, loudest {maxLevel:0.0000}, speech found {started} (begun before listening: {early}), ran {(DateTime.UtcNow - start).TotalSeconds:0.0} s, {pcm.Length} bytes" + Environment.NewLine);
        Mpai.Core.MpaiDiag.DumpPcm(pcm, SamplingFrequency, $"mic_{stamp}");
        return BasicSpeechObject.FromData(started ? pcm : Array.Empty<byte>(), new SpeechQualifier
        {
            SpeechQualifierID = Guid.NewGuid().ToString(),
            // What the bytes are: raw PCM, no header.
            Format = new SpeechFormat
            {
                ContentFormats = new SpeechContentFormats { RawData = new Pcm { SamplingFrequency = SamplingFrequency, Precision = 16 } }
            },
            Attributes = new SpeechAttributes
            {
                Source = SpeechSource.Real,
                Metadata = new SpeechMetadata
                {
                    Language = language,
                    SpeakerProperties = new SpeakerProperties { SpeakerType = SpeakerType.Human, SpeakerCount = 1 }
                }
            }
        });
    }

    // A quiet recording is brought up to a healthy peak (the same as WasapiAudioAcquisition.NormalizeIfQuiet): a USB microphone whose own
    // gain is low gives speech at -30 dBFS, which the recogniser does not hear. Near silence is left alone: amplifying noise is wrong.
    private static byte[] NormaliseIfQuiet(byte[] pcm, float targetPeak = 0.85f, float triggerBelow = 0.5f)
    {
        float peak = 0;
        for (int i = 0; i + 1 < pcm.Length; i += 2) peak = Math.Max(peak, Math.Abs((short)(pcm[i] | (pcm[i + 1] << 8)) / 32768f));
        if (peak >= triggerBelow || peak < 0.005f) return pcm;
        float gain = Math.Min(targetPeak / peak, 100f);
        var o = new byte[pcm.Length];
        for (int i = 0; i + 1 < pcm.Length; i += 2)
        {
            int s = (int)Math.Round((short)(pcm[i] | (pcm[i + 1] << 8)) * gain);
            s = Math.Clamp(s, short.MinValue, short.MaxValue);
            o[i] = (byte)(s & 0xFF); o[i + 1] = (byte)((s >> 8) & 0xFF);
        }
        return o;
    }
}
// CAMERA: a Basic Visual Object - a still of the person in front of the device.
public sealed class CameraUnit
{
    public int CameraIndex { get; init; }

    // A face, or null when the camera gave nothing.
    public async Task<BasicVisualObject?> AcquireFaceAsync()
    {
        var frame = await new WebcamVisualAcquisition(CameraIndex)
            .AcquireAsync(new VisualAcquisitionRequest { VisualObjectType = "Face" });
        return frame.Data is { Length: > 0 } data ? BasicVisualObject.FromFile("webcam.jpg", data, "Face") : null;
    }
}

// LOUDSPEAKER: plays a Basic Speech Object.
public sealed class LoudspeakerUnit
{
    public Task DeliverAsync(BasicSpeechObject speech) => new WinmmSpeechDelivery().DeliverAsync(speech);

    // AN AUDIO OBJECT OR AN AUDIO SCENE, as the User hears it (the author, 2026/10/04:
    // "AOD is UA's business"): rendered from the User Point of View by the spatial
    // renderer (Mpai.SpatialAudio, Steam Audio in steamAudioFolder), then played.
    // What was played is returned.
    public async Task<BasicAudioObject> DeliverAsync(string objectOrScene, string steamAudioFolder,
                                                     Mpai.SpatialAudio.SpatialRenderer.Layout layout = Mpai.SpatialAudio.SpatialRenderer.Layout.Binaural)
    {
        var heard = new Mpai.SpatialAudio.AudioRendering(steamAudioFolder, layout).Render(objectOrScene);
        await PlayAsync(heard.Data);
        return heard;
    }

    // A WAV, played to the end.
    public static async Task PlayAsync(byte[] wav)
    {
        if (wav.Length == 0) return;
        using var reader = new NAudio.Wave.WaveFileReader(new MemoryStream(wav));
        using var output = new NAudio.Wave.WaveOutEvent();
        output.Init(reader);
        output.Play();
        while (output.PlaybackState == NAudio.Wave.PlaybackState.Playing)
            await Task.Delay(100);
    }
}

// AVATAR: the 3D page that draws and animates the Portable Avatar (cav-webview.html,
// in a WebView2 the application owns): it plays a Portable Avatar unpacked - the
// speech with the Face and Body Descriptors - and draws a 3D Model Scene from a Point
// of View. post sends one message to the page.
public sealed class AvatarUnit(Func<string, Task> post)
{
    private readonly WebView3DModelDelivery page = new(post);

    public Task DeliverAsync(byte[] speechWav, FaceDescriptorsObject? face, BodyDescriptorsObject? body) =>
        page.DeliverWithSpeechAsync(Basic3DModelObject.FromData([]), face, speechWav, body);
}
