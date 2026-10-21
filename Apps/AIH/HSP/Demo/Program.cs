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
        var hbe = new HbeUserAgent(Controller("hbe", "BackEnd"), chain, Orch("HBE.orch"), Path.Combine(Work, "hbe", "secure-storage"));
        Cleanup.Add(hbe);
        var trust = new BackEndTrust("hbe-1");
        var door = new TrustedBackEnd(hbe, trust);
        Say("The Back End is the Trust Authority: it admits the parties that will exchange data with it.");

        IBackEnd Join(string party, string subject)
        {
            var admitted = trust.Enroll(party, subject);
            admitted.Establish();
            Say($"  {party}: enrolled, trust established with the Back End.");
            return new TrustedClient(admitted, door);
        }

        Step("Two End Users start their Health Front Ends, and the Third-Party User joins");
        var one = Front("user-1", Join("hfe:user-1", "user-1"));
        var two = Front("user-2", Join("hfe:user-2", "user-2"));
        var tpu = Join("tpu:tpu-1", "tpu-1");

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
        var tpuTokens = (JsonObject)AihJson.RegisterResponseOf(JsonNode.Parse(tpu.Register(ThirdPartyRegistration("tpu-1")))!.AsObject())!["Tokens"]!;
        Say($"  tpu-1 registered, Tokens: {string.Join(", ", AihJson.ServicesOf(tpuTokens))}");
        var inside = JsonNode.Parse(tpu.Process(ProcessRequest("tpu-1", "AIHDataProcessing", tpuTokens, "ahd-1")))!.AsObject();
        Say($"  ahd-1 (licensed to tpu-1): {(Done(inside) ? "ANSWERED" : "refused")} - risk {AihJson.ResponseOf(inside)?["AIHDataOrAIHDataID"]?[0]?["AIHData"]?["ARAData"]?["ARAData"]?["Risk"]}");
        var outside = JsonNode.Parse(tpu.Process(ProcessRequest("tpu-1", "AIHDataProcessing", tpuTokens, "ahd-2")))!.AsObject();
        Say($"  ahd-2 (not licensed to tpu-1): {(Done(outside) ? "answered?!" : "REFUSED")}");

        Step("Both End Users have the Back End de-identify their AIH Data; then they audit");
        Say($"  user-1 ahd-1 (licence states Pseudonymised): {State(one.DeIdentify("ahd-1", "De-Identify", "Hashing"))}");
        Say($"  user-2 ahd-2 (licence states Identified):    {State(two.DeIdentify("ahd-2", "De-Identify", "Hashing"))}");
        Say($"  audit of user-1: {one.Audit()["Audit"]!["Response"]!["AIHDataIDs"]!.AsArray().Count} unlicensed operation(s)");
        var unlicensed = two.Audit()["Audit"]!["Response"]!["AIHDataIDs"]!.AsArray();
        Say($"  audit of user-2: {unlicensed.Count} unlicensed operation(s): {string.Join(", ", unlicensed.Select(u => u!["AIHDataID"]))}");

        Step("A round of Federated Learning: the Back End asks each Front End for the model it trained, and averages them");
        var model = hbe.FederatedLearning("model-0", ("hfe-1", one), ("hfe-2", two));
        var weights = new float[2];
        Buffer.BlockCopy(Convert.FromBase64String(model["MLModelData"]![0]!["Data"]!.ToString()), 0, weights, 0, 8);
        Say($"  new NN Model {model["MLModelObjectID"]}: weights [{weights[0]:0.###}, {weights[1]:0.###}]");

        Step("The Blockchain");
        Say($"  {chain.Ledger.Count} entries; the chain holds together: {chain.Verify()}");
        foreach (var e in chain.Ledger.Where(e => e.Kind == "Confirm" && !e.Allowed))
            Say($"  refused: {e.Requester} for {e.Detail} on {e.AihDataId}");
    }

    // ---- the parts ---------------------------------------------------------------------------------------------------------------------------------------

    private static HfeUserAgent Front(string user, IBackEnd backEnd)
    {
        var ua = new HfeUserAgent(Controller("hfe-" + user, "FrontEnd"), backEnd, user, Orch("HFE.orch"), Path.Combine(Work, "hfe-" + user, "secure-data-vault"));
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

        var address = StartHost(amds, settings, name);
        var api = new ControllerApi(amds, settings, _ => new CompositeProvider());
        api.Controller.AimHostKey = Key;
        foreach (var aim in aims) api.Controller.AimHosts[aim] = address;
        Cleanup.Add(api);
        return api;
    }

    private static string StartHost(string amds, string settings, string name)
    {
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true, WorkingDirectory = Root };
        foreach (var arg in new[]
        {
            Path.Combine(Root, "AIF", "AimHost", "bin", "Debug", "net10.0", "AIF.AimHost.dll"), "--port", "0", "--key", Key, "--amds", amds, "--settings", settings,
            "--storage", Path.Combine(Work, name, "aimhost"), "--provider", Path.Combine(AppContext.BaseDirectory, "Mpai.Aih.dll") + ":Mpai.Aih.AihProvider"
        }) info.ArgumentList.Add(arg);
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
