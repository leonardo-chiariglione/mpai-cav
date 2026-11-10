using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Store;
using Mpai.Aif.PortData;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Cve.Vdi;
using Mpai.Osd.Voe;
using Mpai.Paf.Ebd;

namespace Mpai.Aif.Tests;

// CVE-VDI-V1.0 (Visual Direction Identification) and OSD-VOE-V1.5 (Visual Object Extraction), on synthetic data: a body of BlazePose keypoints and a Visual Scene
// Geometry made by hand. They need no model. What they assume (the frame of the keypoints, the Euler angles, the Aperture as the cone) is written in the AIMs.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CveVdiVoeTests
{
    private const string VdiName = "1CVE-VDI-V1.0-I01", VoeName = "1OSD-VOE-V1.5-I01";
    private const int LeftShoulder = 11, RightShoulder = 12, LeftWrist = 15, RightWrist = 16, LeftIndex = 19, RightIndex = 20;

    private static AimPortReader Ports(string aim) { var store = new AmdStore(Repository.Amds); store.Scan(); return AimPortReader.Load(store, aim); }

    private static void Joint(float[] k, int i, double x, double y, double z, float visibility = 1f)
    { k[i * 4] = (float)x; k[i * 4 + 1] = (float)y; k[i * 4 + 2] = (float)z; k[i * 4 + 3] = visibility; }

    private static string Body(Action<float[]> arrange)
    {
        var k = new float[33 * 4];
        arrange(k);
        return MpaiJson.ToJson(BodyDescriptorsObject.FromContent(EmbeddingCodec.ToBase64(k), EbdAimProcessor.DescriptorFormat));
    }

    // A right arm that points along +x: shoulder at the origin, wrist at 0.3, the tip of the index finger at 0.4.
    private static string RightArmAlongX() => Body(k => { Joint(k, RightShoulder, 0, 0, 0); Joint(k, RightWrist, 0.3, 0, 0); Joint(k, RightIndex, 0.4, 0, 0); });

    private static string Geometry(params (string Id, double X, double Y, double Z)[] objects)
    {
        var items = new JsonArray();
        foreach (var o in objects)
            items.Add(new JsonObject
            {
                ["ObjectSpaceTime"] = new JsonObject { ["SpatialAttitude1"] = new JsonObject { ["Position"] = new JsonObject { ["CartPosition"] = new JsonArray(o.X, o.Y, o.Z) } } },
                ["ObjectIDOrGeometry"] = new JsonArray(o.Id)
            });
        return new JsonObject { ["Header"] = "OSD-VSG-V1.5", ["VisualSceneGeometryID"] = "scene", ["ObjectCount"] = objects.Length, ["Objects"] = items }.ToJsonString();
    }

    private static string VisualObject(string id) => PortObjects.WriteVisual(new BasicVisualObject { BasicVisualObjectID = id, Data = [1, 2, 3] });

    private static async Task<Message> Direction(string bdo, string geometry)
    {
        var ports = Ports(VdiName);
        var vdi = new CveVdiAimProcessor(VdiName, ports);
        return await vdi.ProcessAsync(new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("PAF-BDO-V1.6")] = bdo, [ports.Input("OSD-VSG-V1.5")] = geometry } });
    }

    [Fact]
    public async Task TheIndexFingerOfTheRightArmGivesAPointOfViewAlongThatArm()
    {
        var reply = await Direction(RightArmAlongX(), Geometry());
        Assert.False(reply.IsError, reply.Payload);
        var pov = MpaiJson.FromJson<PointOfView>(reply.Ports[Ports(VdiName).Output("OSD-OPV-V1.5")])!;
        Assert.Equal([0.4, 0, 0], pov.CartPosition.Select(v => Math.Round(v, 4)).ToArray());
        Assert.Equal(90, pov.Orientation[0], 3);       // azimuth: +x
        Assert.Equal(0, pov.Orientation[1], 3);        // elevation
        Assert.Equal(CveVdiAimProcessor.ApertureDegrees, pov.Aperture![0].Azimuth);
    }

    [Fact]
    public async Task TheHandThatReachesFartherFromItsShoulderIsTheOneThatPoints()
    {
        var bdo = Body(k =>
        {
            Joint(k, LeftShoulder, 0, 0, 0); Joint(k, LeftWrist, 0, 0.1, 0.05); Joint(k, LeftIndex, 0, 0.15, 0.05);        // at the side
            Joint(k, RightShoulder, 0.4, 0, 0); Joint(k, RightWrist, 0.7, -0.1, 0); Joint(k, RightIndex, 0.8, -0.1, 0);     // out
        });
        var reply = await Direction(bdo, Geometry());
        var pov = MpaiJson.FromJson<PointOfView>(reply.Ports[Ports(VdiName).Output("OSD-OPV-V1.5")])!;
        Assert.Equal(0.8, pov.CartPosition[0], 4);
    }

    [Fact]
    public async Task AnArmThatPointsUpGivesAnElevationAbove0()
    {
        var bdo = Body(k => { Joint(k, RightShoulder, 0, 0, 0); Joint(k, RightWrist, 0, -0.3, 0.3); Joint(k, RightIndex, 0, -0.4, 0.4); });   // y is down: up is negative
        var reply = await Direction(bdo, Geometry());
        var pov = MpaiJson.FromJson<PointOfView>(reply.Ports[Ports(VdiName).Output("OSD-OPV-V1.5")])!;
        Assert.Equal(45, pov.Orientation[1], 3);
        Assert.Equal(0, pov.Orientation[0], 3);
    }

    [Fact]
    public async Task NoIndexFingerSeenIsSaidAndNoDirectionIsGuessed()
    {
        var reply = await Direction(Body(k => { Joint(k, RightShoulder, 0, 0, 0); Joint(k, RightWrist, 0.3, 0, 0); Joint(k, RightIndex, 0.4, 0, 0, visibility: 0.1f); }), Geometry());
        Assert.True(reply.IsError);
        Assert.Contains("no direction", reply.Payload);
    }

    [Fact]
    public async Task ABodyOfAnotherFormatIsRefused()
    {
        var reply = await Direction(MpaiJson.ToJson(BodyDescriptorsObject.FromContent("AAAA", "Some other format")), Geometry());
        Assert.True(reply.IsError);
    }

    private static async Task<Message> Extract(OsdVoeAimProcessor voe, AimPortReader ports, string? pov, string geometry, string? visualObject)
    {
        var message = new Message { MessageId = "m", Ports = new Dictionary<string, string> { [ports.Input("OSD-VSG-V1.5")] = geometry } };
        if (pov is not null) message.Ports[ports.Input("OSD-OPV-V1.5")] = pov;
        if (visualObject is not null) message.Ports[ports.Input("OSD-BVO-V1.5")] = visualObject;
        return await voe.ProcessAsync(message);
    }

    private static string PointOfViewAlongZ(double[]? from = null, double azimuth = 0, double elevation = 0, double? aperture = null) => MpaiJson.ToJson(new PointOfView
    {
        PointOfViewID = "p", CartPosition = from ?? [0, 0, 0], Orientation = [azimuth, elevation, 0],
        Aperture = aperture is null ? null : [new ApertureEntry { Azimuth = aperture.Value, Elevation = aperture.Value }]
    });

    [Fact]
    public async Task TheObjectInTheConeAndClosestToTheAxisIsExtracted()
    {
        var ports = Ports(VoeName); var voe = new OsdVoeAimProcessor(VoeName, ports);
        var geometry = Geometry(("near-axis", 0.05, 0, 2), ("off-axis", 1, 0, 2), ("behind", 0, 0, -2));
        await Extract(voe, ports, null, geometry, VisualObject("off-axis"));          // the objects arrive one at a time
        await Extract(voe, ports, null, geometry, VisualObject("behind"));
        var reply = await Extract(voe, ports, PointOfViewAlongZ(), geometry, VisualObject("near-axis"));
        Assert.False(reply.IsError, reply.Payload);
        Assert.Equal("near-axis", PortObjects.ReadVisual(reply.Ports[ports.Output("OSD-BVO-V1.5")]).BasicVisualObjectID);
    }

    [Fact]
    public async Task AWiderApertureReachesWhatANarrowOneMisses()
    {
        var ports = Ports(VoeName); var voe = new OsdVoeAimProcessor(VoeName, ports);
        var geometry = Geometry(("side", 1, 0, 2));        // 26.6 degrees off the axis
        await Extract(voe, ports, null, geometry, VisualObject("side"));
        Assert.True((await Extract(voe, ports, PointOfViewAlongZ(aperture: 10), geometry, null)).IsError);
        Assert.False((await Extract(voe, ports, PointOfViewAlongZ(aperture: 60), geometry, null)).IsError);
    }

    [Fact]
    public async Task NothingCrossedAndAnObjectNotYetReceivedAreSaidDifferently()
    {
        var ports = Ports(VoeName); var voe = new OsdVoeAimProcessor(VoeName, ports);
        var nothing = await Extract(voe, ports, PointOfViewAlongZ(), Geometry(("far", 5, 0, 1)), null);
        Assert.Contains("crosses no Visual Object", nothing.Payload);
        var late = await Extract(voe, ports, PointOfViewAlongZ(), Geometry(("here", 0, 0, 2)), null);
        Assert.Contains("has not been received", late.Payload);
    }

    [Fact]
    public async Task TheDirectionOfTheFingerFindsTheObjectItPointsAt()
    {
        var vdiPorts = Ports(VdiName); var voePorts = Ports(VoeName);
        var geometry = Geometry(("door", 2.4, 0, 0), ("window", 0.4, 0, 2), ("lamp", 0.4, -2, 0));        // the finger is at 0.4 and points along +x
        var pov = (await Direction(RightArmAlongX(), geometry)).Ports[vdiPorts.Output("OSD-OPV-V1.5")];
        var voe = new OsdVoeAimProcessor(VoeName, voePorts);
        foreach (var id in new[] { "window", "lamp", "door" }) await Extract(voe, voePorts, null, geometry, VisualObject(id));
        var reply = await Extract(voe, voePorts, pov, geometry, null);
        Assert.False(reply.IsError, reply.Payload);
        Assert.Equal("door", PortObjects.ReadVisual(reply.Ports[voePorts.Output("OSD-BVO-V1.5")]).BasicVisualObjectID);
    }
}
