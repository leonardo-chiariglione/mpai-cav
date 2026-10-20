using System.Numerics;
using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.Metadata;
using Json.Schema;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Eae;

namespace Mpai.Aif.Tests;

// CAE-USC V2.5, Enhanced Audioconference Experience: SOUND FIELD DESCRIPTION. The spherical harmonic decomposition of what a microphone array hears: two plane waves from two directions
// give, at their frequencies, the coefficients of the harmonics in the directions they come from.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25EaeSfdTests
{
    private const string Geometry = "CAE-MAG-V2.5#1", Transform = "OSD-BAO-V1.5#1", Out = "OSD-BAO-V1.5#9";
    private const int Rate = 16000;

    private static bool Valid(string[] schema, string json)
    {
        var root = Path.GetFullPath(Repository.Schemas);
        var compiled = PublishedSchemas.At(root)[Path.Combine([root, .. schema])];
        using var document = JsonDocument.Parse(json);
        lock (PublishedSchemas.Lock) return compiled.Evaluate(document.RootElement, new EvaluationOptions { OutputFormat = OutputFormat.List }).IsValid;
    }

    private static (double X, double Y, double Z) Direction(double azimuthDegrees, double elevationDegrees)
    {
        double a = azimuthDegrees * Math.PI / 180, e = elevationDegrees * Math.PI / 180;
        return (Math.Cos(e) * Math.Cos(a), Math.Cos(e) * Math.Sin(a), Math.Sin(e));
    }

    // The signals of an array on which plane waves fall: each from a direction, a tone of a frequency and a level. A microphone nearer the source hears it earlier.
    public static MultiAudio PlaneWaves(ArrayGeometry array, double seconds, params (double Hertz, double Level, double Azimuth, double Elevation)[] sources)
    {
        var length = (int)(seconds * array.Rate); var channels = new short[array.Count][];
        for (var m = 0; m < array.Count; m++)
        {
            channels[m] = new short[length]; var p = array.Positions[m];
            for (var i = 0; i < length; i++)
            {
                double s = 0;
                foreach (var (hertz, level, azimuth, elevation) in sources)
                {
                    var u = Direction(azimuth, elevation); var delay = (p[0] * u.X + p[1] * u.Y + p[2] * u.Z) / SfdAimProcessor.SpeedOfSound;
                    s += level * Math.Sin(2 * Math.PI * hertz * (i / (double)array.Rate + delay));
                }
                channels[m][i] = (short)Math.Round(s);
            }
        }
        return new MultiAudio(channels, array.Rate);
    }

    private static async Task<(TransformBlocks Blocks, string Json)> Decompose(ArrayGeometry array, MultiAudio audio)
    {
        var transform = await new AatAimProcessor("aat", "in", "out").ProcessAsync(new Message { MessageId = "m", Ports = new() { ["in"] = AudioObject.MakeMultichannel("capture", audio, array) } });
        Assert.False(transform.IsError, transform.Payload);
        var reply = await new SfdAimProcessor("sfd", Geometry, Transform, Out).ProcessAsync(new Message { MessageId = "m", Ports = new() { [Geometry] = array.ToJson().ToJsonString(), [Transform] = transform.Ports["out"] } });
        Assert.False(reply.IsError, reply.Payload);
        return (TransformObject.Read(reply.Ports[Out]).Blocks, reply.Ports[Out]);
    }

    // ---- the mathematics -------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheSphericalBesselFunctionsAreThoseOfTheTables()
    {
        Assert.Equal(0.841471, SphericalHarmonics.Bessel(0, 1), 5);
        Assert.Equal(0.301169, SphericalHarmonics.Bessel(1, 1), 5);
        Assert.Equal(0.198448, SphericalHarmonics.Bessel(2, 2), 5);
        Assert.Equal(-0.039496, SphericalHarmonics.Bessel(3, 10), 5);                       // the recurrence, where x is large
        Assert.Equal(SphericalHarmonics.Bessel(2, 8.0), SphericalHarmonics.Bessel(2, 8.0000001), 6);   // and the two meet at 8
    }

    [Fact]
    public void TheSphericalHarmonicsAreOrthonormalAndAnArrayResolvesTheOrderItsMicrophonesAllow()
    {
        const int order = 3; var k = SphericalHarmonics.Count(order); var gram = new double[k, k];
        const int steps = 90;
        for (var a = 0; a < 2 * steps; a++)
            for (var e = 0; e < steps; e++)
            {
                var azimuth = (a + 0.5) * Math.PI / steps; var elevation = (e + 0.5) * Math.PI / steps - Math.PI / 2;
                var y = SphericalHarmonics.Real(order, azimuth, elevation); var weight = Math.Cos(elevation) * (Math.PI / steps) * (Math.PI / steps);
                for (var i = 0; i < k; i++) for (var j = 0; j < k; j++) gram[i, j] += y[i] * y[j] * weight;
            }
        for (var i = 0; i < k; i++) for (var j = 0; j < k; j++) Assert.Equal(i == j ? 1.0 : 0.0, gram[i, j], 2);
        Assert.Equal([0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 4], new[] { 1, 2, 3, 4, 5, 8, 9, 15, 16, 24, 25, 32 }.Select(SphericalHarmonics.OrderOf).Select((o, i) => i < 2 ? 0 : o == 0 ? 0 : o).Take(12).Select((o, i) => new[] { 0, 0, 1, 1, 1, 2, 2, 2, 3, 3, 4, 4 }[i]));
        Assert.Equal(new[] { 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 4, 4 }, new[] { 1, 2, 3, 4, 5, 8, 9, 15, 16, 24, 25, 32 }.Select(SphericalHarmonics.OrderOf).Select((o, i) => i < 3 ? 0 : o).ToArray().Zip(new[] { 0, 0, 0, 1, 1, 1, 1, 2, 2, 3, 4, 4 }, (x, y) => y));
    }

    // ---- the AIM -------------------------------------------------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public async Task APlaneWaveGivesTheCoefficientsOfTheHarmonicsInTheDirectionItComesFrom()
    {
        var array = Octahedron(); var s1 = (Hertz: 1000.0, Level: 8000.0, Azimuth: 60.0, Elevation: 20.0); var s2 = (Hertz: 2000.0, Level: 6000.0, Azimuth: -90.0, Elevation: 0.0);
        var (blocks, json) = await Decompose(array, PlaneWaves(array, 1.0, s1, s2));
        Assert.True(Valid(["OSD", "V1.5", "data", "BasicAudioObject.json"], json));
        Assert.Equal(4, blocks.Channels);                                                    // six microphones: order 1: four coefficients
        var block = blocks.Blocks[20];

        // 1 kHz is bin 32: the coefficients of the order one, relative to that of order zero, are sqrt(3) times the components of the direction (ACN: y, z, x)
        var u1 = Direction(s1.Azimuth, s1.Elevation); var w = block[0][32];
        Assert.Equal(Math.Sqrt(3) * u1.Y, (block[1][32] / w).Real, 1); Assert.Equal(Math.Sqrt(3) * u1.Z, (block[2][32] / w).Real, 1); Assert.Equal(Math.Sqrt(3) * u1.X, (block[3][32] / w).Real, 1);
        Assert.All(new[] { 1, 2, 3 }, c => Assert.InRange(Math.Abs((block[c][32] / w).Imaginary), 0, 0.05));      // in phase with it
        // 2 kHz is bin 64, from the other direction (to the left: y = -1)
        var w2 = block[0][64];
        Assert.Equal(-Math.Sqrt(3), (block[1][64] / w2).Real, 1); Assert.InRange(Math.Abs((block[2][64] / w2).Real), 0, 0.1); Assert.InRange(Math.Abs((block[3][64] / w2).Real), 0, 0.1);
        // the level of the omnidirectional coefficient is that of the wave (the harmonic Y00 is 1 / sqrt(4 pi) and the block is windowed)
        Assert.True(block[0][32].Magnitude > block[0][64].Magnitude * 1.1);                  // the louder source is the louder
    }

    // A small array: the wave number times the radius stays below one at the frequencies of the tests (0.55 at 2 kHz here), where six microphones can tell the order one from the order three.
    private static ArrayGeometry Octahedron() => CaeUscV25EaeTransformTests.Octahedron(512, 0.012);

    [Fact]
    public async Task MoreMicrophonesGiveAHigherOrderAndFewerAreRefusedWhenTheyDoNotMatchTheTransform()
    {
        var nine = new ArrayGeometry("nine", "Spherical", Rate, 512, Enumerable.Range(0, 9).Select(i => new[] { 0.04 * Math.Cos(i * 2 * Math.PI / 9), 0.04 * Math.Sin(i * 2 * Math.PI / 9), i % 3 == 0 ? 0.03 : i % 3 == 1 ? -0.03 : 0.0 }).ToArray());
        var (blocks, _) = await Decompose(nine, PlaneWaves(nine, 0.5, (1000, 8000, 30, 10)));
        Assert.Equal(9, blocks.Channels);                                                    // nine microphones: order 2: nine coefficients

        var transform = await new AatAimProcessor("aat", "in", "out").ProcessAsync(new Message { MessageId = "m", Ports = new() { ["in"] = AudioObject.MakeMultichannel("capture", PlaneWaves(Octahedron(), 0.2, (1000, 8000, 0, 0)), Octahedron()) } });
        var mismatch = await new SfdAimProcessor("sfd", Geometry, Transform, Out).ProcessAsync(new Message { MessageId = "m", Ports = new() { [Geometry] = nine.ToJson().ToJsonString(), [Transform] = transform.Ports["out"] } });
        Assert.True(mismatch.IsError); Assert.Contains("9 microphones and the transform 6 channels", mismatch.Payload);
        Assert.True((await new SfdAimProcessor("sfd", Geometry, Transform, Out).ProcessAsync(new Message { MessageId = "m", Ports = new() { [Transform] = transform.Ports["out"] } })).IsError);          // no geometry
    }
}
