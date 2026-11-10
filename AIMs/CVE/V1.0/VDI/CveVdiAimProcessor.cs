using System;
using System.Collections.Generic;
using System.Threading.Tasks;

using AIF.Controller;
using Mpai.Core;
using Mpai.Paf.Ebd;          // the keypoints of a Body Descriptors Object

namespace Mpai.Cve.Vdi;

// CVE-VDI-V1.0 - Visual Direction Identification.
//
// From the Body Descriptors of an Entity (PAF-BDO) finds the Point of View (OSD-OPV) the Entity indicates with the index finger: the line from the wrist
// through the tip of the index finger of the hand that reaches farther from its shoulder. The Visual Scene Geometry (OSD-VSG) is a required input of the AIM;
// the Point of View is computed from the body alone, so the geometry is not used (what the geometry would add is not stated).
//
// WHAT THE STANDARD DOES NOT SAY, AND WHAT THIS AIM ASSUMES (to be confirmed):
//  - the Body Descriptors Object carries no reference frame, so its keypoints (the BlazePose landmarks of PAF-EBD: x, y, z, visibility) are taken to be
//    in the frame of the Visual Scene Geometry: x to the right, y down, z away from the camera;
//  - the Orientation of a Point of View is "three Euler angles": here (azimuth, elevation, 0) in degrees, azimuth = atan2(dx, dz) about the vertical
//    axis, elevation = angle above the horizontal (y is down, so elevation = asin(-dy)); the Visual Object Extraction reads it the same way;
//  - the Aperture of the Point of View is the cone in which an object counts as crossed: Aperture degrees (twice the half-angle), 10 here.
public sealed class CveVdiAimProcessor : IAimProcessor
{
    public const double ApertureDegrees = 10;            // twice the half-angle of the cone in which an object is crossed
    private const float MinimumVisibility = 0.5f;
    private const int LeftShoulder = 11, RightShoulder = 12, LeftWrist = 15, RightWrist = 16, LeftIndex = 19, RightIndex = 20;

    private readonly string _instanceId;
    private readonly string _bodyPort, _geometryPort, _outPort;

    public CveVdiAimProcessor(string instanceId, AimPortReader ports)
    {
        _instanceId   = instanceId;
        _bodyPort     = ports.Input("PAF-BDO-V1.6");
        _geometryPort = ports.Input("OSD-VSG-V1.5");
        _outPort      = ports.Output("OSD-OPV-V1.5");
    }

    public string InstanceId => _instanceId;

    public Task<Message> ProcessAsync(Message message)
    {
        if (!message.Ports.TryGetValue(_bodyPort, out var bdo) || string.IsNullOrWhiteSpace(bdo))
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no Body Descriptors Object on input port"));
        if (!message.Ports.ContainsKey(_geometryPort))
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no Visual Scene Geometry on input port"));

        float[] k;
        try { k = EbdAimProcessor.Keypoints(bdo); }
        catch (FormatException e) { return Task.FromResult(Message.Error(message.MessageId, _instanceId, e.Message)); }

        // The hand that points: the index finger, the wrist and the shoulder seen, and the finger farther from its shoulder.
        int best = -1; double reach = -1;
        foreach (var (shoulder, wrist, index) in new[] { (LeftShoulder, LeftWrist, LeftIndex), (RightShoulder, RightWrist, RightIndex) })
        {
            if (k[index * 4 + 3] < MinimumVisibility || k[wrist * 4 + 3] < MinimumVisibility || k[shoulder * 4 + 3] < MinimumVisibility) continue;
            var r = Distance(k, shoulder, index);
            if (r > reach) { reach = r; best = index; }
        }
        if (best < 0)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "no index finger, wrist and shoulder seen: no direction is indicated"));
        var wristOfBest = best == LeftIndex ? LeftWrist : RightWrist;

        double dx = k[best * 4] - k[wristOfBest * 4], dy = k[best * 4 + 1] - k[wristOfBest * 4 + 1], dz = k[best * 4 + 2] - k[wristOfBest * 4 + 2];
        var length = Math.Sqrt(dx * dx + dy * dy + dz * dz);
        if (length < 1e-6)
            return Task.FromResult(Message.Error(message.MessageId, _instanceId, "the index finger and the wrist are at the same place: no direction is indicated"));
        var azimuth   = Math.Atan2(dx, dz) * 180 / Math.PI;
        var elevation = Math.Asin(Math.Clamp(-dy / length, -1, 1)) * 180 / Math.PI;

        var pov = new PointOfView
        {
            PointOfViewID   = Guid.NewGuid().ToString(),
            PointOfViewTime = SimpleTime.At(DateTimeOffset.UtcNow),
            General         = new GeneralClassification { CoordType = CoordinateType.Cartesian, ObjectType = ObjectType.Generic, MediaType = "Visual" },
            CartPosition    = new double[] { k[best * 4], k[best * 4 + 1], k[best * 4 + 2] },
            Orientation     = new double[] { azimuth, elevation, 0 },
            Aperture        = new List<ApertureEntry> { new() { Azimuth = ApertureDegrees, Elevation = ApertureDegrees } },
            DescrMetadata   = "Where the index finger of the " + (best == LeftIndex ? "left" : "right") + " hand points."
        };
        return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = message.MessageType, Ports = new Dictionary<string, string> { [_outPort] = MpaiJson.ToJson(pov) } });
    }

    private static double Distance(float[] k, int a, int b)
    {
        double dx = k[a * 4] - k[b * 4], dy = k[a * 4 + 1] - k[b * 4 + 1], dz = k[a * 4 + 2] - k[b * 4 + 2];
        return Math.Sqrt(dx * dx + dy * dy + dz * dz);
    }
}

public sealed class VdiPlugin : IAimPlugin
{
    public string AimName => "CVE-VDI-V1.0";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => new CveVdiAimProcessor(AimName, ports);
}
