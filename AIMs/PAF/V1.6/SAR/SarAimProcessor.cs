using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Core;
using Mpai.Core.OSD;

namespace Mpai.Paf.Sar;

// PAF-SAR-V1.6 - Scene and Avatar Rendering, as an AIF IAimProcessor. It places the
// Speaking Avatar (XRV-SAV), made by Speaking Avatar Synthesis, in a Scene seen and
// heard from a Point of View, and produces the Scene as data (the author, 2026/10/03:
// RSR itself never draws). Of its two alternatives this Implementation produces the
// 3D one:
//   - 3D Model Scene (OSD-B3S): the input 3D Model Scene with the Avatar placed - a
//     member naming the Avatar's 3D Model, at the Avatar's Space-Time - for a 3D
//     Model Object Delivery (the User Agent's renderer) to draw;
//   - Multimodal Scene (OSD-BMS): the Basic Scenes heard and seen - the input Audio
//     Scene, the Speech Scene (the Avatar's speech at its mouth) and the 3D Model
//     Scene - in one frame.
// The 2D alternative (Basic Audio Scene + Basic Visual Scene of Visual Objects) is
// not implemented yet.
//
// WHERE THE AVATAR IS. Its own Space-Time, when the Avatar has one; else where the
// input 3D Model Scene already has a member naming it; else 1.5 m in front of the
// Point of View, turned to face it; else at the origin. Positions are in metres -
// X ahead, Y left, Z up - and Orientation is (roll, pitch, yaw) in degrees. A 3D
// Model faces -X at yaw 0, so an Avatar given the Point of View's yaw faces the
// viewer.
//
// THE SCENE IS KEPT. Audio, 3D Model and Point of View are given when they change -
// the room once, the viewpoint when the user moves - and used for every utterance
// until replaced, as Speaking Avatar Synthesis keeps the Avatar. Only the Speaking
// Avatar is required. With no scene given at all there is nothing to place the
// Avatar in, and nothing is produced.
public sealed class SarAimProcessor : IAimProcessor
{
    public const double Ahead = 1.5;      // metres between the Point of View and the Avatar
    public const double MouthHeight = 1.6;

    private readonly string _avatarPort;  // XRV-SAV
    private readonly string _audioPort;   // OSD-BAO / OSD-BAS
    private readonly string _modelPort;   // OSD-B3O / OSD-B3S
    private readonly string _povPort;     // OSD-OPV
    private readonly string _b3sPort;     // OSD-B3S out
    private readonly string _bmsPort;     // OSD-BMS out

    private BasicAudioSceneDescriptors? _audio;
    private Basic3DModelSceneDescriptors? _model;
    private PointOfView? _pov;

    public string InstanceId { get; }

    public SarAimProcessor(string instanceId, AimPortReader ports)
    {
        InstanceId  = instanceId;
        _avatarPort = ports.Input("XRV-SAV-V1.0");
        _audioPort  = ports.Input("OSD-BAS-V1.5");
        _modelPort  = ports.Input("OSD-B3S-V1.5");
        _povPort    = ports.Input("OSD-OPV-V1.5");
        _b3sPort    = ports.Output("OSD-B3S-V1.5");
        _bmsPort    = ports.Output("OSD-BMS-V1.5");
    }

    public Task<Message> ProcessAsync(Message message)
    {
        string? Json(string port) =>
            port.Length > 0 && message.Ports.TryGetValue(port, out var json) && !string.IsNullOrWhiteSpace(json) ? json : null;

        if (Json(_povPort) is { } pov) _pov = MpaiJson.FromJson<PointOfView>(pov);
        if (Json(_audioPort) is { } audio)
        {
            if (Header(audio) == "OSD-BAO-V1.5")
            {
                var bao = MpaiJson.FromJson<BasicAudioObject>(audio);
                _audio = new BasicAudioSceneDescriptors
                {
                    BasicAudioSceneDescriptorsID = Guid.NewGuid().ToString(), AudioObjectCount = 1,
                    BasicAudioSceneDescriptorsEntries = [new BasicAudioSceneEntry { AudioObjectSpaceTime = bao.BasicAudioObjectTime, AudioObjectIDOrAudioObject = bao }]
                };
            }
            else if (Header(audio) == "OSD-BAS-V1.5") _audio = MpaiJson.FromJson<BasicAudioSceneDescriptors>(audio);
            else message.Context.Report($"Audio {Header(audio)} is not rendered: this Implementation takes Basic Audio (OSD-BAO, OSD-BAS).");
        }
        if (Json(_modelPort) is { } model)
        {
            if (Header(model) == "OSD-B3S-V1.5") _model = MpaiJson.FromJson<Basic3DModelSceneDescriptors>(model);
            else if (Header(model) == "OSD-B3O-V1.5")
            {
                var b3o = JsonNode.Parse(model)!.AsObject();
                _model = new Basic3DModelSceneDescriptors
                {
                    Basic3DModelSceneDescriptorsID = Guid.NewGuid().ToString(), ModelObjectCount = 1,
                    Basic3DModelSceneItems = [new Basic3DModelSceneItem
                    {
                        ModelObjectSpaceTime = MpaiJson.FromJson<Basic3DModelObject>(model).Basic3DModelObjectSpaceTime,
                        ObjectIDOrObject = [b3o]
                    }]
                };
            }
            else message.Context.Report($"3D Model {Header(model)} is not rendered: this Implementation takes Basic 3D Models (OSD-B3O, OSD-B3S).");
        }

        var speaking = Json(_avatarPort) is { } sav ? MpaiJson.FromJson<SpeakingAvatar>(sav) : null;
        if (speaking is null)                                   // scene alone: kept for the next utterance
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "SceneKept", Ports = new() });
        if (_audio is null && _model is null && _pov is null)   // no scene: nothing to place the Avatar in
            return Task.FromResult(new Message { MessageId = message.MessageId, MessageType = "NoScene", Ports = new() });

        var (b3s, bms) = Render(speaking, _audio, _model, _pov);
        var ports = new Dictionary<string, string>();
        if (_b3sPort.Length > 0) ports[_b3sPort] = MpaiJson.ToJson(b3s);
        if (_bmsPort.Length > 0) ports[_bmsPort] = MpaiJson.ToJson(bms);
        return Task.FromResult(new Message
        {
            MessageId = message.MessageId, MessageType = "Scene",
            DataType = "OSD-B3S-V1.5", Payload = ports.GetValueOrDefault(_b3sPort, ""), Ports = ports
        });
    }

    private static string? Header(string json) => (string?)JsonNode.Parse(json)?["Header"];

    // The Avatar placed in the Scene: the 3D Model Scene with it, and the Multimodal
    // Scene of what is heard and seen.
    public static (Basic3DModelSceneDescriptors Model, BasicAudioVisualSceneDescriptors Multimodal) Render(
        SpeakingAvatar speaking, BasicAudioSceneDescriptors? audio, Basic3DModelSceneDescriptors? model, PointOfView? pov)
    {
        var avatar = speaking.Avatar();
        var name = avatar?.ModelId() ?? avatar?.AvatarID ?? speaking.SpeakingAvatarID;
        var mInstance = speaking.MInstanceID ?? model?.MInstanceID ?? "";
        var viewer = pov ?? model?.UserPoV ?? new PointOfView { PointOfViewID = "origin" };
        var placed = avatar?.AvatarSpaceTime
                     ?? model?.Basic3DModelSceneItems.FirstOrDefault(i => i.Id == name)?.ModelObjectSpaceTime
                     ?? (pov is null ? At([0, 0, 0], 0) : InFrontOf(pov));
        var frame = model?.Basic3DModelSceneDescriptorsSpaceTime ?? At([0, 0, 0], 0);

        var items = (model?.Basic3DModelSceneItems ?? []).Where(i => i.Id != name).ToList();
        items.Add(new Basic3DModelSceneItem { ModelObjectSpaceTime = placed, ObjectIDOrObject = [JsonValue.Create(name)] });
        var b3s = new Basic3DModelSceneDescriptors
        {
            MInstanceID = mInstance, UEnvironmentID = model?.UEnvironmentID,
            Basic3DModelSceneDescriptorsID = Guid.NewGuid().ToString(),
            UserPoV = viewer, GravityValue = model?.GravityValue,
            Basic3DModelSceneDescriptorsSpaceTime = frame,
            ModelObjectCount = items.Count, Basic3DModelSceneItems = items
        };

        var entries = new List<BasicAVSceneEntry>();
        if (audio is not null) entries.Add(new BasicAVSceneEntry { BXSSpaceTime = audio.BASSpaceTime ?? frame, BXSOrBXSID = audio });
        if (speaking.Speech() is { } speech)
        {
            var mouth = Raised(placed, MouthHeight);
            entries.Add(new BasicAVSceneEntry
            {
                BXSSpaceTime = frame,
                BXSOrBXSID = new BasicSpeechSceneDescriptors
                {
                    MInstanceID = mInstance, BasicSpeechSceneDescriptorsID = Guid.NewGuid().ToString(),
                    BasicSpeechSceneDescriptorsSpaceTime = frame, UserPoV = viewer, ObjectCount = 1,
                    BasicSpeechSceneItems = [new BasicSpeechSceneItem { ObjectSpaceTime = mouth, SpeechObject = speech }]
                }
            });
        }
        entries.Add(new BasicAVSceneEntry { BXSSpaceTime = frame, BXSOrBXSID = b3s });
        var bms = new BasicAudioVisualSceneDescriptors
        {
            MInstanceID = mInstance, BasicAVSceneDescriptorsID = Guid.NewGuid().ToString(),
            BAVSDescriptorsSpaceTime = frame, UserPoV = viewer,
            AVObjectCount = entries.Count, BasicAVSceneDescriptorsData = entries
        };
        return (b3s, bms);
    }

    // 1.5 m along the Point of View's heading, on the ground, facing back at it.
    public static SpaceTime InFrontOf(PointOfView pov)
    {
        var yaw = pov.Orientation.Length > 2 ? pov.Orientation[2] : 0;
        var rad = yaw * Math.PI / 180;
        var p = pov.CartPosition;
        return At([p[0] + Ahead * Math.Cos(rad), p[1] + Ahead * Math.Sin(rad), 0], yaw);
    }

    public static SpaceTime At(double[] position, double yaw) => new()
    {
        SpaceTimeID = Guid.NewGuid().ToString(),
        SpatialAttitude1 = new SpatialAttitude
        {
            ObjectSpatialAttitudeID = Guid.NewGuid().ToString(),
            Position = new Position { PositionID = Guid.NewGuid().ToString(), CartPosition = position },
            Orientation = new Orientation { OrientationID = Guid.NewGuid().ToString(), EulerAngles = [0, 0, yaw] }
        }
    };

    private static SpaceTime Raised(SpaceTime at, double height)
    {
        var a = at.SpatialAttitude1;
        var p = a?.Position.CartPosition ?? [0, 0, 0];
        return At([p[0], p[1], p[2] + height], a?.Orientation.EulerAngles is { Length: > 2 } e ? e[2] : 0);
    }
}
