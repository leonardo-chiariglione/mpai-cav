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

        // The Back End's answer goes in; the Front End has no Port to give it on (the End User's User Agent has it from the Back End).
        Write(AihJson.Register, 2, AihSamples.RegisterResponse("user-1"));
        Assert.Equal(AifError.NoSuchPort, api.OutputRead(Hfe, AihJson.Register, 2, 500).Error);
    }

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

    [Fact]
    public void ARegularRateIsGreen()
    {
        Write(AihJson.HealthData, 1, HealthData(190));                                                  // 79 a minute
        Write(AihJson.ModelLicence, 1, AihSamples.ModelLicence("user-1", "ahd-2"));
        Assert.Equal("Green", Read(AihJson.AraData, 1)["ARAData"]!["Risk"]!.ToString());                  // a regular rate: the ARA Data says there is no risk
        Assert.NotNull(Kept("ahd-2"));                                                                   // but it is kept
    }
}
