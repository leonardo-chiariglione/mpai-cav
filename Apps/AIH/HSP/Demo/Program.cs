using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Aih;
using Mpai.Aih.Chain;
using Mpai.Aih.Hsp;

namespace Mpai.Aih.Demo;

// AIH-HSP, THE FIRST SLICE, NARRATED. Two End Users, each with a Health Front End; the Health Back End, the Trust Authority, with a test Blockchain; a Third-Party
// User. Each Front End and the Back End is a Controller with its Module whose AIMs run on an AIM host of their own: three Controllers, three AIM hosts, three User
// Agents, one program. Nothing is kept: the storage is a temporary folder.
//
//   AihDemo
internal static class Program
{
    private static readonly string Root = FindRoot();
    private static readonly List<IDisposable> Cleanup = [];
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "aih-demo-" + Guid.NewGuid().ToString("N"));

    private static void Say(string text) => Console.WriteLine(text);
    private static void Step(string text) { Console.WriteLine(); Console.WriteLine("== " + text); }

    private static int Main()
    {
        Say("AIH-HSP V1.1 - the Health Secure Platform: the first slice");
        Say($"Software tree: {Root}");
        try
        {
            Run();
            Say(""); Say("Done: every step did what it should.");
            return 0;
        }
        catch (Exception ex)
        {
            Console.WriteLine(); Console.WriteLine("FAILED: " + ex.Message); if (Environment.GetEnvironmentVariable("AIH_DEMO_TRACE") == "1") Console.WriteLine(ex.ToString());
            return 1;
        }
        finally
        {
            foreach (var d in Enumerable.Reverse(Cleanup)) { try { d.Dispose(); } catch { } }
            try { Directory.Delete(Work, true); } catch { }
        }
    }

    private static void Run()
    {
        var chain = new HashChainBlockchain();

        Step("The Health Back End starts: a Controller, and its Module on an AIM host");
        var hbe = new HbeUserAgent(Controller("hbe", "BackEnd"), chain, Orch("HBE.orch"), Path.Combine(Work, "hbe", "secure-storage"), models: Store, frontEndL3: FrontEndL3());
        Cleanup.Add(hbe);
        Hbe = hbe;
        if (hbe.Anchor is null) throw new InvalidOperationException("the Back End's Controller is not the Trust Authority of its service.");
        Say("The Back End's Controller is the Trust Authority of its service: the Front Ends' Controllers join it.");

        Step("Two End Users start their Health Front Ends, and the Third-Party User joins");
        var one = Front("user-1");
        var two = Front("user-2");
        var tpuFront = Front("tpu-1", "tpu-1");              // the Third-Party User runs a Health Front End too: its User Profile makes it do what a Third-Party User does

        Step("The End Users register with the Back End, each through their own Front End");
        var services = new[] { "Store", "DeIDAndAnonym", "Auditing" };
        Say($"  user-1: {Ok(one.Register(UserRegistration("user-1", services)))}, Tokens: {string.Join(", ", AihJson.ServicesOf(one.Tokens))}");
        Say($"  user-2: {Ok(two.Register(UserRegistration("user-2", services)))}");

        Step("user-1 acquires Health Data (75 beats a minute) and provides a Model Licence that names the Third-Party User tpu-1");
        var alert1 = one.Acquire(HealthData("user-1", 190), Licence("user-1", "ahd-1", licenseThirdParty: true));
        Say(alert1 is null ? "  The AIH Data is made, checked, and kept in the Secure Data Vault. No alert: the rate is regular." : "  An alert?!");

        Step("user-2 acquires Health Data (120 beats a minute); the licence names no Third-Party User and states Identified for the Back End");
        var alert2 = two.Acquire(HealthData("user-2", 125), Licence("user-2", "ahd-2", licenseThirdParty: false, status: "Identified"));
        Say("  ALERT to the End User: " + (alert2 ?? throw new InvalidOperationException("no alert"))["ARAData"]!["Risk"] + " risk (Anomaly and Risk Alerting).");

        Step("Both upload to the Back End: the Back End has the Smart Contract made, and keeps the AIH Data");
        Say($"  user-1: {Ok(one.Upload("ahd-1"))}, Smart Contract {chain.ContractOf("ahd-1")}");
        Say($"  user-2: {Ok(two.Upload("ahd-2"))}, Smart Contract {chain.ContractOf("ahd-2")}");

        Step("The Third-Party User registers and asks for the AIH Data to be processed");
        var tpuTokens = (JsonObject)AihJson.RegisterResponseOf(tpuFront.Register(JsonNode.Parse(ThirdPartyRegistration("tpu-1"))!.AsObject()))!["Tokens"]!;
        Say($"  tpu-1 registered, Tokens: {string.Join(", ", AihJson.ServicesOf(tpuTokens))}");
        var inside = tpuFront.Process("ahd-1");
        Say($"  ahd-1 (licensed to tpu-1): {(Done(inside) ? "ANSWERED" : "refused")} - risk {AihJson.ResponseOf(inside)?["AIHDataOrAIHDataID"]?[0]?["AIHData"]?["ARAData"]?["ARAData"]?["Risk"]}");
        var outside = tpuFront.Process("ahd-2");
        Say($"  ahd-2 (not licensed to tpu-1): {(Done(outside) ? "answered?!" : "REFUSED")}");

        Step("Both End Users have the Back End de-identify their AIH Data; then they audit");
        Say($"  user-1 ahd-1 (licence states Pseudonymised): {State(one.DeIdentify("ahd-1", "De-Identify", "Hashing"))}");
        Say($"  user-2 ahd-2 (licence states Identified):    {State(two.DeIdentify("ahd-2", "De-Identify", "Hashing"))}");
        Say($"  audit of user-1: {one.Audit()["Audit"]!["Response"]!["AIHDataIDs"]!.AsArray().Count} unlicensed operation(s)");
        var unlicensed = two.Audit()["Audit"]!["Response"]!["AIHDataIDs"]!.AsArray();
        Say($"  audit of user-2: {unlicensed.Count} unlicensed operation(s): {string.Join(", ", unlicensed.Select(u => u!["AIHDataID"]))}");

        Step("Federated Learning: the Back End publishes the Neural Network Model AIM with its first model; the Front Ends start again and take it");
        Say($"  the AIM {hbe.PublishModel(Mpai.Aih.Hfl.FrontEndTrainer.ModelObject("model-0", new float[] { 0f, 0f }, "the Back End's first model"))} and the Front End version {hbe.FrontEndVersion} are in the MPAI Store");
        one = Restart(one, "user-1"); two = Restart(two, "user-2");
        Say("  the Neural Network Model processes new Health Data of each End User, and learns");
        one.Acquire(HealthData("user-1", 190), Licence("user-1", "ahd-5", licenseThirdParty: false));
        two.Acquire(HealthData("user-2", 125), Licence("user-2", "ahd-6", licenseThirdParty: false));

        Step("A round of Federated Learning: the Back End asks each Front End for the model its Neural Network Model AIM holds, and averages them");
        var model = hbe.FederatedLearning("model-0", "hfe-1", "hfe-2");
        var weights = new float[2];
        Buffer.BlockCopy(Convert.FromBase64String(model["MLModelData"]![0]!["Data"]!.ToString()), 0, weights, 0, 8);
        Say($"  new NN Model {model["MLModelObjectID"]}: weights [{weights[0]:0.###}, {weights[1]:0.###}]; the AIM {hbe.ModelAimId} and the Front End version {hbe.FrontEndVersion} are in the MPAI Store");

        Step("The Blockchain");
        Say($"  {chain.Ledger.Count} entries; the chain holds together: {chain.Verify()}");
        foreach (var e in chain.Ledger.Where(e => e.Kind == "Confirm" && !e.Allowed))
            Say($"  refused: {e.Requester} for {e.Detail} on {e.AihDataId}");
    }

    // ---- the parts ---------------------------------------------------------------------------------------------------------------------------------------

    private static HbeUserAgent? Hbe;

    // A Front End's Module started again on the same Controller, which follows the MPAI Store: it takes the newest Front End version and the AIM it names.
    private static HfeUserAgent Restart(HfeUserAgent ua, string user)
    {
        ua.Dispose();
        var again = new HfeUserAgent(Controllers["hfe-" + user], user, Orch("HFE.orch"), Path.Combine(Work, "hfe-" + user, "secure-data-vault"),
                                     ("AIH-HBE", Hbe!.Anchor!, Hbe.Rendezvous!, "hfe-" + user.Split('-')[1]));
        Cleanup.Add(again);
        Say($"  Front End of {user}: started again, as version {Controllers["hfe-" + user].Repository!.VersionOf("1AIH-HFE-V1.1-I01")}.");
        return again;
    }

    private static readonly Dictionary<string, ControllerApi> Controllers = [];

    // THE MPAI STORE (the test one): the Back End publishes in it the AIMs that wrap its models and the Front End versions that have them; it publishes only what conforms to the L2.
    // The Back End's organisation is a Trust Anchor; the Store signs with a key the Front Ends' Controllers know.
    private static readonly System.Security.Cryptography.ECDsa StoreKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    private static readonly AIF.Trust.TrustAuthority Organisation = new("hbe-organisation");
    private static Mpai.Aih.Nnm.ModelAimPublisher? store;
    private static Mpai.Aih.Nnm.ModelAimPublisher Store => store ??= new(Path.Combine(Work, "mpai-store"), Path.Combine(Work, "hbe-implementations"),
        l3 => new AIF.Metadata.L2Conformance(Path.Combine(Root, "schemas"), _ => null).Check(l3));

    // The Front End's L3 as the Back End has it: its AIMs executing apart from the Controller.
    private static string FrontEndL3() => Apart(File.ReadAllText(Path.Combine(Root, "AIMs", "AMDs", "1AIH-HFE-V1.1-I01.json")), FrontEndAims);
    private static string Apart(string text, IEnumerable<string> aims)
    {
        foreach (var aim in aims) text = Regex.Replace(text, "(\"AIMName\"\\s*:\\s*\"" + aim + "\"\\s*,\\s*\"Relation\"\\s*:\\s*)\"Internal\"", "$1\"External\"");
        return text;
    }

    private static HfeUserAgent Front(string user, string? name = null)
    {
        var ua = new HfeUserAgent(Controller("hfe-" + user, "FrontEnd"), user, Orch("HFE.orch"), Path.Combine(Work, "hfe-" + user, "secure-data-vault"),
                                  ("AIH-HBE", Hbe!.Anchor!, Hbe.Rendezvous!, name ?? "hfe-" + user.Split('-')[1]));
        Cleanup.Add(ua);
        Say($"  Front End of {user}: started.");
        return ua;
    }

    private static string Orch(string name) => Path.Combine(Root, "Apps", "AIH", "HSP", name);

    private static readonly string[] BackEndAims = ["1AIH-HDP-V1.1-I01", "1AIH-ARA-V1.1-I01", "1AIH-DIA-V1.1-I01", "1AIH-ADT-V1.1-I01", "1AIH-HFL-V1.1-I01"];
    private static readonly string[] FrontEndAims = ["1AIH-MDL-V1.1-I01", "1AIH-HDP-V1.1-I01", "1AIH-ARA-V1.1-I01"];
    private const string Key = "aih-demo-key";

    // A Controller whose Module's AIMs run on an AIM host of their own: the composite's L3 places them External, the host runs them, the Controller holds no AIM.
    private static ControllerApi Controller(string name, string role)
    {
        var folder = Path.Combine(Work, name); Directory.CreateDirectory(folder);
        var settings = Path.Combine(folder, "aim-settings.json");
        File.WriteAllText(settings, new JsonObject
        {
            ["1AIH-HDP-V1.1-I01"] = new JsonObject { ["Role"] = role, ["BackEndId"] = "hbe-1" }, ["1AIH-ADT-V1.1-I01"] = new JsonObject { ["BackEndId"] = "hbe-1" }
        }.ToJsonString());
        var aims = role == "BackEnd" ? BackEndAims : FrontEndAims;
        var composite = role == "BackEnd" ? "1AIH-HBE-V1.1-I01" : "1AIH-HFE-V1.1-I01";
        var amds = Path.Combine(folder, "amds"); Directory.CreateDirectory(amds);
        foreach (var file in Directory.EnumerateFiles(Path.Combine(Root, "AIMs", "AMDs"), "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));
        var text = File.ReadAllText(Path.Combine(amds, composite + ".json"));
        foreach (var aim in aims) text = Regex.Replace(text, "(\"AIMName\"\\s*:\\s*\"" + aim + "\"\\s*,\\s*\"Relation\"\\s*:\\s*)\"Internal\"", "$1\"External\"");
        File.WriteAllText(Path.Combine(amds, composite + ".json"), text);

        // A FRONT END'S CONTROLLER FOLLOWS THE MPAI STORE: it holds the Metadata of its own (a repository), obtains a newer Front End and the AIM it names when its Module starts,
        // and hands the AIM to the AIM host, which obtains the Implementation and checks it. Both have an identity under the organisation's Trust Anchor; both know the Store's key.
        var extra = new List<string>();
        AIF.Store.AimRepository? repository = null; AIF.Store.StoreAimSource? source = null;
        if (role == "FrontEnd")
        {
            var spki = (System.Security.Cryptography.ECDsa key) => Convert.ToHexString(key.ExportSubjectPublicKeyInfo());
            var host = Organisation.Admit("module-" + name, TimeSpan.FromDays(30));
            var identity = new JsonObject
            {
                ["ControllerId"] = "module-" + name, ["Credential"] = host.Credential.DeepClone(), ["Cii"] = host.Anchor.Object(),
                ["Anchors"] = new JsonObject { [Organisation.Anchor.AnchorId] = spki(Organisation.Anchor.PublicKey) }
            };
            var identityFile = Path.Combine(folder, "module-identity.json"); File.WriteAllText(identityFile, identity.ToJsonString());
            extra.AddRange(["--identity", identityFile, "--store-key", "store-key-1=" + spki(StoreKey)]);
            var controllerAmds = Path.Combine(folder, "controller-amds"); Directory.CreateDirectory(controllerAmds);
            foreach (var file in Directory.EnumerateFiles(amds, "*.json")) File.Copy(file, Path.Combine(controllerAmds, Path.GetFileName(file)));
            var admission = Organisation.Admit(name, TimeSpan.FromDays(30));
            repository = new AIF.Store.AimRepository(controllerAmds)
            {
                Identity = new AIF.Store.AimLicences.ControllerIdentity(name, admission.Credential, admission.Anchor.Object(), [], a => a == Organisation.Anchor.AnchorId ? Organisation.Anchor.PublicKey : null),
                StoreKeys = id => id == "store-key-1" ? StoreKey : null
            };
            source = new AIF.Store.StoreAimSource(Path.Combine(Work, "mpai-store"), Organisation.Anchor.AnchorId, new AIF.Store.AimLicences.Issuer(StoreKey, "store-key-1", "mpai-store", "P7D"));
        }

        var address = StartHost(amds, settings, name, extra);
        var api = new ControllerApi(repository?.Folder ?? amds, settings, _ => new CompositeProvider());
        api.Controller.AimHostKey = Key;
        foreach (var aim in aims.Append(composite)) api.Controller.AimHosts[aim] = address;            // and for the Module itself: the AIMs that come later
        if (repository is not null) { api.Repository = repository; api.Source = source; api.FollowNewerVersions = true; api.FetchImplementations = false; }
        Controllers[name] = api;
        Cleanup.Add(api);
        return api;
    }

    private static string StartHost(string amds, string settings, string name, IEnumerable<string> extra)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Root };
        foreach (var arg in new[]
        {
            Path.Combine(Root, "AIF", "AimHost", "bin", "Debug", "net10.0", "AIF.AimHost.dll"), "--port", "0", "--key", Key, "--amds", amds, "--settings", settings,
            "--storage", Path.Combine(Work, name, "aimhost"), "--provider", Path.Combine(AppContext.BaseDirectory, "Mpai.Aih.dll") + ":Mpai.Aih.AihProvider"
        }.Concat(extra)) info.ArgumentList.Add(arg);
        var process = Process.Start(info)!;
        Cleanup.Add(new Killer(process));
        var clock = Stopwatch.StartNew();
        while (clock.Elapsed < TimeSpan.FromSeconds(30))
        {
            var line = process.StandardOutput.ReadLine() ?? throw new InvalidOperationException("The AIM host stopped: " + process.StandardError.ReadToEnd());
            var m = Regex.Match(line, @"listening on (\d+)");
            if (m.Success)
            {
                _ = System.Threading.Tasks.Task.Run(() => { while (process.StandardOutput.ReadLine() is not null) { } });
                Say($"  {name}: its Module runs on an AIM host at port {m.Groups[1].Value}.");
                return $"localhost:{m.Groups[1].Value}";
            }
        }
        throw new InvalidOperationException("The AIM host did not start.");
    }

    private sealed class Killer(Process process) : IDisposable
    {
        public void Dispose() { try { if (!process.HasExited) process.Kill(true); } catch { } }
    }

    // ---- the data ----------------------------------------------------------------------------------------------------------------------------------------

    private static JsonObject Time() => AihJson.Time();

    // An electrocardiogram of 16-bit samples at 250 Hz with a beat every `period` samples.
    private static JsonObject HealthData(string user, int period) => new()
    {
        ["Header"] = "AIH-HLD-V1.1", ["HealthDataID"] = AihJson.NewId("hd"), ["EndUserID"] = user, ["HealthDataTime"] = Time(),
        ["HealthDataData"] = new JsonArray(new JsonObject
        {
            ["Data"] = Convert.ToBase64String(Enumerable.Range(0, 2500).SelectMany(i => BitConverter.GetBytes((short)(i % period == 0 ? 12000 : 10))).ToArray())
        }),
        ["DataQualifier"] = new JsonObject
        {
            ["Header"] = "TFA-HDQ-V1.5", ["HealthDataQualifierID"] = "hdq",
            ["SpecialisedQualifier"] = new JsonObject { ["Header"] = "TFA-ECQ-V1.5", ["ECGQualifierID"] = "ecq", ["SubTypes"] = new JsonObject(), ["Formats"] = new JsonObject() }
        }
    };

    private static JsonObject Licence(string user, string aihData, bool licenseThirdParty, string status = "Pseudonymised")
    {
        var licence = new JsonObject
        {
            ["Header"] = "AIH-MDL-V1.1", ["ModelLicenceID"] = AihJson.NewId("ml"), ["ModelLicenceTime"] = Time(), ["EndUserID"] = user, ["AIHDataID"] = aihData, ["AIHDataType"] = "AIH-ECO",
            ["ModelLicenceData"] = new JsonObject
            {
                ["Common"] = new JsonObject { ["LicenceTime"] = Time(), ["EndUserID"] = user, ["AIHDataID"] = aihData, ["AIHDataUsage"] = "Research", ["AIHDataProcessingType"] = "PhysiologicalRiskPrediction" },
                ["Specific"] = new JsonArray(new JsonObject
                {
                    ["BackEndLicence"] = new JsonObject
                    {
                        ["EndUserID"] = user, ["BackEndID"] = "hbe-1", ["AIHDataID"] = aihData, ["AIHDataStatus"] = status, ["AIHDataProcess"] = "PhysiologicalRiskPrediction", ["AIHDataUsage"] = "Research"
                    }
                })
            }
        };
        if (licenseThirdParty)
            ((JsonArray)licence["ModelLicenceData"]!["Specific"]!).Add(new JsonObject
            {
                ["ThirdPartyUserLicence"] = new JsonObject
                {
                    ["BackEndID"] = "hbe-1", ["ThirdPartyUserID"] = "tpu-1", ["AIHDataID"] = aihData, ["Time"] = Time(),
                    ["LicensableEndUsers"] = new JsonArray(new JsonObject { ["UserID"] = user, ["AIHDataClasses"] = new JsonArray("AIH-ECO") }),
                    ["UserTypes"] = new JsonArray("Non-Profit Entity"), ["ProcessTypes"] = "PhysiologicalRiskPrediction", ["AnomalyTypes"] = new JsonArray("Point Anomaly"), ["UsageTypes"] = "Research"
                }
            });
        return licence;
    }

    private static JsonObject UserRegistration(string user, string[] services) => new()
    {
        ["Header"] = "AIH-REG-V1.1", ["RegisterID"] = AihJson.NewId("reg"), ["RegisterTime"] = Time(),
        ["RegisterData"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject
            {
                ["UserProfile"] = new JsonObject
                {
                    ["Header"] = "AIH-UPR-V1.1", ["UserID"] = user, ["UserProfileID"] = AihJson.NewId("up"),
                    ["UserProfileData"] = new JsonArray(new JsonObject { ["Human"] = new JsonObject { ["FirstName"] = "Ada", ["LastName"] = "Lovelace", ["Nationality"] = "GB" } })
                },
                ["RequestedServices"] = new JsonArray(services.Select(s => (JsonNode)new JsonObject { ["Service"] = s }).ToArray())
            }
        })
    };

    private static string ThirdPartyRegistration(string id) => new JsonObject
    {
        ["Header"] = "AIH-REG-V1.1", ["RegisterID"] = AihJson.NewId("reg"), ["RegisterTime"] = Time(),
        ["RegisterData"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject
            {
                ["ThirdPartyUserProfile"] = new JsonObject
                {
                    ["Header"] = "AIH-TPU-V1.1", ["ThirdPartyUserID"] = id, ["ThirdPartyUserProfileID"] = AihJson.NewId("tpup"),
                    ["ThirdPartyUserProfileData"] = new JsonObject { ["CompanyName"] = "Cardio Research Ltd", ["Country"] = "GB" },
                    ["Entitlements"] = new JsonObject
                    {
                        ["UserTypes"] = new JsonArray("Non-Profit Entity"), ["DataClasses"] = new JsonArray("AIH-ECO"), ["DataStatus"] = new JsonArray("Pseudonymised"),
                        ["ProcessTypes"] = new JsonArray("PhysiologicalRiskPrediction"), ["UsageTypes"] = new JsonArray("Research")
                    }
                },
                ["RequestedServices"] = new JsonArray(new JsonObject { ["Service"] = "AIHDataProcessing" })
            }
        })
    }.ToJsonString();

    private static string ProcessRequest(string requester, string type, JsonObject tokens, string id) => new JsonObject
    {
        ["Header"] = AihJson.Process, ["UserID"] = requester, ["AIHDataProcessID"] = AihJson.NewId("ahp"), ["AIHDataProcessTime"] = Time(),
        ["AIHDataProcess"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = new JsonArray(id), ["ProcessingType"] = type, ["Tokens"] = tokens.DeepClone() }
        })
    }.ToJsonString();

    private static bool Done(JsonObject response) => AihJson.ResponseOf(response)?["ProcessingStatus"]?.GetValue<bool>() == true;
    private static string Ok(JsonObject response) => (AihJson.RegisterResponseOf(response)?["Success"] ?? AihJson.ResponseOf(response)?["ProcessingStatus"])?.GetValue<bool>() == true ? "done" : "refused";
    private static string State(JsonObject response) => response["DeIDAndAnonymData"]![0]!["Response"]!["ProcessingState"]!.GetValue<bool>() ? "copy made" : "not done";

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "AIMs", "AMDs")) && Directory.Exists(Path.Combine(dir.FullName, "Apps", "AIH"))) return dir.FullName;
        throw new DirectoryNotFoundException("The software tree (AIMs\\AMDs and Apps\\AIH) is not above " + AppContext.BaseDirectory);
    }
}
