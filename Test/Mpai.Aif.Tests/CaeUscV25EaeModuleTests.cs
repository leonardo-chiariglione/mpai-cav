using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cae.Usc;
using Mpai.Cae.Usc.Eae;

namespace Mpai.Aif.Tests;

// ENHANCED AUDIOCONFERENCE EXPERIENCE AS A MODULE under a Controller, the two apart (the Controller and the Module are always separate): its six SubAIMs execute in a process of their own
// (AIF.AimHost), wired as the L3 says. One exchange: the capture of a six-microphone array in which two sources speak, partly together, in a room with noise; the Audio Scene Descriptors
// come back with the two sources, where each is and the audio of each.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class CaeUscV25EaeModuleTests : IDisposable
{
    private const string Ees = "1CAE-EAE-V2.5-I01";
    private static readonly string[] Aims = ["1CAE-AAT-V2.5-I01", "1CAE-SFD-V2.5-I01", "1CAE-SDS-V2.5-I01", "1CAE-NCM-V2.5-I01", "1CAE-AST-V2.5-I01", "1CAE-AMX-V2.5-I01"];
    private readonly string work = Path.Combine(Path.GetTempPath(), "cae-eae-" + Guid.NewGuid().ToString("N"));
    private readonly HostProcess process;
    private readonly ControllerApi api;

    public CaeUscV25EaeModuleTests()
    {
        Directory.CreateDirectory(work);
        var amds = Path.Combine(work, "module-amds"); Directory.CreateDirectory(amds);
        foreach (var file in Directory.EnumerateFiles(Repository.Amds, "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));
        var l3 = File.ReadAllText(Path.Combine(amds, Ees + ".json"));
        foreach (var aim in Aims)
            l3 = System.Text.RegularExpressions.Regex.Replace(l3, "(\"AIMName\"\\s*:\\s*\"" + aim + "\"\\s*,\\s*\"Relation\"\\s*:\\s*)\"Internal\"", "$1\"External\"");
        File.WriteAllText(Path.Combine(amds, Ees + ".json"), l3);
        var settings = Path.Combine(work, "aim-settings.json"); File.WriteAllText(settings, "{}");
        process = new HostProcess(amds: amds, provider: Path.Combine(AppContext.BaseDirectory, "Mpai.Cae.Usc.dll") + ":Mpai.Cae.Usc.UscProvider",
                                  extra: ["--settings", settings], workingDirectory: Repository.Root);
        api = new ControllerApi(amds, settings, _ => new CompositeProvider());
        api.Controller.AimHostKey = HostProcess.Key;
        foreach (var aim in Aims) api.Controller.AimHosts[aim] = process.Address;
        Assert.Equal(AifError.OK, api.StartFlow(Ees));
    }

    public void Dispose() { api.Dispose(); process.Dispose(); try { Directory.Delete(work, true); } catch { } }

    [Fact]
    public void TwoSourcesInANoisyRoomComeBackAsAnAudioSceneWithTheAudioOfEach()
    {
        var array = CaeUscV25EaeTransformTests.Octahedron(512, 0.012);
        var s1 = CaeUscV25EaeSfdTests.PlaneWaves(array, 1.5, (1000, 8000, 60, 20)); var s2 = CaeUscV25EaeSfdTests.PlaneWaves(array, 1.5, (2000, 6000, -90, 0));
        var state = 7u;
        var capture = new MultiAudio(s1.Channels.Select((c, m) => c.Select((v, i) =>
        {
            state = state * 1664525u + 1013904223u;
            return (short)((i < 0.8 * 16000 ? v : 0) + (i >= 0.5 * 16000 ? s2.Channels[m][i] : 0) + ((int)(state >> 8) % 401 - 200));      // the first until 0.8 s, the second from 0.5 s
        }).ToArray()).ToArray(), 16000);

        Assert.Equal(AifError.OK, api.InputWrite(Ees, "CAE-MAG-V2.5", 1, array.ToJson().ToJsonString(), 10_000));
        Assert.Equal(AifError.OK, api.InputWrite(Ees, "CAE-ASR-V1.0", 1, new JsonObject { ["Header"] = "CAE-ASR-V1.0", ["PolynomialVariable"] = "AngleSine", ["Polynomial"] = new JsonArray(new JsonObject { ["Coefficient"] = 1.0, ["Exponent"] = 0 }) }.ToJsonString(), 10_000));
        Assert.Equal(AifError.OK, api.InputWrite(Ees, "OSD-BAO-V1.5", 1, AudioObject.MakeMultichannel("capture", capture, array), 10_000));

        var read = api.OutputRead(Ees, "OSD-BAS-V1.5", 1, 60_000);
        Assert.True(read.Error == AifError.OK, $"nothing at the Audio Scene Descriptors: {read.Error}");
        var root = JsonNode.Parse(read.Json!)!.AsObject();
        Assert.Equal("OSD-BAS-V1.5", root["Header"]!.GetValue<string>()); Assert.Equal(2, root["AudioObjectCount"]!.GetValue<int>());
        var sources = SceneDescriptors.Read(read.Json!);
        Assert.Contains(sources, s => Angle(s, 60, 20) < 8); Assert.Contains(sources, s => Angle(s, -90, 0) < 8);
        foreach (var m in root["BasicAudioSceneDescriptorsData"]!.AsArray())
        {
            var item = AudioObject.Read(m!["AObjectIDOrAObject"]![0]!.ToJsonString()).Single();
            Assert.Equal(16000, item.Audio.Rate); Assert.Equal(24000, item.Audio.Samples.Length);                    // 1.5 s
            Assert.Contains(item.Audio.Samples, v => Math.Abs((int)v) > 1000);                                       // and there is a sound in it
        }
    }

    private static double Angle(SceneSource s, double az, double el)
    {
        double[] U(double a, double e) => [Math.Cos(e * Math.PI / 180) * Math.Cos(a * Math.PI / 180), Math.Cos(e * Math.PI / 180) * Math.Sin(a * Math.PI / 180), Math.Sin(e * Math.PI / 180)];
        var u = U(s.Azimuth, s.Elevation); var v = U(az, el);
        return Math.Acos(Math.Clamp(u[0] * v[0] + u[1] * v[1] + u[2] * v[2], -1, 1)) * 180 / Math.PI;
    }
}