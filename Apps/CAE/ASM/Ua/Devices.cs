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
        App.Log($"[device {name}] delivered {type}: {(json.Length > 120 ? json[..120] + "..." : json)}");
        try { await act(json); }
        catch (Exception e) { App.Record("device " + name, e); App.Log($"[device {name}] FAILED: {e.Message}"); }
    }
    public async IAsyncEnumerable<(string DataType, string Json)> ReadAsync([EnumeratorCancellation] CancellationToken cancel) { await Task.Delay(Timeout.Infinite, cancel).ConfigureAwait(false); yield break; }
    public Task SafeStopAsync() => Task.CompletedTask;
}

// WHAT THE PERSON SAYS: the microphone, with voice-activity detection, or the words they type, spoken by the machine's voice and given as speech. Either way the Module is given a
// Speech Object at its Port; the recognition is the Module's.
public sealed class VoiceDevice : IDevice
{
    private readonly SpeechListener _ears;
    private readonly Func<string, Task<byte[]?>> _synthesise;
    private readonly Func<bool> _microphoneOn;
    private readonly Action<string> _say;
    private readonly Channel<string> _typed = Channel.CreateUnbounded<string>();

    public VoiceDevice(SpeechListener ears, Func<string, Task<byte[]?>> synthesise, Func<bool> microphoneOn, Action<string> say)
    { _ears = ears; _synthesise = synthesise; _microphoneOn = microphoneOn; _say = say; }

    public void Type(string words) { App.Log("[voice] typed: " + words); _typed.Writer.TryWrite(words); }

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
                    if (speech is not null && !_microphoneOn()) App.Log("[voice] speech heard while the microphone is off: dropped");
                    else if (speech is not null) { App.Log("[voice] speech heard by the microphone: " + speech.Data.Length + " bytes"); heard.Writer.TryWrite(MpaiJson.ToJson(speech)); }
                }
                catch (Exception e)
                {
                    App.Record("microphone", e);
                    _say("The microphone does not work. Type what you want to say instead.");
                    await Task.Delay(5000, CancellationToken.None);
                }
            }
        }, CancellationToken.None);
        var typing = Task.Run(async () =>
        {
            try
            {
                await foreach (var words in _typed.Reader.ReadAllAsync(cancel))
                    if (await _synthesise(words) is { } wav) { App.Log("[voice] typed words spoken: " + wav.Length + " bytes, given to the Module"); heard.Writer.TryWrite(SpeechOf(wav)); }
                    else _say("I could not turn the typed words into speech.");
            }
            catch (OperationCanceledException) { }
            catch (Exception e) { App.Record("typed words", e); App.Log("[voice] typed words FAILED: " + e.Message); _say("The typed words could not be given: " + e.Message); }
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
