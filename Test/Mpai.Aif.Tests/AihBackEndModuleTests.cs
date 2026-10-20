using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Aih;
using Mpai.Aih.Chain;

namespace Mpai.Aif.Tests;

// THE HEALTH BACK END AS A MODULE under a Controller, the two apart (the Module's AIMs execute in a process of their own, as always): HDP, Anomaly and Risk Alerting, De-Identification and Anonymisation, Auditing and Health Federated
// Learning wired as the L3 says. The Back End's User Agent does here what its workflow does: it writes and reads the boundary Ports by Data Type and Port Number,
// carries what the Module asks of the Blockchain to the Blockchain (a test one) and gives the Module its answers; AIH Data Processing keeps the AIH Data in the Secure Storage through the storage API (there is no Storage AIM).
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class AihBackEndModuleTests : IDisposable
{
    private const string Hbe = "1AIH-HBE-V1.1-I01";
    private readonly string work = Path.Combine(Path.GetTempPath(), "aih-hbe-" + Guid.NewGuid().ToString("N"));
    private readonly AihApart apart;
    private ControllerApi api => apart.Api;
    private readonly HashChainBlockchain chain = new();
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> Schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);

    public AihBackEndModuleTests()
    {
        Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json");
        File.WriteAllText(settings, new JsonObject { ["1AIH-HDP-V1.1-I01"] = new JsonObject { ["Role"] = "BackEnd", ["BackEndId"] = "hbe-1" }, ["1AIH-ADT-V1.1-I01"] = new JsonObject { ["BackEndId"] = "hbe-1" } }.ToJsonString());
        apart = AihApart.Start(work, settings, Hbe, AihApart.BackEndAims);
        Assert.Equal(AifError.OK, api.StartFlow(Hbe));
        Assert.Equal(AifError.OK, api.SharedStorageInit(Hbe, Path.Combine(work, "secure-storage")));
    }

    public void Dispose() { apart.Dispose(); try { Directory.Delete(work, true); } catch { } }

    private void Write(string type, int n, JsonNode json) => Assert.Equal(AifError.OK, api.InputWrite(Hbe, type, n, json.ToJsonString(), 10_000));

    private JsonObject Read(string type, int n, int ms = 15_000)
    {
        var read = api.OutputRead(Hbe, type, n, ms);
        Assert.True(read.Error == AifError.OK, $"nothing at {type}#{n}: {read.Error}");
        return JsonNode.Parse(read.Json!)!.AsObject();
    }

    private bool Nothing(string type, int n, int ms = 800) => api.OutputRead(Hbe, type, n, ms).Error != AifError.OK;

    private static string Valid(string schema, JsonNode json)
    {
        var s = Schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "AIH1", "V1.1", "data", schema + ".json"))];
        using var doc = JsonDocument.Parse(json.ToJsonString());
        lock (AIF.Metadata.PublishedSchemas.Lock) return s.Evaluate(doc.RootElement).IsValid ? "valid" : "not valid";
    }

    private static JsonObject Request(string requester, string type, JsonObject tokens, params JsonNode[] items) => new()
    {
        ["Header"] = "AIH-AHP-V1.1", ["UserID"] = requester, ["AIHDataProcessID"] = AihJson.NewId("ahp"), ["AIHDataProcessTime"] = AihJson.Time(),
        ["AIHDataProcess"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = new JsonArray(items.Select(i => i.DeepClone()).ToArray()), ["ProcessingType"] = type, ["Tokens"] = tokens.DeepClone() }
        })
    };

    private JsonObject Register(JsonObject request, params string[] services)
    {
        request["RegisterData"]![0]!["Request"]!["RequestedServices"] = new JsonArray(services.Select(s => (JsonNode)new JsonObject { ["Service"] = s }).ToArray());
        Write(AihJson.Register, 1, request);
        var response = Read(AihJson.Register, 1);
        Assert.Equal("valid", Valid("Register", response));
        return (JsonObject)response["RegisterData"]![0]!["Response"]!["Tokens"]!;
    }

    // The Module asks the Blockchain for a Smart Contract; the Back End's User Agent makes it and answers.
    private JsonObject Contract()
    {
        var asked = Read(AihJson.Blockchain, 1);
        var data = asked["AIHDataID"]!.GetValue<string>();
        var licence = asked["BlockchainLicenceData"]![0]!["Request"]!["ModelLicence"]!.AsObject();
        var id = chain.Make(licence, data);
        return AihJson.BlockchainResponse(asked["UserID"]!.GetValue<string>(), data, id ?? "", id is not null);
    }

    // The Module asks the Blockchain to confirm; the Back End's User Agent asks it and answers.
    private JsonObject Confirmation(int port)
    {
        var asked = Read(AihJson.LicenceConfirm, port);
        var ask = asked["LicenceConfirmData"]![0]!["Request"]!;
        var requester = asked["LicenceConfirmData"]![0]!["Request"]!["RequesterID"]!.GetValue<string>();
        var allowed = chain.Confirm(ask["AIHDataID"]!.GetValue<string>(), ask["ProcessType"]!.GetValue<string>(), requester);
        return AihJson.LicenceConfirmResponse(asked["EndUserID"]?.GetValue<string>() ?? "", asked["LicenceConfirmID"]!.GetValue<string>(), allowed);
    }

    private static JsonObject UploadData(string id, string user = "user-1")
    {
        var data = AihSamples.AihData(user, id);
        data["AIHData"]!.AsObject().Remove("SmartContractID"); data["AIHData"]!.AsObject().Remove("DeIDAndAnonym");
        var licence = (JsonObject)data["AIHData"]!["ModelLicence"]!;
        licence["EndUserID"] = user;
        ((JsonArray)licence["ModelLicenceData"]!["Specific"]!).Add(new JsonObject
        {
            ["ThirdPartyUserLicence"] = new JsonObject
            {
                ["BackEndID"] = "hbe-1", ["ThirdPartyUserID"] = "tpu-1", ["AIHDataID"] = id, ["Time"] = AihSamples.Time("t"),
                ["LicensableEndUsers"] = new JsonArray(new JsonObject { ["UserID"] = user, ["AIHDataClasses"] = new JsonArray("AIH-ECO") }),
                ["UserTypes"] = new JsonArray("Non-Profit Entity"), ["ProcessTypes"] = "PhysiologicalRiskPrediction", ["AnomalyTypes"] = new JsonArray("Point Anomaly"), ["UsageTypes"] = "Research"
            }
        });
        data["AIHData"]!["HealthData"]!["HealthDataData"] = new JsonArray(new JsonObject
        {
            ["Data"] = Convert.ToBase64String(Enumerable.Range(0, 2500).SelectMany(i => BitConverter.GetBytes((short)(i % 200 == 0 ? 12000 : 10))).ToArray())     // a beat every 0.8 s
        });
        return data;
    }

    // The End User's upload, as a Front End sends it: registered, the Smart Contract made, and the AIH Data kept by AIH Data Processing with its Smart Contract.
    private JsonObject Uploaded(string id, JsonObject endUserTokens, JsonObject data)
    {
        Write(AihJson.Process, 1, Request("user-1", "Store", endUserTokens, data));
        Write(AihJson.Blockchain, 1, Contract());
        var response = Read(AihJson.Process, 1);
        Assert.Equal("valid", Valid("AIHDataProcess", response));
        var stored = AihJson.ResponseOf(response)!;
        Assert.True(stored["ProcessingStatus"]!.GetValue<bool>());
        var withContract = (JsonObject)stored["AIHDataOrAIHDataID"]![0]!;
        Assert.StartsWith("sc-", withContract["AIHData"]!["SmartContractID"]!.ToString());
        return withContract;
    }

    [SkippableFact]
    public void AnEndUserRegistersUploadsAndTheBackEndKeepsTheAihDataWithItsSmartContract()
    {
        var tokens = Register(AihSamples.RegisterRequest("user-1"), "Store", "AIHDataProcessing");
        Assert.Equal(["Store", "AIHDataProcessing"], AihJson.ServicesOf(tokens));
        var kept = Uploaded("ahd-1", tokens, UploadData("ahd-1"));

        // It is given back by its identifier.
        Write(AihJson.Process, 1, Request("user-1", "Store", tokens, "ahd-1"));
        var back = AihJson.ResponseOf(Read(AihJson.Process, 1))!;
        Assert.True(back["ProcessingStatus"]!.GetValue<bool>());
        Assert.Equal(kept["AIHData"]!["SmartContractID"]!.ToString(), back["AIHDataOrAIHDataID"]![0]!["AIHData"]!["SmartContractID"]!.ToString());
        Assert.True(chain.Verify());
    }

    private static JsonObject DeIdRequest(string user, string id, string type, string algorithm, JsonObject tokens) => new()
    {
        ["Header"] = AihJson.DeId, ["DeIDAndAnonymID"] = AihJson.NewId("dia"), ["DeIDAndAnonymTime"] = AihJson.Time(), ["EndUserID"] = user,
        ["DeIDAndAnonymData"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["AIHDataID"] = id, ["DeIDAndAnonymType"] = type, ["DeIDAndAnonymAlgoID"] = algorithm, ["Tokens"] = tokens.DeepClone() } })
    };

    private static JsonObject AuditRequest(string user, JsonObject tokens) => new()
    {
        ["Header"] = AihJson.Audit, ["UserID"] = user, ["AuditID"] = AihJson.NewId("adt"), ["AuditTime"] = AihJson.Time(),
        ["Audit"] = new JsonObject
        {
            ["Request"] = new JsonObject
            {
                ["Tokens"] = tokens.DeepClone(), ["AuditPeriod"] = AihJson.Time(DateTimeOffset.UtcNow.AddHours(-1)),
                ["ProcessingOperations"] = new JsonArray(new JsonObject { ["ProcessingOperation"] = "PhysiologicalRiskPrediction", ["DataClass"] = "AIH-ECO" })
            }
        }
    };

    // The pseudonym De-Identification gives an End User (a keyed hash of the identifier; the key is the AIM's setting, here its default).
    private static string Pseudonym(string user) => "pseud-" + Convert.ToHexString(System.Security.Cryptography.HMACSHA256.HashData(System.Text.Encoding.UTF8.GetBytes("mpai-aih-test-pseudonym-key"), System.Text.Encoding.UTF8.GetBytes(user)))[..12].ToLowerInvariant();

    // The Back End's User Agent asks AIH Data Processing for an AIH Data, as the Back End, which has no Tokens (or as a user, with theirs): it is given the AIH Data itself, to hand
    // to De-Identification and to Auditing.
    private JsonObject Fetch(string requester, JsonObject tokens, string id)
    {
        Write(AihJson.Process, 1, Request(requester, "Store", tokens, id));
        var response = AihJson.ResponseOf(Read(AihJson.Process, 1))!;
        Assert.True(response["ProcessingStatus"]!.GetValue<bool>(), $"{id} is not given to {requester}");
        return (JsonObject)response["AIHDataOrAIHDataID"]![0]!;
    }

    [SkippableFact]
    public void DeIdentificationMakesACopyThatAuditingFindsLicensedOrNot()
    {
        var licensed = Register(AihSamples.RegisterRequest("user-1"), "Store", "DeIDAndAnonym", "Auditing");
        Uploaded("ahd-1", licensed, UploadData("ahd-1"));                                    // its Back End Licence states Pseudonymised: the Back End may de-identify
        var unlicensed = UploadData("ahd-2", "user-3");
        ((JsonObject)unlicensed["AIHData"]!["ModelLicence"]!["ModelLicenceData"]!["Specific"]![0]!["BackEndLicence"]!)["AIHDataStatus"] = "Identified";
        var user3 = Register(AihSamples.RegisterRequest("user-3"), "Store", "DeIDAndAnonym", "Auditing");
        Uploaded("ahd-2", user3, unlicensed);

        // For each: the request is made, De-Identification is given the AIH Data AIH Data Processing keeps, makes the copy, and the copy is given to AIH Data Processing to keep.
        var copies = new Dictionary<string, JsonObject>();
        foreach (var (id, user, tokens) in new[] { ("ahd-1", "user-1", licensed), ("ahd-2", "user-3", user3) })
        {
            var original = Fetch("hbe-1", new JsonObject(), id);
            Write(AihJson.DeId, 1, DeIdRequest(user, id, "De-Identify", "Hashing", tokens));
            Write(AihJson.AihData, 2, original);                                                // De-Identification gets the AIH Data
            var done = Read(AihJson.DeId, 1);
            Assert.Equal("valid", Valid("DeIDAndAnonym", done));
            Assert.True(done["DeIDAndAnonymData"]![0]!["Response"]!["ProcessingState"]!.GetValue<bool>());
            var copy = Read(AihJson.AihData, 1);                                                // the copy: pseudonym for the End User, the original linked
            Assert.Equal(id + "-deid", copy["AIHDataID"]!.ToString());
            Assert.Equal(id + "-deid", done["DeIDAndAnonymData"]![0]!["Response"]!["NewAIHDataID"]!.ToString());     // and the response says what it is called
            Write(AihJson.AihData, 3, copy);                                                    // AIH Data Processing keeps it, and says so
            Assert.True(AihJson.ResponseOf(Read(AihJson.Process, 1))!["ProcessingStatus"]!.GetValue<bool>());
            copies[id] = copy;
        }
        Assert.Equal("ahd-1-deid", Fetch("hbe-1", new JsonObject(), "ahd-1-deid")["AIHDataID"]!.ToString());      // it is kept

        // The audit of user-1: its copy was licensed. The audit of user-3: its copy was not.
        foreach (var (user, id, expectedUnlicensed, tokens) in new[] { ("user-1", "ahd-1", 0, licensed), ("user-3", "ahd-2", 1, user3) })
        {
            Write(AihJson.AihData, 1, Fetch("hbe-1", new JsonObject(), id));                    // Auditing is given the AIH Data of the End User and its copy...
            Write(AihJson.AihData, 1, copies[id]);
            Write(AihJson.Audit, 1, AuditRequest(user, tokens));                                // ...and then the request
            // Auditing asks one Licence Confirm per copy, to the Blockchain (the boundary Port of Auditing is LCF#1).
            for (var asked = 0; asked < 3; asked++)
            {
                var read = api.OutputRead(Hbe, AihJson.LicenceConfirm, 1, asked == 0 ? 10_000 : 1_000);
                if (read.Error != AifError.OK) break;
                var ask = JsonNode.Parse(read.Json!)!.AsObject();
                var request = ask["LicenceConfirmData"]![0]!["Request"]!;
                var allowed = chain.Confirm(request["AIHDataID"]!.GetValue<string>(), request["ProcessType"]!.GetValue<string>(), request["RequesterID"]!.GetValue<string>());
                Write(AihJson.LicenceConfirm, 1, AihJson.LicenceConfirmResponse(ask["EndUserID"]?.GetValue<string>() ?? "", ask["LicenceConfirmID"]!.GetValue<string>(), allowed));
            }
            var audit = Read(AihJson.Audit, 1);
            var found = audit["Audit"]!["Response"]!["AIHDataIDs"]!.AsArray();
            Assert.Equal(expectedUnlicensed, found.Count);
            if (expectedUnlicensed == 1) Assert.Equal("ahd-2-deid", found[0]!["AIHDataID"]!.ToString());
        }
    }
    [SkippableFact]
    public void AThirdPartyUserIsAnsweredUnderTheLicenceAndRefusedOutsideIt()
    {
        var endUser = Register(AihSamples.RegisterRequest("user-1"), "Store");
        Uploaded("ahd-1", endUser, UploadData("ahd-1"));
        var other = UploadData("ahd-2", "user-2");
        other["AIHData"]!["ModelLicence"]!["ModelLicenceData"]!["Specific"]!.AsArray().RemoveAt(1);   // user-2 licensed no Third-Party User
        var endUser2 = Register(AihSamples.RegisterRequest("user-2"), "Store");
        Write(AihJson.Process, 1, Request("user-2", "Store", endUser2, other));
        Write(AihJson.Blockchain, 1, Contract());
        Read(AihJson.Process, 1);                                                            // kept, with its Smart Contract

        var tpu = Register(AihSamples.ThirdPartyRegisterRequest("tpu-1"), "AIHDataProcessing");
        Assert.Equal(["AIHDataProcessing"], AihJson.ServicesOf(tpu));

        // Inside the licence: the licence is confirmed, AIH Data Processing fetches the AIH Data from the Secure Storage, and the answer carries what Anomaly and Risk Alerting found.
        Write(AihJson.Process, 1, Request("tpu-1", "AIHDataProcessing", tpu, "ahd-1"));
        Write(AihJson.LicenceConfirm, 2, Confirmation(2));
        var answer = AihJson.ResponseOf(Read(AihJson.Process, 1))!;
        Assert.True(answer["ProcessingStatus"]!.GetValue<bool>());
        Assert.Equal("Green", answer["AIHDataOrAIHDataID"]![0]!["AIHData"]!["ARAData"]!["ARAData"]!["Risk"]!.ToString());

        // Outside the licence: the same Third-Party User asks for the AIH Data of the End User who licensed no one.
        Write(AihJson.Process, 1, Request("tpu-1", "AIHDataProcessing", tpu, "ahd-2"));
        Write(AihJson.LicenceConfirm, 2, Confirmation(2));
        var refused = AihJson.ResponseOf(Read(AihJson.Process, 1))!;
        Assert.False(refused["ProcessingStatus"]!.GetValue<bool>());

        Assert.Contains(chain.Ledger, e => e.Kind == "Confirm" && e.Requester == "tpu-1" && e.AihDataId == "ahd-1" && e.Allowed);
        Assert.Contains(chain.Ledger, e => e.Kind == "Confirm" && e.Requester == "tpu-1" && e.AihDataId == "ahd-2" && !e.Allowed);
    }
}
