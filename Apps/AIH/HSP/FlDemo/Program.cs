using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;
using AIF.Trust;
using Mpai.Aih;
using Mpai.Aih.Hsp;
using Mpai.Aih.Hfl;
using Mpai.Aih.Nnm;
using Mpai.Nnw.NoConstraint;

namespace Mpai.Aih.FlDemo;

// AIH-HSP, FEDERATED LEARNING, NARRATED. The Back End watermarks its model and uploads an AIM with it to the Store; three End Users download the same AIM, train the
// network and the final layer on their own images (which stay), and return the model; the Back End checks with its watermark that each trained AIM was made from its
// model, averages those (network and final layer) weighted by the number of cases, checks the result on a fixed set, and uploads it - with the mark
// again - for the next round. It ends with the table of known problems. A trained NN Model has an identifier and no licence: nothing is registered for it.
//
// WHAT IS REAL AND WHAT IS NOT is said on the screen and in the table at the end. In short: the images are SYNTHETIC, the network is small and its starting weights are
// random (the Back End trains it first), the Controllers and the Module of the Front Ends and of the Back End are NOT started here (the components are called directly),
// and the sender of a model is simulated (the Back End's code never reads it).
//
// THE SECURE MODE (--secure). Each party - the Back End and the three End Users' Front Ends - has a root of trust of its own (a simulated TPM 2.0 in WSL, ports 2501,
// 2511, 2521, 2531) and a Security API (AIF V3.0), and uses it (Mpai.Aih.Hsp.FlSecure): the End User's vault is sealed (AES-256-GCM under a key of the vault's Owner);
// the Back End's watermark key is a persistent key of its Security API; each Front End signs the model it returns (ECDSA P-256, a persistent key of its own) and
// gives its Attestation token over a challenge of the Back End, which accepts an update only where the token proves approved code and the signature verifies - the
// simulated sender goes away. On the screen: in round 1 a copy of an update changed on the way, refused (signature); in round 2 a Front End started with altered code,
// whose vault does not open and whose update is refused (token). Still NOT here: the Controllers and the Modules, real images.
internal static class Program
{
    private static readonly string Root = FindRoot();
    private static readonly string Work = Path.Combine(Path.GetTempPath(), "aih-fl-demo-" + Guid.NewGuid().ToString("N"));
    private const int Bits = 16;
    // Chosen by trials on this demo (2026-11-12): 16 values a bit (256 of the 5,760 weights the mark may use; 8 left too little margin, 64 cost 13 points of accuracy);
    // the End Users train the network and the final layer together for 5 passes over their cases (no separate refit of the final layer: the final layers of different
    // End Users then drift apart and their average is poor); the Back End's first model is trained for 40 passes.
    private const int Rounds = 4, FirstModelEpochs = 40, ValuesPerBit = 16, FrontEndEpochs = 5;
    private const double MaxDistance = 2;                                        // a trained AIM moves 0.3 to 0.5 from the published model in this demo

    private static bool Secure;
    private static readonly List<IDisposable> Cleanup = new();
    private const int BackEndTpm = 2501;
    private static readonly int[] FrontEndTpms = { 2511, 2521, 2531 };
    private const string FrontEndOwner = "Front End", VaultOwner = "End User's vault", BackEndOwner = "Back End";

    private static void Say(string text) => Console.WriteLine(text);
    private static void Step(string text) { Console.WriteLine(); Console.WriteLine("== " + text); }

    private static async Task<int> Main(string[] args)
    {
        Secure = args.Contains("--secure");
        Say("AIH-HSP V1.1 - federated learning, narrated" + (Secure ? " - SECURE MODE (a root of trust and a Security API for each party)" : ""));
        Say("THIS IS A SIMULATION OF THE PLATFORM: synthetic chest-like images, a small network trained from random weights, components called directly (no Controller, no AIM host),");
        Say("the Store is the test one. What is real and what is not: see the table at the end.");
        try { await Run(); Say(""); Say("Done."); return 0; }
        catch (Exception ex) { Console.WriteLine(); Console.WriteLine("FAILED: " + ex.Message); return 1; }
        finally
        {
            for (var i = Cleanup.Count - 1; i >= 0; i--) try { Cleanup[i].Dispose(); } catch { }
            try { Directory.Delete(Work, true); } catch { }
        }
    }

    // The End User's storage: in the Secure mode sealed by the party's Security API.
    private static ISharedStorage Vault(string folder, string owner, FlParty? party)
    {
        var plain = new FileSharedStorage(folder, owner, owner);
        return party is null ? plain : new SealedSharedStorage(plain, party.For(VaultOwner));
    }

    // The Back End's watermark Key (NNW-KEY-V2.0) made from the value of its key in the Security API: the same conversion as NnwJson.NewKey (8 bytes -> one real).
    private static JsonObject KeyFrom(string id, byte[] bytes)
    {
        var count = bytes.Length / 8;
        var key = NnwJson.NewKey(id, count);
        key["KeyData"] = new JsonArray(Enumerable.Range(0, count).Select(i => (JsonNode)((double)BitConverter.ToUInt64(bytes, i * 8) / ulong.MaxValue)).ToArray());
        return key;
    }

    // THE MODEL OF THE AIM: data of Access (since 2026-11-13, A7), named in the AccessSources of its L3 by the URI where the Back End keeps it and its SHA-256; as a
    // Controller does at Module start, it is obtained there and its SHA-256 checked.
    private static JsonObject ModelOf(string l3File)
    {
        var l3 = JsonNode.Parse(File.ReadAllText(l3File))!.AsObject();
        var entry = (l3["AccessSources"] as JsonArray)?.OfType<JsonObject>().FirstOrDefault(e => (string?)e["Name"] == "nn-model")
                    ?? throw new InvalidDataException($"{Path.GetFileName(l3File)} names no nn-model in its AccessSources.");
        var bytes = File.ReadAllBytes(new Uri((string)entry["Uri"]!).LocalPath);
        if (!string.Equals(Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)), (string?)entry["Sha256"], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"NOT_TRUSTED: the model of {Path.GetFileName(l3File)} is not the one its L3 names (SHA-256).");
        return JsonNode.Parse(bytes)!.AsObject();
    }

    private sealed record Update(string From, string Request, JsonObject Model, byte[]? Token, byte[]? Signature, string Note);
    private static byte[] Bytes(JsonObject model) => Encoding.UTF8.GetBytes(model.ToJsonString());

    // ---- synthetic images: a body, two darker lungs, noise, and (finding) a bright blob; each End User's device has its own look ----
    private sealed record Device(string Name, float Offset, float Noise, float Contrast, float Cx, float Cy, float Spread);
    private static readonly Device Neutral = new("neutral", 0f, 0.05f, 1f, 32f, 32f, 14f);
    private static readonly Device[] EndUsers =
    {
        new("Alice", +0.03f, 0.05f, 1.0f, 20f, 30f, 4f), new("Bruno", -0.03f, 0.08f, 1.0f, 44f, 30f, 4f), new("Chiara", 0f, 0.05f, 0.8f, 32f, 46f, 5f)
    };
    private static float Gauss(Random r) => (float)(Math.Sqrt(-2 * Math.Log(1 - r.NextDouble())) * Math.Cos(2 * Math.PI * r.NextDouble()));
    private static float[] Image(Device d, bool finding, Random rng)
    {
        const int n = FrontEndTrainer.ImageSide;
        var img = new float[n * n]; float bx = 0, by = 0;
        if (finding) { bx = Math.Clamp(d.Cx + Gauss(rng) * d.Spread, 12, 52); by = Math.Clamp(d.Cy + Gauss(rng) * d.Spread, 14, 52); }
        for (var y = 0; y < n; y++)
            for (var x = 0; x < n; x++)
            {
                var v = 0.6f - 0.05f * (y / (float)n);
                foreach (var lx in new[] { 20f, 44f }) if (Math.Pow((x - lx) / 11.0, 2) + Math.Pow((y - 32) / 20.0, 2) < 1) v -= 0.25f;
                if (finding) v += 0.35f * (float)Math.Exp(-((x - bx) * (x - bx) + (y - by) * (y - by)) / (2 * 3.5 * 3.5));
                img[y * n + x] = Math.Clamp(0.5f + (v - 0.5f) * d.Contrast + d.Offset + Gauss(rng) * d.Noise, 0f, 1f);
            }
        return img;
    }
    private static List<(float[] Image, bool Finding)> Cases(Device d, int count, int seed)
    {
        var rng = new Random(seed);
        return Enumerable.Range(0, count).Select(i => { var f = i % 2 == 0; return (Image(d, f, rng), f); }).ToList();
    }

    private static async Task Run()
    {
        Directory.CreateDirectory(Work);
        var networkPath = Path.Combine(Root, "Test", "Data", "Fl", "frozen-backbone.onnx");
        if (!File.Exists(networkPath)) throw new FileNotFoundException("the starting network is missing; make it with Test\\Tools\\FlLab\\make_frozen_backbone.py: " + networkPath);
        var checkSet = new[] { Neutral }.Concat(EndUsers).SelectMany((d, i) => Cases(d, 50, 9000 + i)).ToList();

        var amds = new AmdStore(Path.Combine(Root, "AIMs", "AMDs")); amds.Scan();
        var nnw = new NnwProvider(amds);
        var wex = nnw.Create(NnwProvider.Wex, new Dictionary<string, string>(), null); var wexPorts = AimPortReader.Load(amds, NnwProvider.Wex);
        var hfl = new HflAimProcessor("hfl", AimPortReader.Load(amds, AihProvider.Hfl));
        var ports = AimPortReader.Load(amds, AihProvider.Hfl);
        var conformance = new AIF.Metadata.L2Conformance(Path.Combine(Root, "schemas"), _ => null);
        var store = new ModelAimPublisher(Path.Combine(Work, "store"), Path.Combine(Work, "back-end"), l3 => conformance.Check(l3));
        using var judge = new FrontEndTrainer(networkPath, new FileSharedStorage(Path.Combine(Work, "judge"), "j", "j"));
        string Acc(JsonObject m) => $"{judge.Score(m, checkSet).Accuracy:P0}";

        // THE WATERMARK (NoConstraint, IMT; M3304). It has ONE use: the Back End is sure that a trained AIM it receives was made from ITS model. So it is the same in every round
        // and for every End User: one key, kept by the Back End (the Locator that names the method carries a KeyReference, never the key), and one payload. It is inserted in DWI
        // mode (During-Training Watermark Insertion): in the network before the Back End trains it, the marked weights kept as the mark set them while it trains - the network learns
        // around the mark. The Front Ends check no watermark; the Back End checks it with Watermark Extraction (NNW-WEX V2.0) on every trained AIM it receives.
        // THE SECURE MODE: the parties start their roots of trust and Security APIs; the Back End enrols the three Front Ends (the identity key of each one's TPM and
        // its public signing key). The enrolment is done directly here.
        FlParty? backEnd = null; var frontEnds = new FlParty[EndUsers.Length]; FlUpdateGate? gate = null;
        if (Secure)
        {
            Step("The Secure Mode: each party starts its root of trust (a simulated TPM 2.0) and its Security API; the Back End enrols the Front Ends");
            var manufacturer = SimulatedManufacturer.Make("the FL demo's TPM manufacturer");
            var code = AppContext.BaseDirectory;
            backEnd = new FlParty("health-back-end", Path.Combine(Work, "party-back-end"), BackEndTpm, manufacturer, code); Cleanup.Add(backEnd);
            Say($"   Back End: TPM at port {BackEndTpm}, its code measured ({FlCode.Of(code).Count} files), its Security API opened: {backEnd.Security.Opened}");
            gate = new FlUpdateGate(backEnd.For(BackEndOwner), FlCode.Policy(manufacturer, FlCode.Reference(code)));
            for (var u = 0; u < EndUsers.Length; u++)
            {
                frontEnds[u] = new FlParty("front-end-" + EndUsers[u].Name, Path.Combine(Work, "party-" + EndUsers[u].Name), FrontEndTpms[u], manufacturer, code); Cleanup.Add(frontEnds[u]);
                gate.Enrol(EndUsers[u].Name, frontEnds[u].IdentityKey, FlFrontEndSecurity.PublicKey(frontEnds[u].For(FrontEndOwner)));
                Say($"   {EndUsers[u].Name}'s Front End: TPM at port {FrontEndTpms[u]}, Security API opened: {frontEnds[u].Security.Opened}; enrolled by the Back End (its TPM's identity key and its signing key, ECDSA P-256)");
            }
        }

        var key = Secure ? KeyFrom("key-backend", FlWatermarkKey.Material(backEnd!.For(BackEndOwner))) : NnwJson.NewKey("key-backend"); const string payload = "1010010111000011";
        var locator = NnwJson.LocatorObject("wlc-backend", Bits, "key:backend", valuesPerBit: ValuesPerBit);
        var markSeed = NnwJson.Seed(key); var markBits = NoConstraint.Bits(payload);

        // The Back End's first model: the network (its starting weights are random) and the final layer, trained together on neutral cases, with the mark.
        Step("The Back End makes its first model: the network and the final layer trained together on 200 neutral cases, the watermark inserted during the training (DWI)");
        JsonObject current;
        using (var seed = new FrontEndTrainer(networkPath, Vault(Path.Combine(Work, "seed"), "be", backEnd)))
        {
            var k = 0; foreach (var (img, f) in Cases(Neutral, 200, 1)) seed.KeepCase("n" + k++, img, f);
            current = seed.Train(null, epochs: 80, networkEpochs: FirstModelEpochs, mark: onnx => NoConstraint.InsertOnnx(onnx, markSeed, markBits, null, ValuesPerBit));
            current["MLModelObjectID"] = "model-0";
        }
        Say($"   watermark: {NoConstraint.MethodName(Bits, ValuesPerBit)}; {Bits * ValuesPerBit} weights of the network set by the mark and kept while the network was trained; the key stays with the Back End");
        if (Secure) Say("   the watermark key is a persistent key of the Back End's Security API (sealed to its code); its value goes only into memory, to the Key at the Input Port of WEX");
        Say($"   accuracy on the fixed check set of 200 cases: {Acc(current)}");

        var refusalsSeen = 0;
        for (var round = 1; round <= Rounds; round++)
        {
            Step($"ROUND {round}: the Back End uploads an AIM with its model to the Store");
            var published = current.DeepClone().AsObject();
            published["MLModelObjectID"] = "model-" + (round - 1);
            var mine = await wex.ProcessAsync(new Message { MessageId = "wex-pub-" + round, Ports = new Dictionary<string, string> {
                [wexPorts.Input(NnwJson.Model)] = published.ToJsonString(), [wexPorts.Input(NnwJson.Key)] = key.ToJsonString(), [wexPorts.Input(NnwJson.Locator)] = locator.ToJsonString() } });
            Say($"   the model it publishes: {JsonNode.Parse(mine.Payload)?["DescrMetadata"]} (no mark is put on it: it carries the one inserted during training)");
            var aim = store.Upload(published);
            Say($"   the Store validated and published the AIM {aim}: every End User downloads this same AIM (BUILT; the Store is the test one)");

            Step("Three End Users download the AIM and train the network and the final layer on their own images (which stay)");
            var returned = new List<Update>();
            for (var u = 0; u < EndUsers.Length; u++)
            {
                var held = Path.Combine(Work, $"controller-{EndUsers[u].Name}-{round}");
                var repository = new AimRepository(held);
                repository.Obtain(aim, new StoreAimSource(Path.Combine(Work, "store"), "hfe-" + EndUsers[u].Name));
                var got = ModelOf(Path.Combine(held, aim + ".json"));
                using var fe = new FrontEndTrainer(networkPath, Vault(Path.Combine(Work, $"vault-{EndUsers[u].Name}"), EndUsers[u].Name, Secure ? frontEnds[u] : null));
                // the End User's own past cases: 100 at the start, and 50 more seen since the last round
                if (fe.Cases == 0) { var k = 0; foreach (var (img, f) in Cases(EndUsers[u], 100, 100 + u)) fe.KeepCase($"case-0-{k++:D3}", img, f); }
                else { var k = 0; foreach (var (img, f) in Cases(EndUsers[u], 50, 1000 * round + u)) fe.KeepCase($"case-{round}-{k++:D3}", img, f); }
                var own = Cases(EndUsers[u], 200, 500 + u + 10 * round);
                var before = fe.Score(got, own);
                var trained = fe.Train(got, epochs: 0, networkEpochs: FrontEndEpochs);
                var after = fe.Score(trained, own);
                Say($"   {EndUsers[u].Name}: {fe.Cases} own images kept in the End User's storage; accuracy on later own cases {before.Accuracy:P0} -> {after.Accuracy:P0}; model version v{fe.Version} kept");
                var model = fe.Export();
                if (!Secure) { returned.Add(new Update(EndUsers[u].Name, "", model, null, null, "")); continue; }
                // the Back End asks for the update with a challenge; the Front End signs its model and answers the challenge with its token
                var request = $"round-{round}-{EndUsers[u].Name}";
                var challenge = gate!.Challenge(EndUsers[u].Name, request);
                var signature = FlFrontEndSecurity.Sign(frontEnds[u].For(FrontEndOwner), Bytes(model));
                returned.Add(new Update(EndUsers[u].Name, request, model, FlFrontEndSecurity.Token(frontEnds[u].For(FrontEndOwner), challenge), signature, ""));
                Say($"      signed by {EndUsers[u].Name}'s Front End (ECDSA P-256, {signature?.Length ?? 0} bytes) and sent with its Attestation token over the Back End's challenge");
                if (round == 1 && u == 0)
                {
                    // ON THE WAY: a copy of the update with one weight of the final layer changed, sent with a token of the same Front End over a new challenge
                    var changed = model.DeepClone().AsObject();
                    var item = (changed["MLModelData"] as JsonArray)!.OfType<JsonObject>().First();
                    var data = Convert.FromBase64String((string)item["Data"]!); data[0] ^= 0x40; item["Data"] = Convert.ToBase64String(data);
                    var again = gate.Challenge(EndUsers[u].Name, request + "-copy");
                    returned.Add(new Update(EndUsers[u].Name, request + "-copy", changed, FlFrontEndSecurity.Token(frontEnds[u].For(FrontEndOwner), again), signature, "a copy changed on the way"));
                    Say($"      ON THE WAY (shown on purpose): a copy of {EndUsers[u].Name}'s update with one weight changed also reaches the Back End, with the same signature");
                }
            }
            if (Secure && round == 2)
            {
                // ALTERED CODE: Chiara's Front End starts again with a training code that is not the approved one (its measurement differs)
                var u = 2; var name = EndUsers[u].Name;
                frontEnds[u].Restart(altered: true);
                Say($"   {name}'s Front End starts again with ALTERED code ({FlCode.AlteredFile(AppContext.BaseDirectory)} measured with another SHA-256): its Security API opened: {frontEnds[u].Security.Opened}");
                try { _ = Vault(Path.Combine(Work, $"vault-{name}"), name, frontEnds[u]); Say("      its vault opened (NOT expected)"); }
                catch (InvalidOperationException e) { Say($"      its vault does not open: {e.Message}"); }
                var request = $"round-{round}-{name}-altered";
                var challenge = gate!.Challenge(name, request);
                var sent = returned[u].Model.DeepClone().AsObject();
                var signature = FlFrontEndSecurity.Sign(frontEnds[u].For(FrontEndOwner), Bytes(sent));
                Say($"      it sends an update all the same (a copy of the model it sent before), {(signature is null ? "unsigned: its signing key is under the sealed master key" : "signed")}, with its token over the Back End's challenge");
                returned.Add(new Update(name, request, sent, FlFrontEndSecurity.Token(frontEnds[u].For(FrontEndOwner), challenge), signature, "from altered code"));
            }

            Step("The Back End receives the trained AIMs and checks that each was made from its model");
            if (Secure) Say("   SECURE: the Back End first checks each update's Attestation token (approved code, its challenge) and signature (the Front End it enrolled); the averaging does not use the sender.");
            else Say("   SIMULATED: the models arrive without a sender shown. The Controllers still sign their answers (not simulated here); the Back End's code never reads who sent a model.");
            var accepted = new List<JsonObject>(); var publishedVector = HflAimProcessor.Vector(published);
            foreach (var update in returned)
            {
                var m = update.Model;
                if (Secure)
                {
                    var why = gate!.Admit(update.From, update.Request, update.Token!, Bytes(m), update.Signature);
                    var what = update.Note.Length > 0 ? $"{update.From} ({update.Note})" : update.From;
                    if (why is not null) { Say($"   REFUSED, an update from {what}: {why}"); continue; }
                    Say($"   an update from {what}: its token proves approved code over the Back End's challenge; its signature verifies");
                }
                var wexOut = await wex.ProcessAsync(new Message { MessageId = "wex-" + round, Ports = new Dictionary<string, string> {
                    [wexPorts.Input(NnwJson.Model)] = m.ToJsonString(), [wexPorts.Input(NnwJson.Key)] = key.ToJsonString(), [wexPorts.Input(NnwJson.Locator)] = locator.ToJsonString() } });
                var read = wexOut.IsError ? null : JsonNode.Parse(wexOut.Payload)!.AsObject();
                var ours = read is not null && (string?)read["PayloadData"] == payload && ((string?)read["DescrMetadata"])!.StartsWith("mark present", StringComparison.Ordinal);
                var moved = Math.Sqrt(HflAimProcessor.Vector(m).Zip(publishedVector, (x, y) => (double)(x - y) * (x - y)).Sum());
                Say($"   a trained AIM (moved {moved:F2} from the published model): watermark {(ours ? "FOUND - made from the Back End's model, taken" : "NOT found - not made from the Back End's model, refused")} ({read?["DescrMetadata"] ?? wexOut.Payload})");
                if (ours) accepted.Add(m);
            }

            hfl.Policy = new RoundPolicy
            {
                WeightCount = HflAimProcessor.Vector(published).Length, MaxDistance = MaxDistance,
                Evaluate = m => judge.Score(m, checkSet)
            };
            hfl.StartFrom(published);
            Message Msg(JsonObject o) => new() { MessageId = Guid.NewGuid().ToString("N"), Ports = new Dictionary<string, string> { [ports.Input(AihJson.FederatedLearn)] = o.ToJsonString() } };
            JsonObject Fdl(JsonObject item) => new() { ["Header"] = "AIH-FDL-V1.1", ["FederatedLearnID"] = AihJson.NewId("fdl"), ["FederatedLearnTime"] = AihJson.Time(), ["FederatedLearnData"] = new JsonArray(item) };
            foreach (var _ in accepted) await hfl.ProcessAsync(Msg(Fdl(new JsonObject { ["Request"] = new JsonObject { ["HFEID"] = "hfe", ["PreLearnNNModelID"] = "model-" + (round - 1) } })));
            foreach (var m in accepted)
            {
                var response = Fdl(new JsonObject { ["Response"] = new JsonObject { ["AIHDataID"] = "aih-data-of-a-front-end-" + Guid.NewGuid().ToString("N")[..6], ["PostLearnNNModel"] = m } });
                await hfl.ProcessAsync(Msg(response));
            }
            foreach (var r in hfl.Refusals.Skip(refusalsSeen)) Say($"   REFUSED ({r.What}, round {r.Round}): {r.Reason}");
            refusalsSeen = hfl.Refusals.Count;
            if (Secure && frontEnds[2].Altered)
            {
                frontEnds[2].Restart(altered: false);
                Say($"   {EndUsers[2].Name}'s Front End starts again with the approved code: its Security API opened: {frontEnds[2].Security.Opened}; its vault opens again");
            }
            var outcome = hfl.Outcomes.Count > 0 ? hfl.Outcomes[^1] : null;
            if (outcome is null || outcome.Round != round || !outcome.Accepted) { Say("   the result was refused: the Back End's model stays as it was"); continue; }
            Say($"   accepted: {outcome.Updates} models averaged (the network and the final layer), weighted by their number of cases ({outcome.Cases}); on the fixed set: {Acc(hfl.CurrentModel!)} (the published model: {Acc(published)})");
            var averaged = await wex.ProcessAsync(new Message { MessageId = "wex-avg-" + round, Ports = new Dictionary<string, string> {
                [wexPorts.Input(NnwJson.Model)] = hfl.CurrentModel!.ToJsonString(), [wexPorts.Input(NnwJson.Key)] = key.ToJsonString(), [wexPorts.Input(NnwJson.Locator)] = locator.ToJsonString() } });
            Say($"   the averaged model: {JsonNode.Parse(averaged.Payload)?["DescrMetadata"]}");
            current = hfl.CurrentModel!.DeepClone().AsObject();
        }

        Step("What is in the End Users' storage");
        var vaultFiles = EndUsers.SelectMany(d => Directory.GetFiles(Path.Combine(Work, $"vault-{d.Name}"), "*.data")).ToList();
        var inClear = vaultFiles.Count(f => Encoding.Latin1.GetString(File.ReadAllBytes(f)).Contains("MLModel", StringComparison.Ordinal));
        Say($"   {vaultFiles.Count} values in the three vaults; an NN Model readable in clear in {inClear} of them" + (Secure ? " (sealed: AES-256-GCM under a key of the vault's Owner)" : " (the Basic mode keeps them in clear)"));

        Step("Known problems of federated learning, and what this demo does about each");
        Say("   Problem                                      Status      How");
        Say("   Images leave the End User                    BUILT       only the model (network and final layer) and the number of cases leave; images stay in the End User's storage");
        Say("   A poisoned or extreme update                 BUILT       size, finite values and a distance limit before averaging");
        Say("   A bad result of averaging                    BUILT       evaluation on a fixed set; the old version is kept and the refusal recorded");
        Say("   Unequal numbers of cases                     BUILT       average weighted by the number of cases (count in the second Data item)");
        if (!Secure) Say("   Who sent a model is used against the sender  SIMULATED   the Back End's code never reads the sender (the Controllers still sign)");
        else
        {
            Say("   Who sent a model is used against the sender  NOT BUILT   the Back End now knows the sender (it checks its token and signature); the averaging does not use it");
            Say("   A model from code that is not approved       BUILT       Attestation token (MPAI_AIFAT_Get_Token) over the Back End's challenge, checked against the approved code; else refused");
            Say("   A model changed on the way, or not sent by   BUILT       ECDSA P-256 signature by a persistent key of the Front End's Security API, verified by the Back End's");
            Say("   a Front End the Back End enrolled");
            Say("   Images and models in clear in the vault      BUILT       the vault sealed (AES-256-GCM) under a key of its Owner; it opens only with the approved code");
            Say("   The watermark key in the code or in a file   BUILT       a persistent key of the Back End's Security API, sealed to its code");
            Say("   Roots of trust                               SIMULATED   the TPM 2.0 reference simulator in WSL, one per party; the Front Ends are enrolled directly");
        }
        Say("   A trained AIM not made from the Back End's   BUILT       the Back End's watermark (NoConstraint, IMT), inserted while it trains its first model (DWI); WEX on every trained AIM; without the mark, refused");
        Say("   Real chest images and a real network         SIMULATED   synthetic images; a small network whose starting weights are random, trained by the Back End and the End Users");
        Say("   Controllers, Modules and AIM hosts           SIMULATED   the components are called directly here; the first-slice demo runs them as Controllers");
        Say("   Leakage through the model's weights          PLANNED     noise (differential privacy) is not added");
        Say("   Server sees each End User's update           PLANNED     secure aggregation is not built");
    }

    private static string FindRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (Directory.Exists(Path.Combine(dir.FullName, "AIMs", "AMDs")) && Directory.Exists(Path.Combine(dir.FullName, "Apps", "AIH"))) return dir.FullName;
        throw new DirectoryNotFoundException("The software tree is not above " + AppContext.BaseDirectory);
    }
}
