using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using AIF.Controller;

namespace Mpai.Cae.Usc.Eae;

// CAE-AAT-V2.5, Audio Analysis Transform: the Audio Object (the multichannel audio of a microphone array, with the geometry of the array in its Qualifier) in; the Audio Object in the
// transform domain out - the spectrum of each channel for each block, the block size that of the array's geometry (512 where there is none), the geometry carried on to the next AIM.
public sealed class AatAimProcessor(string instanceId, string audioKey, string outputKey) : IAimProcessor
{
    public const int DefaultBlockSize = 512;
    public string InstanceId { get; } = instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(audioKey, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"CAE-AAT-V2.5: no Audio Object on input port '{audioKey}'."));
        try
        {
            var (audio, geometry) = AudioObject.ReadMultichannel(json);
            if (geometry is not null && geometry.Count != audio.Channels.Length) throw new FormatException($"The array has {geometry.Count} microphones and the audio {audio.Channels.Length} channels.");
            var blocks = Stft.Analyse(audio, geometry?.BlockSize ?? DefaultBlockSize);
            var text = TransformObject.Make("aat-" + Guid.NewGuid().ToString("N")[..12], blocks, "Transform", geometry?.ToJson());
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "BasicAudioObject", DataType = AudioObject.Header, Payload = text, Ports = new Dictionary<string, string> { [outputKey] = text } });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-AAT-V2.5: " + ex.Message));
        }
    }
}

// CAE-AST-V2.5, Audio Synthesis Transform: the Enhanced Audio Object in the transform domain in; the Enhanced Audio Objects in the time domain out - the inverse transform. Each channel of
// the transform (a separated source) is an Enhanced Audio Object of its own: a mono audio file, its ID "source-<n>", in the Audio Object it gives.
public sealed class AstAimProcessor(string instanceId, string transformKey, string outputKey) : IAimProcessor
{
    public string InstanceId { get; } = instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(transformKey, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, $"CAE-AST-V2.5: no Enhanced Audio Object (transform) on input port '{transformKey}'."));
        try
        {
            var (blocks, _) = TransformObject.Read(json);
            var channels = Stft.Synthesise(blocks);
            var objects = channels.Select((x, s) => new AudioObject.Item($"source-{s + 1}", new Audio(Stft.ToSamples(x), blocks.Rate))).ToArray();
            var text = AudioObject.Make("ast-" + Guid.NewGuid().ToString("N")[..12], objects);
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "BasicAudioObject", DataType = AudioObject.Header, Payload = text, Ports = new Dictionary<string, string> { [outputKey] = text } });
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or InvalidOperationException)
        {
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "CAE-AST-V2.5: " + ex.Message));
        }
    }
}
