using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

using Mpai.Core;
using Mpai.Ua;
using Mpai.UaKit;

namespace Mpai.Cae.AsmUa;

// THE DEVICES OF THIS USER AGENT, each bound by the workflow to a Port of the Module by Data Type and Port Number. They hold nothing of the Module.

// A DEVICE THAT GIVES WHAT THE PERSON GAVE, as they give it, for the life of the workflow: a sound loaded from a file (a User Command), words typed.
public sealed class GivingDevice(string dataType) : IDevice
{
    private readonly Channel<string> _queue = Channel.CreateUnbounded<string>();
    public void Give(string json) { App.Log($"[device giving {dataType}] {(json.Length > 120 ? json[..120] + "..." : json)}"); _queue.Writer.TryWrite(json); }
    public Task DeliverAsync(string type, string json, CancellationToken cancel) => Task.CompletedTask;
    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        await foreach (var json in _queue.Reader.ReadAllAsync(cancel)) yield return (dataType, json);
    }
    public Task SafeStopAsync() => Task.CompletedTask;
}

// A DEVICE THAT ACTS ON WHAT THE MODULE GIVES: the window shows it, says it, draws it.
public sealed class ActingDevice(string name, Func<string, Task> act) : IDevice
{
    public async Task DeliverAsync(string type, string json, CancellationToken cancel)
    {
        App.Log($"[device {name}] delivered {type}: {(json.Length > 120 && !json.Contains("\"AddedObjects\"") ? json[..120] + "..." : json)}");
        try { await act(json); }
        catch (Exception e) { App.Record("device " + name, e); App.Log($"[device {name}] FAILED: {e.Message}"); }
    }
    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel) { await Task.Delay(Timeout.Infinite, cancel).ConfigureAwait(false); yield break; }
    public Task SafeStopAsync() => Task.CompletedTask;
}

// WHAT THE PERSON SAYS: the microphone, with voice-activity detection. It takes in speech and nothing else; the Module is given a Speech Object at its Port and the recognition is the Module's.
// What the person types is the text device's (TypedIn).
public sealed class VoiceDevice : IDevice
{
    private readonly SpeechListener _ears;
    private readonly Func<bool> _microphoneOn;
    private readonly Action<string> _say;

    // ONE REQUEST AT A TIME IN THE RECOGNISER: what is given to the Module waits for its words (Heard) before the next is given; what the microphone hears meanwhile is dropped, not piled up.
    // If the words do not come in time, the recogniser is taken to be stuck: that is said, and the owner of this device is told (TimedOut).
    private DateTime? _sentAt;
    public Action? TimedOut { get; set; }
    public TimeSpan Patience { get; set; } = TimeSpan.FromSeconds(25);
    public void Heard() => _sentAt = null;
    private bool Busy => _sentAt is { } at && DateTime.UtcNow - at < Patience;
    private bool Gone(int bytes, int rate) => bytes < 0.4 * rate * 2 || bytes > 30 * rate * 2;      // under 0.4 s: a click; over thirty seconds: not an utterance

    // ONE UTTERANCE, WHOLE: what the microphone captures in pieces - a person pauses to think, and the capture ends at 1.2 s of quiet - is joined while the pieces come within 1.5 s of one
    // another; the request goes when 1.5 s have passed with nothing more, and when the recogniser is free. Nothing is dropped for being busy: it waits.
    public static readonly TimeSpan Joining = TimeSpan.FromSeconds(1.0);
    private BasicSpeechObject? _pending;
    private DateTime _pendingAt;
    private readonly object _gate = new();

    public static BasicSpeechObject Join(BasicSpeechObject first, BasicSpeechObject next) =>
        BasicSpeechObject.FromData([.. first.Data, .. next.Data], first.SpeechQualifier!);

    public VoiceDevice(SpeechListener ears, Func<bool> microphoneOn, Action<string> say)
    { _ears = ears; _microphoneOn = microphoneOn; _say = say; }

    public Task DeliverAsync(string type, string json, CancellationToken cancel) => Task.CompletedTask;
    public Task SafeStopAsync() => Task.CompletedTask;

    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel)
    {
        var heard = Channel.CreateUnbounded<string>();
        var microphone = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                if (!_microphoneOn()) { await Task.Delay(300, CancellationToken.None); continue; }
                try
                {
                    var speech = await _ears.ListenAsync(cancel, CancellationToken.None);
                    var rate = speech?.SpeechQualifier?.Format?.ContentFormats?.RawData?.SamplingFrequency is { } r and > 0 ? (int)r : 16000;
                    if (speech is not null && !_microphoneOn()) App.Log("[voice] speech heard while the microphone is off: dropped");
                    else if (speech is not null && Gone(speech.Data.Length, rate)) App.Log($"[voice] a capture of {speech.Data.Length} bytes at {rate} Hz is a click or the noise of the room: dropped");
                    else if (speech is not null)
                    {
                        lock (_gate)
                        {
                            var joined = _pending is not null;      // whatever waits is the same sentence: it is sent only when nobody is speaking
                            App.Log($"[voice] capture of {speech.Data.Length} bytes ({speech.Data.Length / 2.0 / rate:0.0} s)" + (joined ? $", joined to the {_pending!.Data.Length} bytes before it" : ""));
                            _pending = joined ? Join(_pending!, speech) : _pending is null ? speech : _pending;
                            if (!joined && !ReferenceEquals(_pending, speech)) _pending = Join(_pending, speech);      // (a piece that waited a long while is still the same utterance if not sent)
                            _pendingAt = DateTime.UtcNow;
                        }
                    }
                }
                catch (Exception e)
                {
                    App.Record("microphone", e);
                    _say("The microphone does not work. Type what you want to say instead.");
                    await Task.Delay(5000, CancellationToken.None);
                }
            }
        }, CancellationToken.None);
        // THE UTTERANCE GOES when nothing has been joined to it for 1.5 s and the recogniser is free.
        var sender = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(100, CancellationToken.None);
                BasicSpeechObject? send = null;
                lock (_gate)
                    if (_pending is not null && DateTime.UtcNow - _pendingAt >= Joining && !Busy && !Mpai.PhysicalLayer.MicrophoneUnit.SpeechUnderway) { send = _pending; _pending = null; }
                if (send is null) continue;
                App.Log($"[voice] utterance of {send.Data.Length} bytes given to the Module");
                _sentAt = DateTime.UtcNow;
                heard.Writer.TryWrite(MpaiJson.ToJson(send));
            }
        }, CancellationToken.None);

        // THE WATCHDOG: words that do not come in time are not waited for for ever.
        var watchdog = Task.Run(async () =>
        {
            while (!cancel.IsCancellationRequested)
            {
                await Task.Delay(1000, CancellationToken.None);
                if (_sentAt is { } at && DateTime.UtcNow - at >= Patience)
                {
                    _sentAt = null;
                    App.Log($"[voice] no words came back within {Patience.TotalSeconds:0} s: the recogniser is taken to be stuck");
                    try { TimedOut?.Invoke(); } catch (Exception e) { App.Record("watchdog", e); }
                }
            }
        }, CancellationToken.None);
        await foreach (var json in heard.Reader.ReadAllAsync(cancel)) yield return ("OSD-BSO-V1.5", json);
    }

    // A WAV as the Speech Object the Module is given: the file format and what the bytes hold are stated.
    public static string SpeechOf(byte[] wav)
    {
        int rate = wav.Length > 28 ? BitConverter.ToInt32(wav, 24) : 22050, bits = wav.Length > 36 ? BitConverter.ToInt16(wav, 34) : 16;
        var qualifier = new SpeechQualifier
        {
            SpeechQualifierID = Guid.NewGuid().ToString(),
            Format = new SpeechFormat
            {
                ContentFormats = new SpeechContentFormats { RawData = new Pcm { SamplingFrequency = rate, Precision = bits } },
                TransportFormats = new SpeechTransportFormats { FileFormat = SpeechFileFormat.Wav }
            },
            Attributes = new SpeechAttributes { Source = SpeechSource.Synthetic }
        };
        return MpaiJson.ToJson(BasicSpeechObject.FromData(wav, qualifier));
    }
}
