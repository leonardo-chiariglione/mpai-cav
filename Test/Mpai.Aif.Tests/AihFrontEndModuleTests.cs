using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Aih;

namespace Mpai.Aif.Tests;

// THE HEALTH FRONT END AS A MODULE under a Controller, the two apart (the Module's AIMs execute in a process of their own, as always): Model Licensing, HDP, Anomaly and Risk Alerting and Storage wired as the L3 says. The End User's User Agent
// gives it the Health Data it acquired and the Model Licence, reads the boundary Ports, and carries the Module's requests to the Back End.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AihFrontEndModuleTests : IDisposable
{
    private const string Hfe = "1AIH-HFE-V1.1-I01";
    private readonly string work = Path.Combine(Path.GetTempPath(), "aih-hfe-" + Guid.NewGuid().ToString("N"));
    private readonly AihApart apart;
    private ControllerApi api => apart.Api;
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> Schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);

    public AihFrontEndModuleTests()
    {
        Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json");
        File.WriteAllText(settings, new JsonObject { ["1AIH-HDP-V1.1-I01"] = new JsonObject { ["Role"] = "FrontEnd" } }.ToJsonString());
        apart = AihApart.Start(work, settings, Hfe, AihApart.FrontEndAims);
        Assert.Equal(AifError.OK, api.StartFlow(Hfe));
        Assert.Equal(AifError.OK, api.SharedStorageInit(Hfe, Path.Combine(work, "secure-data-vault")));
    }

    public void Dispose() { apart.Dispose(); try { Directory.Delete(work, true); } catch { } }

    private void Write(string type, int n, JsonNode json) => Assert.Equal(AifError.OK, api.InputWrite(Hfe, type, n, json.ToJsonString(), 10_000));

    private JsonObject Read(string type, int n, int ms = 15_000)
    {
        var read = api.OutputRead(Hfe, type, n, ms);
        Assert.True(read.Error == AifError.OK, $"nothing at {type}#{n}: {read.Error}");
        return JsonNode.Parse(read.Json!)!.AsObject();
    }

    private static string Valid(string schema, JsonNode json)
    {
        var s = Schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "AIH1", "V1.1", "data", schema + ".json"))];
        using var doc = JsonDocument.Parse(json.ToJsonString());
        lock (AIF.Metadata.PublishedSchemas.Lock) return s.Evaluate(doc.RootElement).IsValid ? "valid" : "not valid";
    }

    // Health Data of an electrocardiogram with a beat every `period` samples at 250 Hz.
    private static JsonObject HealthData(int period)
    {
        var health = AihSamples.HealthData();
        health["HealthDataData"] = new JsonArray(new JsonObject
        {
            ["Data"] = Convert.ToBase64String(Enumerable.Range(0, 2500).SelectMany(i => BitConverter.GetBytes((short)(i % period == 0 ? 12000 : 10))).ToArray())
        });
        return health;
    }

    [Fact]
    public void RegistrationAndRequestsGoToTheBackEndAndItsAnswersComeBack()
    {
        var register = AihSamples.RegisterRequest("user-1");
        Write(AihJson.Register, 1, register);
        Assert.Equal(register.ToJsonString(), Read(AihJson.Register, 1).ToJsonString());               // to the Back End (NB)

        // The Back End's answer goes in, and the Front End gives it out to the End User's User Agent, on its Output Port 2.
        var response = AihSamples.RegisterResponse("user-1");
        Write(AihJson.Register, 2, response);
        Assert.Equal(response.ToJsonString(), Read(AihJson.Register, 2).ToJsonString());

        // De-identification, anonymisation and audit the same way (2026-11-13): the request goes to the Back End on Output 1, the Back End's answer comes in on Input 2 and goes out on Output 2.
        var deId = DeIdRequest("user-1", "d1");
        Write(AihJson.DeId, 1, deId);
        Assert.Equal(deId.ToJsonString(), Read(AihJson.DeId, 1).ToJsonString());
        var deIdAnswer = new JsonObject
        {
            ["Header"] = AihJson.DeId, ["DeIDAndAnonymID"] = "d1", ["DeIDAndAnonymTime"] = AihJson.Time(), ["EndUserID"] = "user-1",
            ["DeIDAndAnonymData"] = new JsonArray(new JsonObject { ["Response"] = new JsonObject { ["AIHDataID"] = "ahd-1", ["ProcessingState"] = true, ["NewAIHDataID"] = "ahd-1-deid" } })
        };
        Write(AihJson.DeId, 2, deIdAnswer);
        Assert.Equal(deIdAnswer.ToJsonString(), Read(AihJson.DeId, 2).ToJsonString());
        var audit = AuditRequest("user-1", "a1");
        Write(AihJson.Audit, 1, audit);
        Assert.Equal(audit.ToJsonString(), Read(AihJson.Audit, 1).ToJsonString());
        var auditAnswer = new JsonObject
        {
            ["Header"] = AihJson.Audit, ["UserID"] = "user-1", ["AuditID"] = "a1", ["AuditTime"] = AihJson.Time(),
            ["Audit"] = new JsonObject { ["Response"] = new JsonObject { ["ProcessingState"] = true, ["AIHDataIDs"] = new JsonArray() } }
        };
        Write(AihJson.Audit, 2, auditAnswer);
        Assert.Equal(auditAnswer.ToJsonString(), Read(AihJson.Audit, 2).ToJsonString());
    }

    private static JsonObject DeIdRequest(string user, string id) => new()
    {
        ["Header"] = AihJson.DeId, ["DeIDAndAnonymID"] = id, ["DeIDAndAnonymTime"] = AihJson.Time(), ["EndUserID"] = user,
        ["DeIDAndAnonymData"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["AIHDataID"] = "ahd-1", ["DeIDAndAnonymType"] = "De-Identify", ["DeIDAndAnonymAlgoID"] = "Hashing", ["Tokens"] = AihSamples.Tokens() } })
    };

    private static JsonObject AuditRequest(string user, string id) => new()
    {
        ["Header"] = AihJson.Audit, ["UserID"] = user, ["AuditID"] = id, ["AuditTime"] = AihJson.Time(),
        ["Audit"] = new JsonObject { ["Request"] = new JsonObject { ["AuditPeriod"] = AihJson.Time(), ["ProcessingOperations"] = new JsonArray(new JsonObject { ["ProcessingOperation"] = "PhysiologicalRiskPrediction" }), ["Tokens"] = AihSamples.Tokens() } }
    };

    [Fact]
    public void HealthDataAndALicenceMakeAihDataThatIsCheckedKeptAndRaisesTheAlert()
    {
        // A beat every half second: 120 a minute, which is a risk (Yellow).
        Write(AihJson.HealthData, 1, HealthData(125));
        Write(AihJson.ModelLicence, 1, AihSamples.ModelLicence("user-1", "ahd-1"));

        // The alert is the ARA Data the Front End gives at its boundary: what Anomaly and Risk Alerting found.
        var ara = Read(AihJson.AraData, 1);
        Assert.Equal("valid", Valid("ARAData", ara));
        Assert.Equal("Yellow", ara["ARAData"]!["Risk"]!.ToString());
        Assert.Equal(AifError.NotProduced, api.OutputRead(Hfe, AihJson.Process, 2, 500).Error);          // and no response of AIH Data Processing carries an alert

        // It is kept in the End User's Secure Data Vault by AIH Data Processing - there is no Storage AIM - and, asked for by identifier, it is given back.
        var back = Kept("ahd-1");
        Assert.NotNull(back);
        Assert.Equal("Yellow", back!["AIHData"]!["ARAData"]!["ARAData"]!["Risk"]!.ToString());
    }

    // What the End User asks of the Front End: the AIH Data of its Vault, by identifier (null if it is not there).
    private JsonObject? Kept(string id)
    {
        var ask = new JsonObject
        {
            ["Header"] = "AIH-AHP-V1.1", ["UserID"] = "user-1", ["AIHDataProcessID"] = "r", ["AIHDataProcessTime"] = AihJson.Time(),
            ["AIHDataProcess"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = new JsonArray(id), ["ProcessingType"] = "Store", ["Tokens"] = AihSamples.Tokens() } })
        };
        Write(AihJson.Process, 1, ask);
        var response = AihJson.ResponseOf(Read(AihJson.Process, 2))!;
        return response["AIHDataOrAIHDataID"]![0] as JsonObject;
    }

    // THE FRONT END DOES WHAT ITS USER PROFILE ALLOWS. Registered with a User Profile of an individual who is a member of an organisation - a Third-Party User - it makes and keeps no AIH Data,
    // and a request to store or to de-identify is answered here, refused: it goes nowhere.
    [Fact]
    public void ForAThirdPartyUserTheFrontEndKeepsNoAihDataAndRefusesWhatIsNotProcessing()
    {
        var register = AihSamples.RegisterRequest("ada");
        register["RegisterData"]![0]!["Request"]!["UserProfile"]!["UserProfileData"]![0]!["Human"]!["Membership"] = new JsonArray(new JsonObject { ["CompanyName"] = "Cardio Research Ltd" });
        Write(AihJson.Register, 1, register);
        Read(AihJson.Register, 1);                                                                      // to the Back End

        Write(AihJson.HealthData, 1, HealthData(125));
        Write(AihJson.ModelLicence, 1, AihSamples.ModelLicence("user-1", "ahd-1"));
        Assert.Equal(AifError.NotProduced, api.OutputRead(Hfe, AihJson.AraData, 1, 1500).Error);          // no AIH Data was made: nothing to judge, no alert

        var store = new JsonObject
        {
            ["Header"] = "AIH-AHP-V1.1", ["UserID"] = "ada", ["AIHDataProcessID"] = "r1", ["AIHDataProcessTime"] = AihJson.Time(),
            ["AIHDataProcess"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = new JsonArray("ahd-9"), ["ProcessingType"] = "Store", ["Tokens"] = AihSamples.Tokens() } })
        };
        Write(AihJson.Process, 1, store);
        Assert.False(AihJson.ResponseOf(Read(AihJson.Process, 2))!["ProcessingStatus"]!.GetValue<bool>());   // refused here
        Assert.Equal(AifError.NotProduced, api.OutputRead(Hfe, AihJson.Process, 1, 500).Error);            // and not sent to the Back End

        // A request to de-identify, or to audit, likewise: refused here, with the identifier of the request, and not sent to the Back End.
        Write(AihJson.DeId, 1, DeIdRequest("ada", "d9"));
        var deId = Read(AihJson.DeId, 2);
        Assert.Equal("d9", deId["DeIDAndAnonymID"]!.ToString());
        Assert.False(deId["DeIDAndAnonymData"]![0]!["Response"]!["ProcessingState"]!.GetValue<bool>());
        Assert.Equal(AifError.NotProduced, api.OutputRead(Hfe, AihJson.DeId, 1, 500).Error);
        Write(AihJson.Audit, 1, AuditRequest("ada", "a9"));
        var audit = Read(AihJson.Audit, 2);
        Assert.Equal("a9", audit["AuditID"]!.ToString());
        Assert.False(audit["Audit"]!["Response"]!["ProcessingState"]!.GetValue<bool>());
        Assert.Equal(AifError.NotProduced, api.OutputRead(Hfe, AihJson.Audit, 1, 500).Error);
    }

    [Fact]
    public void ARegularRateIsGreen()
    {
        Write(AihJson.HealthData, 1, HealthData(190));                                                  // 79 a minute
        Write(AihJson.ModelLicence, 1, AihSamples.ModelLicence("user-1", "ahd-2"));
        Assert.Equal("Green", Read(AihJson.AraData, 1)["ARAData"]!["Risk"]!.ToString());                  // a regular rate: the ARA Data says there is no risk
        Assert.NotNull(Kept("ahd-2"));                                                                   // but it is kept
    }
}
