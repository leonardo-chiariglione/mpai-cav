using System.Text.Json;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;
using Mpai.Aih;

namespace Mpai.Aif.Tests;

// AIH DATA PROCESSING, the AIM of both ends, on its own: the Back End's registers, gives Tokens, has the Smart Contract made, has the licence confirmed and the
// AIH Data checked; the Front End's forwards, and adds what Anomaly and Risk Alerting finds. What it waits for it waits for between messages, as the Module's
// wiring and the User Agent's workflow would give them.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AihHdpTests : IDisposable
{
    private readonly AmdStore store = new(Repository.Amds);
    private readonly string work = Path.Combine(Path.GetTempPath(), "aih-hdp-" + Guid.NewGuid().ToString("N"));
    private static readonly IReadOnlyDictionary<string, Json.Schema.JsonSchema> Schemas = AIF.Metadata.PublishedSchemas.At(Repository.Schemas);

    public AihHdpTests() { store.Scan(); Directory.CreateDirectory(work); }
    public void Dispose() { try { Directory.Delete(work, true); } catch { } }

    private IAimProcessor Hdp(string role) =>
        new AihProvider(store).Create(AihProvider.Hdp, new Dictionary<string, string> { ["Role"] = role }, new FileSharedStorage(work, "aih-test", "hdp"));

    private static readonly AimPortReader Ports = AimPortReader.Load(new AmdStoreOf().Store, AihProvider.Hdp);
    private sealed class AmdStoreOf { public AmdStore Store { get; } = Scan(); private static AmdStore Scan() { var s = new AmdStore(Repository.Amds); s.Scan(); return s; } }

    private string In(string dataType, int n = 1) => Ports.Input(dataType, n);
    private string Out(string dataType, int n = 1) => Ports.Output(dataType, n);

    private static Message With(params (string Key, string Json)[] ports) =>
        new() { MessageId = Guid.NewGuid().ToString("N"), Ports = ports.ToDictionary(p => p.Key, p => p.Json) };

    private static async Task<Message> Send(IAimProcessor aim, Message m)
    {
        var reply = await aim.ProcessAsync(m);
        Assert.False(reply.IsError, reply.Payload);
        return reply;
    }

    private static string Valid(string schema, string json)
    {
        var s = Schemas[Path.GetFullPath(Path.Combine(Repository.Schemas, "AIH1", "V1.1", "data", schema + ".json"))];
        using var doc = JsonDocument.Parse(json);
        lock (AIF.Metadata.PublishedSchemas.Lock) return s.Evaluate(doc.RootElement).IsValid ? "valid" : "not valid";
    }

    private static JsonObject Request(string requester, string type, JsonObject tokens, params JsonNode[] items) => new()
    {
        ["Header"] = "AIH-AHP-V1.1", ["UserID"] = requester, ["AIHDataProcessID"] = "ahp-1", ["AIHDataProcessTime"] = AihSamples.Time("t"),
        ["AIHDataProcess"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = new JsonArray(items.Select(i => i.DeepClone()).ToArray()), ["ProcessingType"] = type, ["Tokens"] = tokens.DeepClone() }
        })
    };

    private static JsonObject Register(JsonObject request, params string[] services)
    {
        var json = request["RegisterData"]![0]!["Request"]!;
        json["RequestedServices"] = new JsonArray(services.Select(s => (JsonNode)new JsonObject { ["Service"] = s }).ToArray());
        return request;
    }

    private async Task<(JsonObject Response, JsonObject Tokens)> Registered(IAimProcessor hdp, JsonObject registerRequest)
    {
        var reply = await Send(hdp, With((In(AihJson.Register, 1), registerRequest.ToJsonString())));
        var json = reply.Ports[Out(AihJson.Register, 2)];
        Assert.Equal("valid", Valid("Register", json));
        var response = JsonNode.Parse(json)!.AsObject();
        return (response, (JsonObject)(response["RegisterData"]![0]!["Response"]!["Tokens"] ?? new JsonObject()));
    }

    [Fact]
    public async Task TheBackEndRegistersAnEndUserAndGivesTokens()
    {
        var hdp = Hdp("BackEnd");
        var (response, tokens) = await Registered(hdp, Register(AihSamples.RegisterRequest("user-1"), "Store", "AIHDataProcessing"));
        Assert.True(response["RegisterData"]![0]!["Response"]!["Success"]!.GetValue<bool>());
        Assert.Equal(["Store", "AIHDataProcessing"], AihJson.ServicesOf(tokens));
        Assert.Equal("valid", Valid("Tokens", tokens.ToJsonString()));
    }

    [Fact]
    public async Task AThirdPartyUserIsGivenOnlyWhatAThirdPartyMayAsk()
    {
        var hdp = Hdp("BackEnd");
        var (response, tokens) = await Registered(hdp, Register(AihSamples.ThirdPartyRegisterRequest("tpu-1"), "AIHDataProcessing", "Store", "Auditing"));
        Assert.True(response["RegisterData"]![0]!["Response"]!["Success"]!.GetValue<bool>());
        Assert.Equal(["AIHDataProcessing"], AihJson.ServicesOf(tokens));
    }

    [Fact]
    public async Task ARegistrationOfNothingThatMayBeGrantedFails()
    {
        var hdp = Hdp("BackEnd");
        var (response, _) = await Registered(hdp, Register(AihSamples.ThirdPartyRegisterRequest("tpu-1"), "Store"));
        Assert.False(response["RegisterData"]![0]!["Response"]!["Success"]!.GetValue<bool>());
    }

    private static JsonObject UploadData(string id = "ahd-1")
    {
        var data = AihSamples.AihData("user-1", id);
        data["AIHData"]!.AsObject().Remove("SmartContractID"); data["AIHData"]!.AsObject().Remove("DeIDAndAnonym");
        return data;
    }

    [Fact]
    public async Task AnUploadHasItsSmartContractMadeBeforeItIsAccepted()
    {
        var hdp = Hdp("BackEnd");
        var (_, tokens) = await Registered(hdp, Register(AihSamples.RegisterRequest("user-1"), "Store"));
        var data = UploadData();

        // The upload: HDP asks the Blockchain, and says nothing yet.
        var asked = await Send(hdp, With((In(AihJson.Process, 2), Request("user-1", "Store", tokens, data).ToJsonString())));
        Assert.True(asked.Ports.ContainsKey(Out(AihJson.Blockchain)));
        Assert.False(asked.Ports.ContainsKey(Out(AihJson.Process, 2)));
        var bcl = asked.Ports[Out(AihJson.Blockchain)];
        Assert.Equal("valid", Valid("BlockchainLicence", bcl));

        // The Blockchain's answer: the AIH Data comes back with the Smart Contract's identifier.
        var answered = await Send(hdp, With((In(AihJson.Blockchain), AihJson.BlockchainResponse("user-1", "ahd-1", "sc-77", true).ToJsonString())));
        var response = answered.Ports[Out(AihJson.Process, 2)];
        Assert.Equal("valid", Valid("AIHDataProcess", response));
        var kept = JsonNode.Parse(response)!["AIHDataProcess"]![0]!["Response"]!["AIHDataOrAIHDataID"]![0]!;
        Assert.Equal("sc-77", kept["AIHData"]!["SmartContractID"]!.ToString());
        Assert.Equal("valid", Valid("AIHData", kept.ToJsonString()));
    }

    [Fact]
    public async Task ARequestWithoutTheTokenIsRefused()
    {
        var hdp = Hdp("BackEnd");
        var (_, tokens) = await Registered(hdp, Register(AihSamples.RegisterRequest("user-1"), "Store"));
        // the service asked is not the one granted
        var reply = await Send(hdp, With((In(AihJson.Process, 2), Request("user-1", "AIHDataProcessing", tokens, "ahd-1").ToJsonString())));
        Assert.False(AihJson.ResponseOf(JsonNode.Parse(reply.Ports[Out(AihJson.Process, 2)])!.AsObject())!["ProcessingStatus"]!.GetValue<bool>());
        // a user who never registered
        var stranger = await Send(hdp, With((In(AihJson.Process, 2), Request("nobody", "Store", tokens, UploadData()).ToJsonString())));
        Assert.False(AihJson.ResponseOf(JsonNode.Parse(stranger.Ports[Out(AihJson.Process, 2)])!.AsObject())!["ProcessingStatus"]!.GetValue<bool>());
    }

    private async Task<(IAimProcessor Hdp, JsonObject Tokens)> ThirdPartyReady()
    {
        var hdp = Hdp("BackEnd");
        var (_, tokens) = await Registered(hdp, Register(AihSamples.ThirdPartyRegisterRequest("tpu-1"), "AIHDataProcessing"));
        return (hdp, tokens);
    }

    [Fact]
    public async Task AThirdPartyUserIsAnsweredWhenTheLicenceConfirmsAndRefusedWhenItDoesNot()
    {
        var ecg = Convert.ToBase64String(new byte[2000]);                                   // nothing to judge: the risk is Blue, which is an answer
        foreach (var confirmed in new[] { true, false })
        {
            var (hdp, tokens) = await ThirdPartyReady();
            // The AIH Data is kept by HDP in the Secure Storage (the copy De-Identification made, or an upload): there is no Storage AIM.
            var data = AihSamples.AihData("user-1", "ahd-3"); data["AIHData"]!["HealthData"]!["HealthDataData"] = new JsonArray(new JsonObject { ["Data"] = ecg });
            await Send(hdp, With((In(AihJson.AihData), data.ToJsonString())));

            // The request: HDP asks for the Licence Confirm.
            var asked = await Send(hdp, With((In(AihJson.Process, 2), Request("tpu-1", "AIHDataProcessing", tokens, "ahd-3").ToJsonString())));
            var lcf = JsonNode.Parse(asked.Ports[Out(AihJson.LicenceConfirm)])!.AsObject();
            Assert.Equal("valid", Valid("LicenceConfirm", lcf.ToJsonString()));
            Assert.Equal("tpu-1", lcf["LicenceConfirmData"]![0]!["Request"]!["RequesterID"]!.ToString());         // the Third-Party User whose licence is asked about
            var confirmId = lcf["LicenceConfirmID"]!.ToString();

            // The licence is confirmed or not; if it is, HDP fetches the AIH Data from the Secure Storage.
            var answered = await Send(hdp, With((In(AihJson.LicenceConfirm), AihJson.LicenceConfirmResponse("user-1", confirmId, confirmed).ToJsonString())));

            if (!confirmed)
            {
                Assert.False(AihJson.ResponseOf(JsonNode.Parse(answered.Ports[Out(AihJson.Process, 2)])!.AsObject())!["ProcessingStatus"]!.GetValue<bool>());
                continue;
            }
            // Confirmed: the AIH Data goes to Anomaly and Risk Alerting (without a previous finding), and what it finds is the answer.
            var toAra = JsonNode.Parse(answered.Ports[Out(AihJson.AihData)])!;
            Assert.Null(toAra["AIHData"]!["ARAData"]);
            var ara = new AihProvider(store).Create(AihProvider.Ara, new Dictionary<string, string>(), null);
            var araPorts = AimPortReader.Load(store, AihProvider.Ara);
            var found = await Send(ara, With((araPorts.Input(AihJson.AihData), toAra.ToJsonString())));
            var done = await Send(hdp, With((In(AihJson.AraData), found.Ports[araPorts.Output(AihJson.AraData)])));
            var response = done.Ports[Out(AihJson.Process, 2)];
            Assert.Equal("valid", Valid("AIHDataProcess", response));
            var result = AihJson.ResponseOf(JsonNode.Parse(response)!.AsObject())!;
            Assert.True(result["ProcessingStatus"]!.GetValue<bool>());
            Assert.Equal("Blue", result["AIHDataOrAIHDataID"]![0]!["AIHData"]!["ARAData"]!["ARAData"]!["Risk"]!.ToString());
        }
    }

    [Fact]
    public async Task TheFrontEndForwardsAndKeepsWhatIsFound()
    {
        var hdp = Hdp("FrontEnd");

        // To the Back End and back to the End User.
        var register = Register(AihSamples.RegisterRequest("user-1"), "Store").ToJsonString();
        Assert.Equal(register, (await Send(hdp, With((In(AihJson.Register, 1), register)))).Ports[Out(AihJson.Register, 1)]);
        var request = Request("user-1", "Store", AihJson.TokensFor("user-1", ["Store"]), UploadData()).ToJsonString();
        Assert.Equal(request, (await Send(hdp, With((In(AihJson.Process, 2), request)))).Ports[Out(AihJson.Process, 1)]);
        var answer = AihJson.ProcessResponse("user-1", ["ahd-1"], true).ToJsonString();
        Assert.Equal(answer, (await Send(hdp, With((In(AihJson.Process, 1), answer)))).Ports[Out(AihJson.Process, 2)]);

        // New AIH Data is kept in the Secure Data Vault and goes to Anomaly and Risk Alerting; what is found is added to it.
        var data = UploadData("ahd-8");
        var ecg = Convert.ToBase64String(Enumerable.Range(0, 2500).SelectMany(i => BitConverter.GetBytes((short)(i % 125 == 0 ? 12000 : 10))).ToArray());   // a beat every half second: 120 a minute
        data["AIHData"]!["HealthData"]!["HealthDataData"] = new JsonArray(new JsonObject { ["Data"] = ecg });
        var out1 = await Send(hdp, With((In(AihJson.AihData), data.ToJsonString())));
        Assert.True(out1.Ports.ContainsKey(Out(AihJson.AihData)));
        var ara = new AihProvider(store).Create(AihProvider.Ara, new Dictionary<string, string>(), null);
        var araPorts = AimPortReader.Load(store, AihProvider.Ara);
        var found = await Send(ara, With((araPorts.Input(AihJson.AihData), out1.Ports[Out(AihJson.AihData)])));
        var out2 = await Send(hdp, With((In(AihJson.AraData), found.Ports[araPorts.Output(AihJson.AraData)])));
        Assert.False(out2.Ports.ContainsKey(Out(AihJson.Process, 2)));                         // the alert is the ARA Data, not a response of this AIM
        Assert.False(out2.Ports.ContainsKey(Out(AihJson.AihData)));                            // nothing goes to a Storage AIM: there is none

        // What is kept has what was found: asked for, it comes back.
        var back = await Send(hdp, With((In(AihJson.Process, 2), Request("user-1", "Store", AihJson.TokensFor("user-1", ["Store"]), "ahd-8").ToJsonString())));
        var kept = AihJson.ResponseOf(JsonNode.Parse(back.Ports[Out(AihJson.Process, 2)])!.AsObject())!;
        Assert.True(kept["ProcessingStatus"]!.GetValue<bool>());
        var stored = kept["AIHDataOrAIHDataID"]![0]!;
        Assert.NotNull(stored["AIHData"]!["ARAData"]);
        Assert.Equal("valid", Valid("AIHData", stored.ToJsonString()));
    }

    // THE STORAGE API, USED BY HDP: the AIH Data it keeps is given back to its End User, to nobody else, and what is not kept is not found. A request to retrieve is answered
    // where it arrives; one that is not is passed on.
    [Fact]
    public async Task TheFrontEndGivesBackTheAihDataOfItsEndUserOnly()
    {
        var hdp = Hdp("FrontEnd");
        await Send(hdp, With((In(AihJson.AihData), UploadData("ahd-7").ToJsonString())));
        var tokens = AihJson.TokensFor("user-1", ["Store"]);

        var mine = await Send(hdp, With((In(AihJson.Process, 2), Request("user-1", "Store", tokens, "ahd-7").ToJsonString())));
        Assert.False(mine.Ports.ContainsKey(Out(AihJson.Process, 1)));                           // answered here, not passed on to the Back End
        var result = AihJson.ResponseOf(JsonNode.Parse(mine.Ports[Out(AihJson.Process, 2)])!.AsObject())!;
        Assert.True(result["ProcessingStatus"]!.GetValue<bool>());
        Assert.Equal("ahd-7", result["AIHDataOrAIHDataID"]![0]!["AIHDataID"]!.ToString());

        foreach (var (user, id) in new[] { ("user-2", "ahd-7"), ("user-1", "ahd-none") })
        {
            var refused = await Send(hdp, With((In(AihJson.Process, 2), Request(user, "Store", AihJson.TokensFor(user, ["Store"]), id).ToJsonString())));
            Assert.False(AihJson.ResponseOf(JsonNode.Parse(refused.Ports[Out(AihJson.Process, 2)])!.AsObject())!["ProcessingStatus"]!.GetValue<bool>());
        }

        // An upload carries the AIH Data: that is for the Back End.
        var upload = Request("user-1", "Store", tokens, UploadData("ahd-9")).ToJsonString();
        Assert.Equal(upload, (await Send(hdp, With((In(AihJson.Process, 2), upload)))).Ports[Out(AihJson.Process, 1)]);
    }

    [Fact]
    public async Task TheBackEndKeepsWhatItUploadsAndGivesItToItsEndUserAndToTheBackEnd()
    {
        var hdp = new AihProvider(store).Create(AihProvider.Hdp, new Dictionary<string, string> { ["Role"] = "BackEnd", ["BackEndId"] = "hbe-1" }, new FileSharedStorage(work, "aih-test", "hdp-be"));
        var (_, tokens) = await Registered(hdp, Register(AihSamples.RegisterRequest("user-1"), "Store"));

        // An upload, its Smart Contract made: kept.
        await Send(hdp, With((In(AihJson.Process, 2), Request("user-1", "Store", tokens, UploadData("ahd-4")).ToJsonString())));
        await Send(hdp, With((In(AihJson.Blockchain), AihJson.BlockchainResponse("user-1", "ahd-4", "sc-4", true).ToJsonString())));

        var asked = await Send(hdp, With((In(AihJson.Process, 2), Request("user-1", "Store", tokens, "ahd-4").ToJsonString())));
        var mine = AihJson.ResponseOf(JsonNode.Parse(asked.Ports[Out(AihJson.Process, 2)])!.AsObject())!;
        Assert.True(mine["ProcessingStatus"]!.GetValue<bool>());
        Assert.Equal("sc-4", mine["AIHDataOrAIHDataID"]![0]!["AIHData"]!["SmartContractID"]!.ToString());

        // The Back End itself is not a user and has no Tokens: it is given what it keeps, for De-Identification and for Auditing.
        var backEnd = await Send(hdp, With((In(AihJson.Process, 2), Request("hbe-1", "Store", new JsonObject(), "ahd-4").ToJsonString())));
        Assert.True(AihJson.ResponseOf(JsonNode.Parse(backEnd.Ports[Out(AihJson.Process, 2)])!.AsObject())!["ProcessingStatus"]!.GetValue<bool>());

        // Another End User, even registered, is not given it.
        var (_, otherTokens) = await Registered(hdp, Register(AihSamples.RegisterRequest("user-2"), "Store"));
        var other = await Send(hdp, With((In(AihJson.Process, 2), Request("user-2", "Store", otherTokens, "ahd-4").ToJsonString())));
        Assert.False(AihJson.ResponseOf(JsonNode.Parse(other.Ports[Out(AihJson.Process, 2)])!.AsObject())!["ProcessingStatus"]!.GetValue<bool>());
    }
}
