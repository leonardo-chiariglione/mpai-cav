using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cav.Recordings;
using Mpai.Cav.Vrv;

namespace Mpai.Aif.Tests;

// THE SIMULATED VEHICLE ON A PLANE (IVehiclePlane): the vehicle of the recordings, put on a surface that has no end, commanded through its devices as it is on a road.
internal sealed class VehiclePlaneRig : IVehiclePlane
{
    private Vehicle vehicle = new(0, 0, 0, 0, 1);

    public void Reset(double speed, double friction) => vehicle = new Vehicle(0, 0, 0, speed, 1) { Friction = friction };
    public void Command(string dataType, JsonNode command) => vehicle.Command(dataType, command);
    public void Step(double seconds) => vehicle.Step(seconds);
    public double Speed => vehicle.Speed;
    public double Acceleration => vehicle.Acceleration;
    public double Heading => vehicle.Heading;
    public bool Slipping => vehicle.AbsActive || vehicle.WheelLocked || vehicle.LossOfTraction;
    public bool Sliding => vehicle.Skidding;
}

// THE VEHICLE RESPONSE VOCABULARY (CAV-VRV): what the probe measures on the plane, against what the vehicle is built to do, and against its schema.
public sealed class VehicleResponseVocabularyTests
{
    private static JsonObject Level(JsonObject surface, double speed, string device, double command)
    {
        var atSpeed = surface["Speed"]!.AsArray().Single(s => Math.Abs((double)s!["Velocity"]! - speed) < 1e-9)!;
        return atSpeed[device]!["Level"]!.AsArray().Single(l => Math.Abs((double)l!["Command"]! - command) < 1e-9)!.AsObject();
    }

    private static JsonObject Surface(JsonObject vocabulary, string type) => vocabulary["Surface"]!.AsArray().Single(s => (string?)s!["SurfaceType"] == type)!.AsObject();

    [Fact]
    public void TheProbeMeasuresTheVehicleOnAPlane()
    {
        var vocabulary = new ResponseProbe(new VehiclePlaneRig()).Measure("CAV-1", 0);

        // It is the Data Type: valid against its schema.
        var schema = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "CAV2", "V2.0", "data", "VehicleResponseVocabulary.json"))];
        using (var doc = JsonDocument.Parse(vocabulary.ToJsonString()))
        {
            bool valid; lock (AIF.Metadata.PublishedSchemas.Lock) valid = schema.Evaluate(doc.RootElement).IsValid;
            Assert.True(valid, "the vocabulary does not validate against VehicleResponseVocabulary.json");
        }
        File.WriteAllText(Path.Combine(Repository.Root, "Test", "Reports", "vehicle-response-vocabulary.json"), vocabulary.ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + Environment.NewLine);

        // On dry asphalt, at 20 m/s, a brake command of 3 m/s2 gives about 3 m/s2, after the lag of the brakes and not before.
        var dry = Surface(vocabulary, "dry asphalt");
        var brake = Level(dry, 20, "Brake", 3);
        Assert.InRange((double)brake["Settled"]!, 2.7, 3.4);
        Assert.InRange((double)brake["DeadTime"]!, 0, 0.2);
        Assert.InRange((double)brake["RiseTime"]!, 0.05, 0.6);
        Assert.False((bool)brake["Slipping"]!);

        // On ice the tyres cannot give 8 m/s2: they slip, and the vehicle stops no harder than the tyres allow (0.15 g, ABS at 0.95 of it) with the resistance of the road and the air added.
        var ice = Surface(vocabulary, "ice");
        var hard = Level(ice, 20, "Brake", 8);
        Assert.True((bool)hard["Slipping"]!);
        Assert.InRange((double)hard["Settled"]!, 0.5, 0.15 * 9.81 * 0.95 + 0.3);          // the tyres' 0.95 of the grip, and the drag and the rolling resistance on top

        // The motor answers as the motor does: in about its lag, and a command beyond the power it has is not given at speed.
        Assert.InRange((double)Level(dry, 10, "Motor", 2)["Settled"]!, 1.8, 2.2);
        Assert.True((double)Level(dry, 30, "Motor", 3)["Settled"]! < 3);

        // Steering: the turn the wheels ask for on dry asphalt is the turn the vehicle makes, and on ice it is not.
        var turn = Level(dry, 10, "Steering", 4);
        Assert.True((bool)turn["Following"]!);
        Assert.InRange((double)turn["LateralAcceleration"]!, 2.2, 2.8);          // 10 m/s, 4 degrees: v2 tan(angle) / wheelbase = 2.5 m/s2
        Assert.False((bool)Level(ice, 20, "Steering", 25)["Following"]!);

        // Coasting slows it down.
        Assert.True((double)dry["Speed"]!.AsArray().Single(s => (double)s!["Velocity"]! == 20)!["Motor"]!["Coasting"]! < 0);
    }
}
