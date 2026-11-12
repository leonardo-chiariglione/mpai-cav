using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;
using Mpai.Aih;
using Mpai.Aih.Hfl;

namespace Mpai.Aif.Tests;

// FEDERATED LEARNING, STEP 2 (night N-2): the checks of a round by the Back End. Before averaging: size, finite values, and a limit on how far an update moved from the model the
// round started from. After averaging: the new model is evaluated on a FIXED set and replaces the old one only if the metrics stay within limits; else the old version is kept
// and the refusal is recorded (HflAimProcessor.Refusals).
[Trait("Group", "Fast")]
public class AihFederatedRoundTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "aih-fl-round-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

    private static string Network => Path.Combine(Repository.Root, "Test", "Data", "Fl", "frozen-backbone.onnx");
    private static readonly AimPortReader Ports = Load();
    private static AimPortReader Load() { var s = new AmdStore(Repository.Amds); s.Scan(); return AimPortReader.Load(s, AihProvider.Hfl); }

    private static Message With(string port, JsonObject o) => new() { MessageId = Guid.NewGuid().ToString("N"), Ports = new Dictionary<string, string> { [port] = o.ToJsonString() } };
    private static JsonObject Fdl(JsonObject item) => new()
    {
        ["Header"] = "AIH-FDL-V1.1", ["FederatedLearnID"] = "fdl-" + Guid.NewGuid().ToString("N")[..6], ["FederatedLearnTime"] = AihJson.Time(),
        ["FederatedLearnData"] = new JsonArray(item)
    };
    private static Message Request(string hfe) => With(Ports.Input(AihJson.FederatedLearn), Fdl(new JsonObject { ["Request"] = new JsonObject { ["HFEID"] = hfe, ["PreLearnNNModelID"] = "model-0" } }));
    private static Message Response(JsonObject model) => With(Ports.Input(AihJson.FederatedLearn), Fdl(new JsonObject { ["Response"] = new JsonObject { ["AIHDataID"] = "x", ["PostLearnNNModel"] = model.DeepClone() } }));

    private static async Task<Message> Send(HflAimProcessor hfl, Message m) { var r = await hfl.ProcessAsync(m); Assert.False(r.IsError, r.Payload); return r; }
    private static bool Averaged(Message m) => m.Ports.ContainsKey(Ports.Output("AIP-MLO-V1.0"));

    private static float Distance(float[] a, float[] b) => (float)Math.Sqrt(a.Zip(b, (x, y) => (double)(x - y) * (x - y)).Sum());

    // The three honest updates of the three Front Ends, from the start model.
    private (JsonObject Start, JsonObject[] Updates, FrontEndTrainer Judge) Honest()
    {
        var judge = new FrontEndTrainer(Network, new FileSharedStorage(Path.Combine(folder, "judge"), "j", "j"));
        var start = AihFederatedTrainingTests.StartModel(judge, folder);
        var updates = new List<JsonObject>();
        for (var u = 0; u < 3; u++)
        {
            using var fe = new FrontEndTrainer(Network, new FileSharedStorage(Path.Combine(folder, "fe" + u), "fe" + u, "fe" + u));
            var k = 0; foreach (var (img, f) in AihFederatedTrainingTests.Cases(AihFederatedTrainingTests.EndUsers[u], 100, 100 + u)) fe.KeepCase($"c{k++:D3}", img, f);
            updates.Add(fe.Train(start, epochs: 0));                       // local training of the network and the final layer together, as in the demo
        }
        return (start, updates.ToArray(), judge);
    }

    private static RoundPolicy PolicyFor(FrontEndTrainer judge, float[] startWeights, double? maxDistance)
    {
        var check = AihFederatedTrainingTests.CheckSet();
        return new RoundPolicy
        {
            WeightCount = startWeights.Length, MaxDistance = maxDistance,
            Evaluate = m => judge.Score(m, check)
        };
    }

    private static async Task Requests(HflAimProcessor hfl, int n)
    {
        for (var i = 0; i < n; i++) await Send(hfl, Request("hfe-" + i));
    }

    [Fact]
    public async Task AnAcceptedRoundIsAveragedCheckedAndReplacesTheOldModel()
    {
        var (start, updates, judge) = Honest();
        using var _ = judge;
        var s = HflAimProcessor.Vector(start);                            // all the weights: the final layer and the network
        var limit = updates.Max(u => Distance(HflAimProcessor.Vector(u), s)) * 1.5;
        var hfl = new HflAimProcessor("hfl", Ports) { Policy = PolicyFor(judge, s, limit) };
        hfl.StartFrom(start);
        await Requests(hfl, 3);
        Message last = null!;
        foreach (var u in updates) last = await Send(hfl, Response(u));
        Assert.True(Averaged(last), string.Join("; ", hfl.Refusals));
        Assert.Empty(hfl.Refusals);
        Assert.NotEqual(s, hfl.Current);                                  // the new model replaced the old one
        var mean = HflAimProcessor.Vector(JsonNode.Parse(last.Ports[Ports.Output("AIP-MLO-V1.0")])!.AsObject());
        Assert.Equal(mean, hfl.Current);
    }

    [Fact]
    public async Task AnExtremeUpdateIsRefusedAndTheRestIsAveraged()
    {
        var (start, updates, judge) = Honest();
        using var _ = judge;
        var s = HflAimProcessor.Vector(start);
        var limit = updates.Max(u => Distance(HflAimProcessor.Vector(u), s)) * 1.5;
        var extreme = FrontEndTrainer.ModelObject("evil", FrontEndTrainer.Weights(start).Select(v => v + 50f).ToArray(), "extreme", 1, FrontEndTrainer.NetworkOf(start));
        var wrongSize = FrontEndTrainer.ModelObject("short", new float[5], "wrong size");
        var hfl = new HflAimProcessor("hfl", Ports) { Policy = PolicyFor(judge, s, limit) };
        hfl.StartFrom(start);
        await Requests(hfl, 4);
        await Send(hfl, Response(updates[0])); await Send(hfl, Response(extreme)); await Send(hfl, Response(wrongSize));
        var last = await Send(hfl, Response(updates[1]));
        Assert.True(Averaged(last), string.Join("; ", hfl.Refusals));                                      // the two honest updates were averaged
        Assert.Equal(2, hfl.Refusals.Count);
        Assert.All(hfl.Refusals, r => Assert.Equal("update", r.What));
        Assert.Contains(hfl.Refusals, r => r.Reason.Contains("moved"));
        Assert.Contains(hfl.Refusals, r => r.Reason.Contains("5 values"));
        var mean = HflAimProcessor.Vector(JsonNode.Parse(last.Ports[Ports.Output("AIP-MLO-V1.0")])!.AsObject());
        var expected = HflAimProcessor.Vector(updates[0]).Zip(HflAimProcessor.Vector(updates[1]), (a, b) => (a + b) / 2).ToArray();
        for (var i = 0; i < mean.Length; i++) Assert.Equal(expected[i], mean[i], 4);
    }

    [Fact]
    public async Task AResultThatIsWorseOnTheFixedSetIsRefusedAndTheOldModelIsKept()
    {
        var (start, updates, judge) = Honest();
        using var _ = judge;
        var s = HflAimProcessor.Vector(start);
        // Updates that pass the checks of size and distance (no distance limit here) but together make a model that is worse on the fixed set: the old model's final layer upside down.
        var flipped = FrontEndTrainer.ModelObject("flip", FrontEndTrainer.Weights(start).Select(v => -v).ToArray(), "flipped", 1, FrontEndTrainer.NetworkOf(start));
        var hfl = new HflAimProcessor("hfl", Ports) { Policy = PolicyFor(judge, s, null) };
        hfl.StartFrom(start);
        await Requests(hfl, 2);
        await Send(hfl, Response(flipped));
        var last = await Send(hfl, Response(flipped));
        Assert.False(Averaged(last));                                     // nothing leaves
        Assert.Equal(s, hfl.Current);                                     // the old version is kept
        var refusal = Assert.Single(hfl.Refusals);
        Assert.Equal(("result", 1), (refusal.What, refusal.Round));
        Assert.False(string.IsNullOrWhiteSpace(refusal.Reason));          // the reason is recorded in words
        // the next round can still be accepted
        await Requests(hfl, 3);
        await Send(hfl, Response(updates[0])); await Send(hfl, Response(updates[1]));
        Assert.True(Averaged(await Send(hfl, Response(updates[2]))), string.Join("; ", hfl.Refusals));
    }

    // ---- step 3: the average is weighted by the number of cases ----

    private static async Task<float[]> Mean(params JsonObject[] models)
    {
        var hfl = new HflAimProcessor("hfl", Ports);
        for (var i = 0; i < models.Length; i++) await Send(hfl, Request("hfe-" + i));
        Message last = null!;
        foreach (var m in models) last = await Send(hfl, Response(m));
        Assert.True(Averaged(last));
        return FrontEndTrainer.Weights(JsonNode.Parse(last.Ports[Ports.Output("AIP-MLO-V1.0")])!.AsObject());
    }

    [Fact]
    public async Task AModelOf100CasesCountsMoreThanOneOf5()
    {
        var big = FrontEndTrainer.ModelObject("big", new float[] { 0f, 0f }, "100 cases", cases: 100);
        var small = FrontEndTrainer.ModelObject("small", new float[] { 1f, 2f }, "5 cases", cases: 5);
        var mean = await Mean(big, small);
        Assert.Equal(5f / 105f, mean[0], 5);                              // (100*0 + 5*1) / 105
        Assert.Equal(10f / 105f, mean[1], 5);
        Assert.True(mean[0] < 0.5f);                                      // a plain average would be 0.5
        // models without a number count one case each: the plain average, as before
        var plain = await Mean(FrontEndTrainer.ModelObject("a", new float[] { 0f }, "x"), FrontEndTrainer.ModelObject("b", new float[] { 1f }, "y"));
        Assert.Equal(0.5f, plain[0], 5);
        // one with the number and one without: the one without counts one
        var mixed = await Mean(FrontEndTrainer.ModelObject("a", new float[] { 0f }, "x", cases: 3), FrontEndTrainer.ModelObject("b", new float[] { 1f }, "y"));
        Assert.Equal(0.25f, mixed[0], 5);
    }

    [Fact]
    public async Task ANumberOfCasesThatIsNotAPositiveWholeNumberIsAnError()
    {
        var hfl = new HflAimProcessor("hfl", Ports);
        await Send(hfl, Request("hfe"));
        var bad = FrontEndTrainer.ModelObject("bad", new float[] { 1f }, "x", cases: 1);
        bad["MLModelData"]![1]!["Data"] = "-4";
        var reply = await hfl.ProcessAsync(Response(bad));
        Assert.True(reply.IsError);
        Assert.Contains("number of cases", reply.Payload);
    }

    [Fact]
    public void TheNumberOfCasesIsCarriedInTheWayTheSchemaOfTheModelObjectAllows()
    {
        var s = AIF.Metadata.PublishedSchemas.At(Repository.Schemas)[Path.GetFullPath(Path.Combine(Repository.Schemas, "AIP", "V1.0", "data", "MLModelObject.json"))];
        var model = FrontEndTrainer.ModelObject("m", new float[] { 1f, 2f }, "x", cases: 100);
        using var doc = System.Text.Json.JsonDocument.Parse(model.ToJsonString());
        lock (AIF.Metadata.PublishedSchemas.Lock) Assert.True(s.Evaluate(doc.RootElement).IsValid);
        Assert.Equal(100, HflAimProcessor.Cases(model));
    }

    [Fact]
    public async Task TrainedModelsAreAveragedByTheirNumberOfCases()
    {
        var (start, _, judge) = Honest();
        using var _ = judge;
        var models = new List<JsonObject>();
        foreach (var (u, n) in new[] { (0, 100), (1, 5) })
        {
            using var fe = new FrontEndTrainer(Network, new FileSharedStorage(Path.Combine(folder, "w" + u), "w", "w"));
            var k = 0; foreach (var (img, f) in AihFederatedTrainingTests.Cases(AihFederatedTrainingTests.EndUsers[u], n, 300 + u)) fe.KeepCase($"c{k++:D3}", img, f);
            models.Add(fe.Train(start));
        }
        Assert.Equal(new[] { 100, 5 }, models.Select(HflAimProcessor.Cases));
        var mean = await Mean(models.ToArray());
        var a = FrontEndTrainer.Weights(models[0]); var b = FrontEndTrainer.Weights(models[1]);
        Assert.True(Distance(mean, a) < Distance(mean, b), "the average is nearer the model of 100 cases");
    }

    // ---- step 4: the models of a round are taken without using the sender ----

    private static Message ResponseFrom(JsonObject model, string sender, string place)
    {
        var fdl = Fdl(new JsonObject { ["Response"] = new JsonObject { ["AIHDataID"] = sender + "-data", ["PostLearnNNModel"] = model.DeepClone() } });
        fdl["EndUserID"] = sender; fdl["MInstance"] = sender + "-instance"; fdl["UEnvironment"] = place;
        fdl["FederatedLearnID"] = "fdl-of-" + sender;
        return With(Ports.Input(AihJson.FederatedLearn), fdl);
    }

    private static async Task<string> RoundBy(JsonObject[] models, string[] senders, string place)
    {
        var hfl = new HflAimProcessor("hfl", Ports);
        for (var i = 0; i < models.Length; i++) await Send(hfl, Request(senders[i]));
        Message last = null!;
        for (var i = 0; i < models.Length; i++) last = await Send(hfl, ResponseFrom(models[i], senders[i], place));
        return last.Ports[Ports.Output("AIP-MLO-V1.0")];
    }

    [Fact]
    public async Task ARoundGivesTheSameResultWhateverTheSendersAreCalled()
    {
        var models = new[]
        {
            FrontEndTrainer.ModelObject("m1", new float[] { 0.1f, 0.5f, -1f }, "one", cases: 100),
            FrontEndTrainer.ModelObject("m2", new float[] { 0.4f, -0.5f, 2f }, "two", cases: 30),
            FrontEndTrainer.ModelObject("m3", new float[] { -0.3f, 0.2f, 0f }, "three", cases: 5)
        };
        var a = await RoundBy(models, new[] { "hfe-alice", "hfe-bob", "hfe-carol" }, "hospital-1");
        var b = await RoundBy(models, new[] { "zz-1", "zz-2", "zz-3" }, "somewhere-else");
        var c = await RoundBy(models, new[] { "hfe-bob", "hfe-bob", "hfe-bob" }, "");      // even the same name for all
        Assert.Equal(a, b);
        Assert.Equal(a, c);
        // and with a policy (checks of step 2) the senders are still not used
        var s = new float[3];
        var hfl = new HflAimProcessor("hfl", Ports) { Policy = new RoundPolicy { WeightCount = 3, MaxDistance = 10 } };
        hfl.StartFrom(FrontEndTrainer.ModelObject("start", s, "start"));
        for (var i = 0; i < 3; i++) await Send(hfl, Request("n" + i));
        Message last = null!;
        for (var i = 0; i < 3; i++) last = await Send(hfl, ResponseFrom(models[i], "other-" + i, "elsewhere"));
        Assert.Equal(a, last.Ports[Ports.Output("AIP-MLO-V1.0")]);
    }

    [Fact]
    public void TheCodeOfTheBackEndsRoundNeverReadsWhoSentAModel()
    {
        var code = File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "AIH", "V1.1", "HFL", "HflAimProcessor.cs"));
        // the code is read without its comments, which may name these fields
        var lines = code.Split('\n').Select(l => l.Contains("//") ? l[..l.IndexOf("//", StringComparison.Ordinal)] : l);
        var text = string.Join("\n", lines);
        foreach (var field in new[] { "EndUserID", "MInstance", "UEnvironment", "HFEID", "DataXMData", "FederatedLearnID", "AIHDataID", "Context", "MessageId", "FailedAim", "Signature", "Sender" })
            Assert.DoesNotContain("\"" + field + "\"", text);
        Assert.DoesNotContain(".Context", text);
    }

    // ---- step 6: licence and audit of the NN Models of the rounds ----

    [Fact]
    public async Task TheAuditOfAModelVersionListsItsRoundItsDataAndItsLicence()
    {
        var chain = new Mpai.Aih.Chain.HashChainBlockchain();
        var register = new Mpai.Aih.Chain.ModelVersionRegister(chain, "hbe-1");
        var hfl = new HflAimProcessor("hfl", Ports);
        var models = new[]
        {
            FrontEndTrainer.ModelObject("m1", new float[] { 0.1f, 0.5f }, "one", cases: 100),
            FrontEndTrainer.ModelObject("m2", new float[] { 0.4f, -0.5f }, "two", cases: 30)
        };
        // round 1
        for (var i = 0; i < 2; i++) await Send(hfl, Request("hfe-" + i));
        Message last = null!;
        for (var i = 0; i < 2; i++)
        {
            var m = ResponseFrom(models[i], "u" + i, "x");
            register.NoteResponse(JsonNode.Parse(m.Ports.Values.First())!.AsObject());
            last = await Send(hfl, m);
        }
        Assert.True(Averaged(last));
        var outcome = Assert.Single(hfl.Outcomes);
        Assert.True(outcome.Accepted);
        var v1 = register.Register(outcome.ModelId!, outcome.Round, outcome.StartedFrom, outcome.Updates, outcome.Cases, new[] { "user-1", "user-2" });
        Assert.NotNull(v1);

        // the Blockchain answers who may use the version
        Assert.True(register.MayUse(outcome.ModelId!, "user-1"));
        Assert.True(register.MayUse(outcome.ModelId!, "hbe-1"));            // the owner
        Assert.False(register.MayUse(outcome.ModelId!, "user-3"));          // not named
        Assert.False(register.MayUse("no-such-version", "user-1"));

        // the audit: only the owner may ask; it lists the round, the data, the licence and what the ledger holds about the version
        Assert.Null(register.Audit(outcome.ModelId!, "user-1"));
        var audit = register.Audit(outcome.ModelId!, "hbe-1");
        Assert.NotNull(audit);
        Assert.Equal(outcome.ModelId, audit!["ModelId"]!.GetValue<string>());
        Assert.Equal(1, audit["Round"]!.GetValue<int>());
        Assert.Equal("model-0", audit["StartedFrom"]!.GetValue<string>());
        Assert.Equal(130, audit["Cases"]!.GetValue<int>());
        Assert.Equal(new[] { "u0-data", "u1-data" }, audit["DataIds"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.Equal(new[] { "user-1", "user-2" }, audit["Licence"]!["MayUse"]!.AsArray().Select(d => d!.GetValue<string>()));
        Assert.StartsWith("sc-", audit["Licence"]!["SmartContractID"]!.GetValue<string>());
        var kinds = audit["Ledger"]!.AsArray().Select(e => e!["Kind"]!.GetValue<string>() + ":" + e["Requester"]!.GetValue<string>() + ":" + e["Allowed"]!.GetValue<bool>()).ToList();
        Assert.Contains("Contract:hbe-1:True", kinds);
        Assert.Contains("Confirm:user-3:False", kinds);
        Assert.True(chain.Verify());

        // a licence change is a new Smart Contract: user-2 no longer, user-3 now
        Assert.True(register.ChangeLicence(outcome.ModelId!, new[] { "user-1", "user-3" }));
        Assert.False(register.MayUse(outcome.ModelId!, "user-2"));
        Assert.True(register.MayUse(outcome.ModelId!, "user-3"));
    }
}
