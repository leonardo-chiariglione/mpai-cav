using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Xrv;
using Mpai.Xrv.End;
using Mpai.Xrv.Reg;
using Mpai.Xrv.Veg;

namespace Mpai.Aif.Tests;

// N-17 OF THE NIGHT LIST: THE LIVE THEATRICAL PERFORMANCE (XRV-LTP V1.0), the Composite AIM of its seven AIMs (1XRV-LTP-V1.0-I01, L3 made by AIMs\XRV1\V1.0\make_ltp_l3.py from the L2),
// run continuously under the Controller. The mini show (Test\Data\XRV\MiniShow) is given on the boundary Ports, then a recording of a performance of it - the RE and VE Data In
// the venue would send - and what comes out at RE Data Out and VE Data Out is judged: the Actions of the three Cues, in the order of the Script, as the Venues can take them.
// Also the three new AIMs alone: Environment Description, RE and VE Experience Generation.
[Trait("Group", "Fast")]
public class XrvLtpTests
{
    private const string Ltp = XrvProvider.Ltp, Env = XrvShow.Environment;
    private static JsonObject Time(string id) => XrvTime.Simple(id, 0);

    // ---- the recording: Data In as the venue sends it ---------------------------------------------------------------------------------------------

    // A 3D Model Object whose ID is the venue element it comes from (a performer tracked on the real stage).
    private static JsonObject Model3D(string id) => new() { ["3DModelObjectTime"] = Time(id + "-t"), ["3DModelObject"] = new JsonObject { ["Header"] = "OSD-3DO-V1.5", ["MInstanceID"] = "MI1", ["3DModelObjectID"] = id, ["3DModelObjectSpaceTime"] = Time(id + "-st") } };
    private static JsonObject Control(string controller, string data) => new()
    {
        ["ControlObject"] = new JsonObject
        {
            ["Header"] = "XRV-CTO-V1.0", ["ControlObjectID"] = controller + "-" + data, ["UEnvironmentID"] = Env, ["ControlObjectTime"] = Time(controller + "-cto-t"), ["ControllerID"] = controller,
            ["ControlData"] = data, ["ControlQualifier"] = new JsonObject { ["Header"] = "TFA-CXQ-V1.5", ["ControlQualifierID"] = controller + "-q", ["SubTypes"] = new JsonObject(), ["Formats"] = "OSC" }
        }
    };
    private static JsonObject Metaverse(string id) => new()
    {
        ["MetaverseObject"] = new JsonObject
        {
            ["Header"] = "XRV-MVO-V1.0", ["MetaverseObjectID"] = id, ["UEnvironmentID"] = Env, ["MetaverseObjectTime"] = Time(id + "-t"), ["MetaverseObjectData"] = new JsonArray(new JsonObject { ["ID"] = id }),
            ["MetaverseQualifier"] = new JsonObject { ["Header"] = "TFA-MAQ-V1.5", ["MetaverseQualifierID"] = id + "-q", ["SubTypes"] = new JsonObject(), ["Formats"] = new JsonObject() }
        }
    };

    public static string REDataIn(string id, JsonObject content) => new JsonObject { ["Header"] = EndAimProcessor.RDI, ["REDataInID"] = id, ["UEnvironmentID"] = Env, ["REDataInTime"] = Time(id + "-t"), ["REDataIn"] = content }.ToJsonString();
    public static string VEDataIn(string id, JsonObject content) => new JsonObject { ["Header"] = EndAimProcessor.VDI, ["VEDataInID"] = id, ["UEnvironmentID"] = Env, ["VEDataInTime"] = Time(id + "-t"), ["VEDataIn"] = content }.ToJsonString();

    // Anna is on the real stage; Guest1 is in the virtual venue; P1 sits in S1 and the Bot is in the virtual venue.
    private static string AnnaOnStage => REDataIn("rdi-1", new JsonObject { ["3DModelObjects"] = new JsonArray(Model3D("Anna")) });
    private static string Guest1Arrives => VEDataIn("vdi-1", new JsonObject { ["MetaverseObjects"] = new JsonArray(Metaverse("Guest1")) });
    private static string P1Sits => REDataIn("rdi-2", new JsonObject { ["ControlObjects"] = new JsonArray(Control("P1", "S1")) });
    private static string BotAppears => VEDataIn("vdi-2", new JsonObject { ["MetaverseObjects"] = new JsonArray(Metaverse("Bot")) });

    // ---- the AIMs alone ---------------------------------------------------------------------------------------------------------------------------

    [Fact]
    public void TheEnvironmentDescriptionTellsPerformersParticipantsAndObjectsByTheAssociations()
    {
        var sva = ScriptVenueAssociations.Read(XrvShow.Text("ScriptVenueAssociations.json"));
        var unknown = new List<string>();

        var re = EndAimProcessor.Describe(JsonNode.Parse(REDataIn("r", new JsonObject { ["3DModelObjects"] = new JsonArray(Model3D("Anna"), Model3D("Nobody")), ["ControlObjects"] = new JsonArray(Control("P1", "S1"), Control("FX1", "on")) }))!.AsObject(),
            true, EndAimProcessor.Sources(sva, true), "t", 0, unknown.Add);
        Assert.Equal(["Nobody"], unknown);
        var performance = Assert.Single(re, d => d.Family == Family.Performance).Descriptors;
        Assert.Empty(DescriptorsObject.Read(StatusKind.REPerformance, performance.ToJsonString()).Violations());
        Assert.Equal("Anna", performance["REPerformanceDescriptorsData"]![0]!["REPerformerID"]!.GetValue<string>());
        Assert.Equal("FX1", performance["REObjectDescriptors"]![0]!["REObjectID"]!.GetValue<string>());
        var participant = Assert.Single(re, d => d.Family == Family.Participant).Descriptors;
        Assert.Empty(DescriptorsObject.Read(StatusKind.REParticipant, participant.ToJsonString()).Violations());
        Assert.Equal("S1", participant["REParticipantDescriptorsData"]![0]!["SeatID"]!.GetValue<string>());

        var ve = EndAimProcessor.Describe(JsonNode.Parse(Guest1Arrives)!.AsObject(), false, EndAimProcessor.Sources(sva, false), "v", 0);
        var guests = Assert.Single(ve).Descriptors;
        Assert.Equal("Guest1", guests["VEParticipantDescriptorsData"]![0]!["VEParticipantID"]!.GetValue<string>());

        var latest = new Dictionary<StatusKind, JsonObject> { [StatusKind.REPerformance] = performance, [StatusKind.VEParticipant] = guests };
        var show = EndAimProcessor.Show(latest, "s", 0, Env);
        Assert.Empty(ShowDescriptors.Read(show.ToJsonString()).Violations());
    }

    [Fact]
    public void TheExperienceGenerationGivesTheCommandsOfAnActionAsTheVenueCanTakeThem()
    {
        var table = ActionTable.Read(XrvShow.Text("ActionTable.json"));
        var why = new List<string>();
        var rdo = RegAimProcessor.Generate("RE-LIGHTS-UP", table, REVenueSpecification.Read(XrvShow.Text("REVenueSpecification.json")), "o", 0, Env, why);
        Assert.NotNull(rdo); Assert.Empty(why);
        Assert.Empty(XrvSchemas.Violations("XRV1/V1.0/data/REDataOut.json", rdo!));
        var controls = rdo!["REDataOut"]!["ControlObjects"]!.AsArray().Select(c => c!["ControlObject"]!).ToList();
        Assert.Equal(["FX1", "FX2"], controls.Select(c => c["ControllerID"]!.GetValue<string>()));
        Assert.All(controls, c => Assert.Equal(255, JsonNode.Parse(c["ControlData"]!.GetValue<string>())!["Value"]!.GetValue<int>()));

        // a Channel the venue does not have is left out; an Action that is not in the table gives nothing
        var smaller = JsonNode.Parse(XrvShow.Text("REVenueSpecification.json"))!.AsObject();
        smaller["DMXUniverses"]![0]!["Channels"]!.AsArray().RemoveAt(1);
        why.Clear();
        var one = RegAimProcessor.Generate("RE-LIGHTS-UP", table, REVenueSpecification.Read(smaller.ToJsonString()), "o", 0, Env, why);
        Assert.Single(one!["REDataOut"]!["ControlObjects"]!.AsArray());
        Assert.Contains(why, w => w.Contains("Channel 2"));
        Assert.Null(RegAimProcessor.Generate("NO-SUCH", table, null, "o", 0, Env, why));

        why.Clear();
        var vdo = VegAimProcessor.Generate("VE-LAMP-DIM", table, VEVenueSpecification.Read(XrvShow.Text("VEVenueSpecification.json")), "v", 0, Env, why);
        Assert.NotNull(vdo); Assert.Empty(why);
        Assert.Empty(XrvSchemas.Violations("XRV1/V1.0/data/VEDataOut.json", vdo!));
        var command = JsonNode.Parse(vdo!["VEDataOut"]!["MetaverseObjects"]![0]!["MetaverseObject"]!["MetaverseObjectData"]![0]!["Data"]!.GetValue<string>())!;
        Assert.Equal("VLamp1", command["Object"]!.GetValue<string>());
        Assert.Equal(0.3, command["Value"]!.GetValue<double>());
    }

    // ---- the Composite under the Controller --------------------------------------------------------------------------------------------------------

    private sealed class Show : IDisposable
    {
        private readonly string work = Path.Combine(Path.GetTempPath(), "xrv-ltp-" + Guid.NewGuid().ToString("N"));
        public ControllerApi Api { get; }
        public int ModuleId { get; }                                                                       // the MODULE_ID the Controller returned when it started LTP
        // UNDER TRUST (AIF V3.0 Zero Trust and Profiles; review finding X2, 2026-11-14): the Controller is the verifier, and the Store has approved the fingerprint of
        // the package of each XRV AIM - its binary and the libraries it loads, Mpai.Xrv.Common among them (MPAI Store 4). Each AIM is measured before it runs;
        // one whose package is not what the Store approved refuses the Module. measuredFrom: where the Controller finds the binaries to measure (by default where they run).
        public Show(string? measuredFrom = null)
        {
            Directory.CreateDirectory(work);
            var settings = Path.Combine(work, "aim-settings.json"); File.WriteAllText(settings, "{}");
            Api = new ControllerApi(Approved(work), settings, new Measured(new XrvProvider(), measuredFrom));
            Api.Controller.Trust = new AIF.Trust.TrustDomain("xrv-controller");
            var started = Api.StartFlow(Ltp, out var moduleId);
            if (started != AifError.OK)
            {
                var why = $"{Ltp} did not start: {started}. {Api.StartRefusal}";
                Api.Dispose();
                try { Directory.Delete(work, true); } catch { }
                throw new InvalidOperationException(why);
            }
            ModuleId = moduleId;
        }

        // The L3s, and the Store's approval of the package of each XRV AIM, as built.
        private static string Approved(string work)
        {
            var amds = Path.Combine(work, "amds");
            Directory.CreateDirectory(amds);
            foreach (var file in Directory.EnumerateFiles(Repository.Amds, "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));
            var provider = new XrvProvider();
            var fingerprints = new AIF.Store.ImplementationFingerprints(amds);
            foreach (var file in Directory.EnumerateFiles(amds, "1XRV-*.json"))
            {
                var l3 = JsonNode.Parse(File.ReadAllText(file))!;
                var aim = (string)l3["Identifier"]!["AIMName"]!;
                if (!provider.CanCreate(aim)) continue;                                                    // the Composite AIM: its Sub-AIMs are measured
                var binary = (string)l3["Implementations"]![0]!["BinaryName"]!;
                fingerprints.Approve(aim, binary, AIF.Store.ImplementationFingerprints.OfImplementation(provider.ImplementationOf(aim)!), DateTimeOffset.UtcNow);
            }
            return amds;
        }

        // The XRV provider, its binaries measured where measuredFrom says.
        private sealed class Measured(XrvProvider inner, string? measuredFrom) : IAimProvider
        {
            public bool CanCreate(string aimName) => inner.CanCreate(aimName);
            public string? ImplementationOf(string aimName) =>
                inner.ImplementationOf(aimName) is { } file && measuredFrom is not null ? Path.Combine(measuredFrom, Path.GetFileName(file)) : inner.ImplementationOf(aimName);
            public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage) => inner.Create(aimName, settings, storage);
            public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, AIF.SharedStorage.ISharedStorage? storage, AIF.SharedStorage.ISharedStorage? privateStorage,
                AIF.SharedStorage.IRuledStorage? moduleStorage, IAccessReader? access) => ((IAimProvider)inner).Create(aimName, settings, storage, privateStorage, moduleStorage, access);
        }
        public void Write(string dataType, string json) => Assert.Equal(AifError.OK, Api.InputWrite(ModuleId, dataType, 1, json, 10_000));
        public JsonObject? Read(string dataType, int timeoutMs = 10_000)
        {
            var r = Api.OutputRead(ModuleId, dataType, 1, timeoutMs);
            return r.Error == AifError.OK ? JsonNode.Parse(r.Json!)!.AsObject() : null;
        }
        public string Why() => string.Join("; ", Api.Status(ModuleId).Aims.Select(a => a.Aim + " " + a.Status + " " + a.Reason + " " + string.Join("/", a.Reports)));
        public void Dispose() { try { Api.StopFlow(ModuleId); } catch { } Api.Dispose(); try { Directory.Delete(work, true); } catch { } }
    }

    private static IEnumerable<string> ReCommands(JsonObject rdo) => rdo["REDataOut"]!["ControlObjects"]!.AsArray().Select(c => c!["ControlObject"]!["ControllerID"] + "=" + JsonNode.Parse(c!["ControlObject"]!["ControlData"]!.GetValue<string>())!["Value"]);
    private static IEnumerable<string> VeCommands(JsonObject vdo) => vdo["VEDataOut"]!["MetaverseObjects"]!.AsArray().Select(m => JsonNode.Parse(m!["MetaverseObject"]!["MetaverseObjectData"]![0]!["Data"]!.GetValue<string>())!).Select(c => c["Object"] + "=" + c["Value"]);

    // FROM THE PACKAGES (review finding X2): the L3s of MPAIStore/L3s/XRV - LTP, which carries no code, and the seven leaf AIMs, each with the address of its package -
    // are submitted to a running Store, which measures each package; the Controller is given only these L3s, fetches each package, has the Store verify it, loads the
    // plug-in it carries and runs the mini show. The packages are those AIMs/Build-Packages.ps1 made (MPAIStore/Packages; published, the release xrv-v1.0).
    [SkippableFact]
    public async Task TheMiniShowRunsFromThePackagesTheStoreApproved()
    {
        var packages = Path.Combine(Repository.Root, "MPAIStore", "Packages");
        var l3s = Path.Combine(Repository.Root, "MPAIStore", "L3s", "XRV");
        Skip.IfNot(Directory.Exists(l3s) && File.Exists(Path.Combine(packages, "XRV-PFI-V1.0-I02-win-x64.zip")), "The packages of XRV are not in " + packages);
        const string module = "1XRV-LTP-V1.0-I02";
        string[] leaves = ["ACG", "END", "OSI", "PFI", "PTI", "REG", "VEG"];

        using var store = new StoreTests.RunningStore();
        foreach (var zip in Directory.GetFiles(packages, "XRV-*-I02-win-x64.zip")) File.Copy(zip, Path.Combine(store.Packages, Path.GetFileName(zip)));
        var amds = Path.Combine(store.Root, "submitted"); Directory.CreateDirectory(amds);
        foreach (var file in Directory.GetFiles(l3s, "*.json"))
            File.WriteAllText(Path.Combine(amds, Path.GetFileName(file)), System.Text.RegularExpressions.Regex.Replace(File.ReadAllText(file),
                "https://github.com/leonardo-chiariglione/packages/releases/download/xrv-v[0-9.]+", new Uri(store.Packages).AbsoluteUri.TrimEnd('/')));
        foreach (var id in leaves.Select(l => $"1XRV-{l}-V1.0-I02").Append(module))
        {
            var (status, body) = await StoreTests.Submit(store.Http, JsonNode.Parse(File.ReadAllText(Path.Combine(amds, id + ".json")))!, null, StoreTests.Admin);
            Assert.True(status == System.Net.HttpStatusCode.Created, $"{id}: {status} {body}");
        }

        var said = new List<string>();
        var settings = Path.Combine(store.Root, "aim-settings.json"); File.WriteAllText(settings, "{}");
        using var api = new ControllerApi(amds, settings, s => new PackageAimProvider(s, Path.Combine(store.Root, "cache"), line => { lock (said) said.Add(line); }, new AIF.Store.HttpAimSource(store.Url)));
        Assert.True(api.StartFlow(module, out var moduleId) == AifError.OK, $"{module}: {api.StartRefusal} :: {string.Join(" / ", said)}");
        try
        {
            foreach (var (type, name) in new[] { (Types.ScriptVenueAssociations, "ScriptVenueAssociations.json"), (Types.REVenueSpecification, "REVenueSpecification.json"),
                                                 (Types.VEVenueSpecification, "VEVenueSpecification.json"), (Types.ActionTable, "ActionTable.json"), (Types.Script, "Script.json") })
                Assert.Equal(AifError.OK, api.InputWrite(moduleId, type, 1, XrvShow.Text(name), 10_000));
            Assert.Equal(AifError.OK, api.InputWrite(moduleId, EndAimProcessor.RDI, 1, AnnaOnStage, 10_000));
            var up = api.OutputRead(moduleId, RegAimProcessor.RDO, 1, 30_000);                // the first: seven packages, each loaded and its schemas built for the first time
            Assert.True(up.Error == AifError.OK, $"no RE Data Out for C1: {up.Error} :: " + string.Join("; ", api.Status(moduleId).Aims.Select(a => a.Aim + " " + a.Status + " " + a.Reason + " " + string.Join("/", a.Reports))));
            Assert.Equal(["FX1=255", "FX2=255"], ReCommands(JsonNode.Parse(up.Json!)!.AsObject()));
            Assert.Equal(AifError.OK, api.InputWrite(moduleId, EndAimProcessor.VDI, 1, Guest1Arrives, 10_000));
            var on = api.OutputRead(moduleId, VegAimProcessor.VDO, 1, 10_000);
            Assert.True(on.Error == AifError.OK, $"no VE Data Out for C2: {on.Error}");
            Assert.Equal(["VLamp1=1.0"], VeCommands(JsonNode.Parse(on.Json!)!.AsObject()));
            Assert.True(said.Count(l => l.Contains("from the package")) == leaves.Length, "the AIMs did not all come from packages: " + string.Join(" / ", said));
        }
        finally { api.StopFlow(moduleId); }
    }

    // A LIBRARY OF THE PACKAGE CHANGED (review findings X2 and X3): Mpai.Xrv.Common - which most of Performance, Participant and Object Status Interpretation is -
    // with one byte added. The fingerprint of the package of each AIM that loads it is no longer what the Store approved: the Module is refused, NOT_TRUSTED.
    [Fact]
    public void AChangedLibraryOfThePackageRefusesTheModule()
    {
        var altered = Path.Combine(Path.GetTempPath(), "xrv-altered-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(altered);
        try
        {
            var built = Path.GetDirectoryName(new XrvProvider().ImplementationOf(XrvProvider.Pfi))!;
            foreach (var file in Directory.EnumerateFiles(built, "*.dll")) File.Copy(file, Path.Combine(altered, Path.GetFileName(file)));
            using (var common = File.Open(Path.Combine(altered, "Mpai.Xrv.Common.dll"), FileMode.Append)) common.WriteByte(0);
            var refused = Assert.Throws<InvalidOperationException>(() => new Show(measuredFrom: altered));
            Assert.Contains("NotTrusted", refused.Message);
            Assert.Contains("not the binary the Store approved", refused.Message);
        }
        finally { try { Directory.Delete(altered, true); } catch { } }
    }

    [Fact]
    public void TheMiniShowRunsThroughTheLiveTheatricalPerformance()
    {
        using var show = new Show();
        show.Write(Types.ScriptVenueAssociations, XrvShow.Text("ScriptVenueAssociations.json"));
        show.Write(Types.REVenueSpecification, XrvShow.Text("REVenueSpecification.json"));
        show.Write(Types.VEVenueSpecification, XrvShow.Text("VEVenueSpecification.json"));
        show.Write(Types.ActionTable, XrvShow.Text("ActionTable.json"));
        show.Write(Types.Script, XrvShow.Text("Script.json"));

        // C1 (Real): Anna is on stage -> both lamps up
        show.Write(EndAimProcessor.RDI, AnnaOnStage);
        var up = show.Read(RegAimProcessor.RDO);
        Assert.True(up is not null, "no RE Data Out for C1. " + show.Why());
        Assert.Equal(["FX1=255", "FX2=255"], ReCommands(up!));

        // C2 (Virtual): Guest1 comes into the virtual venue -> the virtual lamp on
        show.Write(EndAimProcessor.VDI, Guest1Arrives);
        var on = show.Read(VegAimProcessor.VDO);
        Assert.True(on is not null, "no VE Data Out for C2. " + show.Why());
        Assert.Equal(["VLamp1=1.0"], VeCommands(on!));

        // C3 (Mixed): P1 sits in S1 and the Bot appears -> lamps off in both venues
        show.Write(EndAimProcessor.RDI, P1Sits);
        show.Write(EndAimProcessor.VDI, BotAppears);
        var off = show.Read(RegAimProcessor.RDO);
        var vOff = show.Read(VegAimProcessor.VDO);
        Assert.True(off is not null && vOff is not null, "no Data Out for C3. " + show.Why());
        Assert.Equal(["FX1=0", "FX2=0"], ReCommands(off!));
        Assert.Equal(["VLamp1=0.0"], VeCommands(vOff!));

        // nothing more: the show has ended
        Assert.Null(show.Read(RegAimProcessor.RDO, 1_000));
        Assert.Null(show.Read(VegAimProcessor.VDO, 1_000));
    }

    // The Participant Data the User Agent gives leaves the Composite unchanged (Action Generation passes it through).
    [Fact]
    public void TheParticipantDataLeavesUnchanged()
    {
        using var show = new Show();
        show.Write(Types.ActionTable, XrvShow.Text("ActionTable.json"));
        show.Write(Types.Script, XrvShow.Text("Script.json"));
        show.Write(Types.ScriptVenueAssociations, XrvShow.Text("ScriptVenueAssociations.json"));     // Action Generation requires it (EXE-6)
        var data = XrvShow.Text("ParticipantData.json");
        show.Write(Types.ParticipantData, data);
        var back = show.Read(Types.ParticipantData);
        Assert.True(back is not null, show.Why());
        Assert.Equal(JsonNode.Parse(data)!.ToJsonString(), back!.ToJsonString());
    }
}
