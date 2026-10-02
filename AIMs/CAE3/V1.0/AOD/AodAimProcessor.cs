using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Aims.Audio.Spatial;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Aims.Audio;

// CAE-AOD-V1.0 - Audio Object Delivery: an audio input, a scene input and a User
// Command (the author, 2026/10/02); it plays Objects and Scenes.
//
//   - A Basic Audio Object is played as it is, unless it states where it is heard
//     from (its UserPoV): then it is heard from there, its directional pattern
//     counting.
//   - An Audio Object is heard as a scene of its Basic Audio Objects, from its UserPoV.
//   - A Basic Audio Scene or an Audio Scene is rendered from its UserPoV - each
//     member from its own, if it has one - by the spatial renderer (Steam Audio):
//     distance, air, directional patterns, binaural or onto the speaker layout of
//     the setting "Layout".
//
// What is played is also the output, a Basic Audio Object: the rendered audio as a
// 16-bit PCM WAV at 48 kHz. One Port takes either kind of Object, one either kind
// of Scene; the Header says which arrived.
public sealed class AodAimProcessor : IAimProcessor
{
    private readonly string            _audioPort;
    private readonly string            _scenePort;
    private readonly string            _outputPort;
    private readonly IAudioDeliveryAim _aod;
    private readonly string            _steamAudio;
    private readonly SpatialRenderer.Layout _layout;

    public string InstanceId { get; }

    public AodAimProcessor(
        string            instanceId,
        IAudioDeliveryAim aod,
        AimPortReader     ports,
        string?           steamAudioFolder = null,
        string?           layout = null)
    {
        InstanceId  = instanceId;
        _aod        = aod;
        _audioPort  = ports.Input("OSD-BAO-V1.5");                       // or OSD-AUO-V1.5: one Port
        _scenePort  = ports.InputOrDefault("OSD-BAS-V1.5", "");          // or OSD-ASD-V1.5: one Port
        _outputPort = ports.Output("OSD-BAO-V1.5");
        _steamAudio = steamAudioFolder ?? MpaiPaths.Resolve("Models/SteamAudio");
        _layout     = Enum.TryParse<SpatialRenderer.Layout>(layout, true, out var l) ? l : SpatialRenderer.Layout.Binaural;
    }

    public async Task<Message> ProcessAsync(Message message)
    {
        BasicAudioObject? played = null;

        if (_scenePort.Length > 0 && message.Ports.TryGetValue(_scenePort, out var sceneJson) && !string.IsNullOrWhiteSpace(sceneJson))
        {
            var members = HeaderOf(sceneJson) == "OSD-BAS-V1.5"
                ? SceneAudio.Members(MpaiJson.FromJson<BasicAudioSceneDescriptors>(sceneJson))
                : SceneAudio.Members(MpaiJson.FromJson<AudioSceneDescriptors>(sceneJson));
            played = Render(members.ToList(), "scene");
        }
        else if (message.Ports.TryGetValue(_audioPort, out var audioJson) && !string.IsNullOrWhiteSpace(audioJson))
        {
            if (HeaderOf(audioJson) == "OSD-AUO-V1.5")
            {
                var auo = MpaiJson.FromJson<AudioObject>(audioJson);
                played = Render(SceneAudio.Members(auo).ToList(), auo.AudioObjectID);
            }
            else
            {
                var bao = MpaiJson.FromJson<BasicAudioObject>(audioJson);
                played = bao.UserPoV is null
                    ? bao
                    : Render([new SceneAudio.Member(bao, new double[3], 0, 0, bao.UserPoV)], bao.BasicAudioObjectID);
            }
        }

        if (played is null)
            return Message.Error(message.MessageId, InstanceId, "nothing to play: no Audio Object and no Audio Scene");

        await _aod.DeliverAsync(played);
        var json = MpaiJson.ToJson(played);
        return new Message
        {
            MessageId   = message.MessageId,
            MessageType = "BasicAudioObject",
            DataType    = "OSD-BAO-V1.5",
            Payload     = json,
            Ports       = new Dictionary<string, string> { [_outputPort] = json }
        };
    }

    // The members as the user hears them: each group of members heard from the same
    // place is rendered, and the groups are mixed.
    private BasicAudioObject Render(IReadOnlyList<SceneAudio.Member> members, string what)
    {
        if (!SpatialRenderer.Available(_steamAudio))
            throw new InvalidOperationException($"CAE-AOD: no spatial renderer - Steam Audio is not in {_steamAudio}.");

        using var renderer = new SpatialRenderer(_steamAudio, _layout);
        float[] mix = [];
        foreach (var group in members.GroupBy(m => m.User))
        {
            var sources = group.Select(m => new SpatialRenderer.Source(
                SceneAudio.Mono(m.Object.Data, SpatialRenderer.Rate), m.Position, m.YawDeg, m.PitchDeg, 1,
                DirectionalPattern.FromPlot(m.Object.BasicAudioObjectProperties?.AcousticProfile?.DirectionalPatterns))).ToList();
            var part = renderer.Render(sources, SceneAudio.UserOf(group.Key));
            if (part.Length > mix.Length) Array.Resize(ref mix, part.Length);
            for (var i = 0; i < part.Length; i++) mix[i] += part[i];
        }

        var qualifier = new AudioQualifier
        {
            AudioQualifierID = Guid.NewGuid().ToString(),
            SubTypes = "Mixed",   // a rendering is a mix of its sources
            Formats = new AudioFormats
            {
                ContentFormat = new AudioContentFormat { RawData = new AudioRawData { SampleSpace = new Pcm { SamplingFrequency = SpatialRenderer.Rate, Precision = 16 } } },
                TransportFormat = new AudioTransportFormat { FileFormats = AudioFileFormat.Wav }
            },
            Attributes = new AudioAttributes
            {
                Device = new AudioDevice
                {
                    DeviceRole = "Render",
                    CaptureConfiguration = new CaptureConfiguration { ChannelCount = renderer.Channels, SamplingMode = renderer.Channels switch { 1 => "Mono", 2 => "Stereo", _ => "MultiChannel" } }
                }
            }
        };
        var played = BasicAudioObject.FromData(SceneAudio.Wav(mix, renderer.Channels, SpatialRenderer.Rate), qualifier);
        return new BasicAudioObject
        {
            BasicAudioObjectID = played.BasicAudioObjectID,
            BasicAudioObjectData = played.BasicAudioObjectData,
            AudioQualifier = played.AudioQualifier,
            DescrMetadata = $"{what} rendered for the user ({_layout})"
        };
    }

    private static string? HeaderOf(string json)
    {
        try { return JsonNode.Parse(json)?["Header"]?.GetValue<string>(); }
        catch { return null; }
    }
}
