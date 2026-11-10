// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;

using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Paf.Ebd;

// PAF-EBD-V1.6 - Entity Body Description. Receives a Body Object (a picture of one body) and gives the Body Descriptors Object of it: the 33 keypoints of the body (BlazePose), each x, y, z and
// visibility, as 132 float32 numbers in the Data (base64). The Qualifier names the format. PS-Body Interpretation (PAF-PBI) turns the keypoints into the Body Personal Status.
public sealed class EbdAimProcessor : IAimProcessor
{
    // A value of TFA/V1.5/formats/BodyDescriptorsContentFormats.json.
    public const string DescriptorFormat = "MediaPipe BlazePose Landmarks (33 keypoints: x, y, z, visibility)";

    private readonly PoseLandmarker _pose;
    private readonly string _inPort, _outPort;
    public string InstanceId { get; }

    public EbdAimProcessor(string instanceId, PoseLandmarker pose, AimPortReader ports)
    {
        InstanceId = instanceId; _pose = pose;
        _inPort = ports.Input("OSD-BVO-V1.5");
        _outPort = ports.Output("PAF-BDO-V1.6");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_inPort, out var json) || string.IsNullOrWhiteSpace(json))
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "PAF-EBD-V1.6: no Body Object to describe."));
        var visual = Mpai.Aif.PortData.PortObjects.ReadVisual(json);
        if (visual is null || visual.Data.Length == 0)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "PAF-EBD-V1.6: the Body Object holds no picture."));
        var keypoints = _pose.Detect(visual.Data);
        if (keypoints is null)
            return Task.FromResult(Message.Error(message.MessageId, InstanceId, "PAF-EBD-V1.6: no body found in the picture."));
        var outJson = MpaiJson.ToJson(BodyDescriptorsObject.FromContent(EmbeddingCodec.ToBase64(keypoints), DescriptorFormat));
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string> { [_outPort] = outJson } });
    }

    // The keypoints in a Body Descriptors Object, or why it is not of this format.
    public static float[] Keypoints(string bdoJson)
    {
        var bdo = MpaiJson.FromJson<BodyDescriptorsObject>(bdoJson) ?? throw new FormatException("The Body Descriptors Object cannot be read.");
        var format = bdo.GetContentFormat();
        if (format != DescriptorFormat) throw new FormatException($"The Body Descriptors Object is of format '{format ?? "(none)"}': this interpretation takes '{DescriptorFormat}'.");
        var data = bdo.Content() ?? throw new FormatException("The Body Descriptors Object carries no data.");
        var v = EmbeddingCodec.FromBase64(data);
        if (v.Length != PoseLandmarker.Keypoints * 4) throw new FormatException($"The Body Descriptors Object holds {v.Length} numbers, not {PoseLandmarker.Keypoints * 4}.");
        return v;
    }
}

public sealed class EbdPlugin : IAimPlugin
{
    private PoseLandmarker? _dep;
    public string AimName => "PAF-EBD-V1.6";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings)
        => new EbdAimProcessor(AimName, _dep ??= new PoseLandmarker(MpaiPaths.Given(settings, "PoseModel")), ports);
}
