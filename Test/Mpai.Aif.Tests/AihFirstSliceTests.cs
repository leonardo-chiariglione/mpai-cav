using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Aih;
using Mpai.Aih.Chain;
using Mpai.Aih.Hsp;

namespace Mpai.Aif.Tests;

// THE FIRST SLICE OF AIH-HSP, END TO END: two End Users, each with a Health Front End (a Controller, a Module, a User Agent running HFE.orch), the Health Back End
// (another Controller, Module and User Agent, running HBE.orch, with a test Blockchain), and a Third-Party User. The Front Ends talk to the Back End only.
//   register -> make AIH Data from Health Data and a Model Licence (checked, kept in the Secure Data Vault, alert if a risk) -> upload (the Smart Contract made, the
//   AIH Data kept) -> a Third-Party User processes it under the licence and is refused outside it -> the End User has it de-identified, and the audit shows what was not licensed.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AihFirstSliceTests : IDisposable
{
    private readonly string work = Path.Combine(Path.GetTempPath(), "aih-slice-" + Guid.NewGuid().ToString("N"));
    private readonly List<IDisposable> toDispose = [];
    private readonly HashChainBlockchain chain = new();

    public void Dispose() { storeKey.Dispose(); foreach (var d in Enumerable.Reverse(toDispose)) d.Dispose(); try { Directory.Delete(work, true); } catch { } }

    private static string Orch(string name) => Path.Combine(Repository.Root, "Apps", "AIH", "HSP", name);

    // The sub-AIMs of each composite: placed on an AIM host when the Controller and the Module are to be different processes.
    private static readonly string[] BackEndAims = ["1AIH-HDP-V1.1-I01", "1AIH-ARA-V1.1-I01", "1AIH-DIA-V1.1-I01", "1AIH-ADT-V1.1-I01", "1AIH-HFL-V1.1-I01"];
    private static readonly string[] FrontEndAims = ["1AIH-MDL-V1.1-I01", "1AIH-HDP-V1.1-I01", "1AIH-ARA-V1.1-I01"];
    private readonly Dictionary<string, (AIF.Store.AimRepository Repository, AIF.Store.StoreAimSource Source)> holdings = [];
    private readonly Dictionary<string, ControllerApi> controllers = [];
    private readonly Dictionary<string, string> moduleFolders = [];

    // THE LICENCE OF THE MODEL AIM. The Back End's organisation is a PTF Trust Anchor (the Trust Authority that admits the Controllers of its Front Ends); the MPAI Store signs with
    // a key the Front Ends' Controllers know. The Back End offers the AIM that wraps each model for a year, free, to run and keep: the Store issues one AIM Licence to the Trust Anchor.
    private readonly System.Security.Cryptography.ECDsa storeKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    private readonly AIF.Trust.TrustAuthority organisation = new("hbe-organisation");
    private static JsonObject ModelOffer() => new()
    {
        ["Header"] = "AIF-LIC-V3.0", ["TermsURI"] = "https://store.example/terms/AIH-NNM-V1.1", ["Sublicensing"] = new JsonObject { ["Allowed"] = true },
        ["Grant"] = new JsonObject { ["Rights"] = new JsonArray("Run", "Keep") }, ["Term"] = new JsonObject { ["Duration"] = "P1Y" }
    };

    // The L3 of a composite, its AIMs executing apart from the Controller (Relation External): the Controller and the Module are always separate.
    private static string Apart(string l3, IEnumerable<string> aims)
    {
        foreach (var aim in aims)
            l3 = System.Text.RegularExpressions.Regex.Replace(l3, "(\"AIMName\"\\s*:\\s*\"" + aim + "\"\\s*,\\s*\"Relation\"\\s*:\\s*)\"Internal\"", "$1\"External\"");
        return l3;
    }

    // THE CONTROLLER AND THE MODULE ARE SEPARATE, always: the Controller holds no AIM - it reaches the AIMs of the Module where they execute (a process of their own, AIF.AimHost) - and
    // the two keep a folder each. A Front End's Controller keeps a repository of the Metadata, the fingerprints and the AIM Licences it obtains from the MPAI Store (it obtains no
    // Implementation: that is where the AIMs execute) and follows the Store's newer versions. What executes the Module's AIMs keeps a repository of its own, in which it takes in an AIM
    // its Controller hands it that it has not seen: it obtains the Implementation and its models itself, from where the Metadata says, and checks them and the AIM Licence.
    private ControllerApi Controller(string name, string role)
    {
        var folder = Path.Combine(work, name); Directory.CreateDirectory(folder);
        var settings = Path.Combine(folder, "aim-settings.json");
        File.WriteAllText(settings, new JsonObject
        {
            ["1AIH-HDP-V1.1-I01"] = new JsonObject { ["Role"] = role, ["BackEndId"] = "hbe-1" }, ["1AIH-ADT-V1.1-I01"] = new JsonObject { ["BackEndId"] = "hbe-1" }
        }.ToJsonString());

        var aims = role == "BackEnd" ? BackEndAims : FrontEndAims;
        var composite = role == "BackEnd" ? "1AIH-HBE-V1.1-I01" : "1AIH-HFE-V1.1-I01";
        string Copy(string into)                                                                          // a folder of AIM Metadata with the composite's AIMs executing apart
        {
            Directory.CreateDirectory(into);
            foreach (var file in Directory.EnumerateFiles(Repository.Amds, "*.json")) File.Copy(file, Path.Combine(into, Path.GetFileName(file)));
            File.WriteAllText(Path.Combine(into, composite + ".json"), Apart(File.ReadAllText(Path.Combine(into, composite + ".json")), aims));
            return into;
        }
        var module = Copy(Path.Combine(folder, "module-amds"));
        var extra = new List<string> { "--settings", settings };
        AIF.Store.AimRepository? repository = null; AIF.Store.StoreAimSource? source = null;
        if (role == "FrontEnd")
        {
            // The identities: the Controller's, and that of what executes its Module's AIMs - both under the organisation''s Trust Anchor - and the key of the MPAI Store.
            var spki = (System.Security.Cryptography.ECDsa key) => Convert.ToHexString(key.ExportSubjectPublicKeyInfo());
            var module_ = organisation.Admit("module-" + name, TimeSpan.FromDays(30));
            var identity = new JsonObject
            {
                ["ControllerId"] = "module-" + name, ["Credential"] = module_.Credential.DeepClone(), ["Cii"] = module_.Anchor.Object(),
                ["Anchors"] = new JsonObject { [organisation.Anchor.AnchorId] = spki(organisation.Anchor.PublicKey) }
            };
            var identityFile = Path.Combine(folder, "module-identity.json"); File.WriteAllText(identityFile, identity.ToJsonString());
            extra.AddRange(["--identity", identityFile, "--store-key", "store-key-1=" + spki(storeKey)]);
            moduleFolders[name] = module;

            var admission = organisation.Admit(name, TimeSpan.FromDays(30));
            repository = new AIF.Store.AimRepository(Copy(Path.Combine(folder, "controller-amds")))
            {
                Identity = new AIF.Store.AimLicences.ControllerIdentity(name, admission.Credential, admission.Anchor.Object(), [], a => a == organisation.Anchor.AnchorId ? organisation.Anchor.PublicKey : null),
                StoreKeys = id => id == "store-key-1" ? storeKey : null
            };
            source = new AIF.Store.StoreAimSource(Path.Combine(work, "mpai-store"), organisation.Anchor.AnchorId, new AIF.Store.AimLicences.Issuer(storeKey, "store-key-1", "mpai-store", "P7D"));
        }

        var process = new HostProcess(amds: module, provider: Path.Combine(AppContext.BaseDirectory, "Mpai.Aih.dll") + ":Mpai.Aih.AihProvider", extra: extra.ToArray(), workingDirectory: Repository.Root);
        toDispose.Add(process);
        var api = new ControllerApi(repository?.Folder ?? module, settings, _ => new AIF.Controller.CompositeProvider());
        api.Controller.AimHostKey = HostProcess.Key;
        foreach (var aim in aims.Append(composite)) api.Controller.AimHosts[aim] = process.Address;      // what executes the Module's AIMs: for each, and for the Module itself - the AIMs that come later
        if (repository is not null)
        {
            api.Repository = repository; api.Source = source; api.FollowNewerVersions = true; api.FetchImplementations = false;
            holdings[name] = (repository, source!);
        }
        controllers[name] = api;
        toDispose.Add(api);
        return api;
    }
    // THE MPAI STORE: the Back End uploads to it the AIMs that wrap its models, the Front Ends download from it; it publishes only what conforms to the L2.
    private Mpai.Aih.Nnm.ModelAimPublisher? mpaiStore;
    private Mpai.Aih.Nnm.ModelAimPublisher MpaiStore => mpaiStore ??= new(Path.Combine(work, "mpai-store"), Path.Combine(work, "hbe-implementations"), l3 => new AIF.Metadata.L2Conformance(Repository.Schemas, _ => null).Check(l3));

    private HbeUserAgent BackEnd()
    {
        var ua = new HbeUserAgent(Controller("hbe", "BackEnd"), chain, Orch("HBE.orch"), Path.Combine(work, "hbe", "secure-storage"), models: MpaiStore,
            frontEndL3: Apart(File.ReadAllText(Path.Combine(Repository.Amds, "1AIH-HFE-V1.1-I01.json")), FrontEndAims), modelLicence: ModelOffer());
        toDispose.Add(ua);
        return ua;
    }

    private HfeUserAgent FrontEnd(IBackEnd backEnd, string user)
    {
        var ua = new HfeUserAgent(Controller("hfe-" + user, "FrontEnd"), backEnd, user, Orch("HFE.orch"), Path.Combine(work, "hfe-" + user, "secure-data-vault"));
        toDispose.Add(ua);
        return ua;
    }

    // Health Data of an electrocardiogram with a beat every `period` samples at 250 Hz.
    private static JsonObject HealthData(string user, int period)
    {
        var health = AihSamples.HealthData(user);
        health["HealthDataData"] = new JsonArray(new JsonObject
        {
            ["Data"] = Convert.ToBase64String(Enumerable.Range(0, 2500).SelectMany(i => BitConverter.GetBytes((short)(i % period == 0 ? 12000 : 10))).ToArray())
        });
        return health;
    }

    // A Model Licence: the Back End may keep the AIH Data (pseudonymised), and the Third-Party User tpu-1 may have it processed - unless `licenseTpu` is false.
    private static JsonObject Licence(string user, string aihData, bool licenseTpu, string backEndStatus = "Pseudonymised")
    {
        var licence = AihSamples.ModelLicence(user, aihData);
        ((JsonObject)licence["ModelLicenceData"]!["Specific"]![0]!["BackEndLicence"]!)["AIHDataStatus"] = backEndStatus;
        ((JsonObject)licence["ModelLicenceData"]!["Specific"]![0]!["BackEndLicence"]!)["BackEndID"] = "hbe-1";
        if (licenseTpu)
            ((JsonArray)licence["ModelLicenceData"]!["Specific"]!).Add(new JsonObject
            {
                ["ThirdPartyUserLicence"] = new JsonObject
                {
                    ["BackEndID"] = "hbe-1", ["ThirdPartyUserID"] = "tpu-1", ["AIHDataID"] = aihData, ["Time"] = AihSamples.Time("t"),
                    ["LicensableEndUsers"] = new JsonArray(new JsonObject { ["UserID"] = user, ["AIHDataClasses"] = new JsonArray("AIH-ECO") }),
                    ["UserTypes"] = new JsonArray("Non-Profit Entity"), ["ProcessTypes"] = "PhysiologicalRiskPrediction", ["AnomalyTypes"] = new JsonArray("Point Anomaly"), ["UsageTypes"] = "Research"
                }
            });
        return licence;
    }

    private static JsonObject Request(string requester, string type, JsonObject tokens, string id) => new()
    {
        ["Header"] = AihJson.Process, ["UserID"] = requester, ["AIHDataProcessID"] = AihJson.NewId("ahp"), ["AIHDataProcessTime"] = AihJson.Time(),
        ["AIHDataProcess"] = new JsonArray(new JsonObject
        {
            ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = new JsonArray(id), ["ProcessingType"] = type, ["Tokens"] = tokens.DeepClone() }
        })
    };

    private static bool Done(JsonObject response) => AihJson.ResponseOf(response)!["ProcessingStatus"]!.GetValue<bool>();

    // Without and with the security model of AIF: the Back End the Trust Authority, the parties it admitted trusting it, everything signed.
    // and with the Controller and the Module of each end as different processes.
    [SkippableTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void TheFirstSlice(bool trusted)
    {
        using var hbe = BackEnd();
        var trust = new BackEndTrust();
        var parties = new Dictionary<string, PartyTrust>();
        IBackEnd Door(string party, string subject)
        {
            if (!trusted) return hbe;
            var admitted = trust.Enroll(party, subject); admitted.Establish();
            parties[party] = admitted;
            return new TrustedClient(admitted, new TrustedBackEnd(hbe, trust));
        }
        // What the Back End asks of a Front End goes through the Front End's door, signed both ways, when there is trust.
        IFrontEnd Learner(string party, IFrontEnd front) => trusted ? new TrustedFrontEndClient(new TrustedFrontEnd(front, parties[party]), trust, party) : front;
        var one = FrontEnd(Door("hfe:user-1", "user-1"), "user-1");
        var two = FrontEnd(Door("hfe:user-2", "user-2"), "user-2");
        var tpu = Door("tpu:tpu-1", "tpu-1");

        // 1. The End Users register with the Back End, each through their own Front End.
        var register = AihSamples.RegisterRequest("user-1"); register["RegisterData"]![0]!["Request"]!["RequestedServices"] = new JsonArray(
            new JsonObject { ["Service"] = "Store" }, new JsonObject { ["Service"] = "DeIDAndAnonym" }, new JsonObject { ["Service"] = "Auditing" });
        Assert.True(AihJson.RegisterResponseOf(one.Register(register))!["Success"]!.GetValue<bool>());
        Assert.NotNull(one.Tokens);
        var register2 = AihSamples.RegisterRequest("user-2"); register2["RegisterData"]![0]!["Request"]!["RequestedServices"] = new JsonArray(
            new JsonObject { ["Service"] = "Store" }, new JsonObject { ["Service"] = "DeIDAndAnonym" }, new JsonObject { ["Service"] = "Auditing" });
        Assert.True(AihJson.RegisterResponseOf(two.Register(register2))!["Success"]!.GetValue<bool>());

        // 2. user-1 acquires Health Data and provides a licence that names tpu-1: the AIH Data is made and kept; a regular rate raises no alert.
        Assert.Null(one.Acquire(HealthData("user-1", 190), Licence("user-1", "ahd-1", licenseTpu: true)));
        // user-2's licence names no Third-Party User, and the rate is a risk: the alert.
        var alert = two.Acquire(HealthData("user-2", 125), Licence("user-2", "ahd-2", licenseTpu: false, backEndStatus: "Identified"));
        Assert.NotNull(alert);
        Assert.Equal("Yellow", alert!["ARAData"]!["Risk"]!.ToString());                                      // the alert is the ARA Data

        // 3. Both upload; the Back End makes the Smart Contracts and keeps the AIH Data.
        Assert.True(Done(one.Upload("ahd-1")));
        Assert.True(Done(two.Upload("ahd-2")));
        Assert.StartsWith("sc-", chain.ContractOf("ahd-1"));
        Assert.StartsWith("sc-", chain.ContractOf("ahd-2"));

        // 4. A Third-Party User registers and processes: answered under the licence, refused outside it.
        var tpuRegister = AihSamples.ThirdPartyRegisterRequest("tpu-1");
        var tpuTokens = (JsonObject)AihJson.RegisterResponseOf(JsonNode.Parse(tpu.Register(tpuRegister.ToJsonString()))!.AsObject())!["Tokens"]!;
        var inside = JsonNode.Parse(tpu.Process(Request("tpu-1", "AIHDataProcessing", tpuTokens, "ahd-1").ToJsonString()))!.AsObject();
        Assert.True(Done(inside));
        Assert.Equal("Green", AihJson.ResponseOf(inside)!["AIHDataOrAIHDataID"]![0]!["AIHData"]!["ARAData"]!["ARAData"]!["Risk"]!.ToString());
        var outside = JsonNode.Parse(tpu.Process(Request("tpu-1", "AIHDataProcessing", tpuTokens, "ahd-2").ToJsonString()))!.AsObject();
        Assert.False(Done(outside));

        // 5. user-1 has the Back End de-identify (licensed: it states Pseudonymised) and user-2 the same (not licensed: it states Identified); the audit shows it.
        Assert.True(AihJson.Parse(one.DeIdentify("ahd-1", "De-Identify", "Hashing").ToJsonString(), "r")["DeIDAndAnonymData"]![0]!["Response"]!["ProcessingState"]!.GetValue<bool>());
        Assert.True(AihJson.Parse(two.DeIdentify("ahd-2", "De-Identify", "Hashing").ToJsonString(), "r")["DeIDAndAnonymData"]![0]!["Response"]!["ProcessingState"]!.GetValue<bool>());
        Assert.Empty(one.Audit()["Audit"]!["Response"]!["AIHDataIDs"]!.AsArray());
        var unlicensed = two.Audit()["Audit"]!["Response"]!["AIHDataIDs"]!.AsArray();
        Assert.Equal("ahd-2-deid", Assert.Single(unlicensed)!["AIHDataID"]!.ToString());

        // 5b. The Tokens are checked. A Third-Party User was granted the AIH Data Processing service only: a request to de-identify, or to audit, is not served - whoever carries it.
        var refusedDeId = JsonNode.Parse(tpu.DeIdentify(new JsonObject
        {
            ["Header"] = AihJson.DeId, ["DeIDAndAnonymID"] = "x", ["DeIDAndAnonymTime"] = AihJson.Time(), ["EndUserID"] = "tpu-1",
            ["DeIDAndAnonymData"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["AIHDataID"] = "ahd-1", ["DeIDAndAnonymType"] = "De-Identify", ["DeIDAndAnonymAlgoID"] = "Hashing", ["Tokens"] = tpuTokens.DeepClone() } })
        }.ToJsonString()))!;
        Assert.False(refusedDeId["DeIDAndAnonymData"]![0]!["Response"]!["ProcessingState"]!.GetValue<bool>());
        var refusedAudit = JsonNode.Parse(tpu.Audit(new JsonObject
        {
            ["Header"] = AihJson.Audit, ["UserID"] = "tpu-1", ["AuditID"] = "y", ["AuditTime"] = AihJson.Time(),
            ["Audit"] = new JsonObject { ["Request"] = new JsonObject { ["Tokens"] = tpuTokens.DeepClone(), ["AuditPeriod"] = AihJson.Time(), ["ProcessingOperations"] = new JsonArray(new JsonObject { ["ProcessingOperation"] = "PhysiologicalRiskPrediction" }) } }
        }.ToJsonString()))!;
        Assert.False(refusedAudit["Audit"]!["Response"]!["ProcessingState"]!.GetValue<bool>());
        Assert.Empty(refusedAudit["Audit"]!["Response"]!["AIHDataIDs"]!.AsArray());

        // 6. A round of Federated Learning: the Back End asks each Front End for the model it trained and averages them (user-1's data is Green, user-2's Yellow).
        var model = hbe.FederatedLearning("model-0", ("hfe-1", Learner("hfe:user-1", one)), ("hfe-2", Learner("hfe:user-2", two)));
        Assert.Equal("model-0-federated-1", model["MLModelObjectID"]!.ToString());
        var weights = new float[2];
        Buffer.BlockCopy(Convert.FromBase64String(model["MLModelData"]![0]!["Data"]!.ToString()), 0, weights, 0, 8);
        Assert.Equal(0.375f, weights[0], 3);                                                         // the mean of 0.25 and 0.5
        Assert.Equal(1f, weights[1], 3);

        // What the Back End put in the MPAI Store is the AIM that wraps this model, and a new version of the Front End that has it as a Sub-AIM; the next round names the AIM.
        Assert.Equal("1AIH-NNM-V1.1-I01", hbe.ModelAimId);
        Assert.Equal("1.2", hbe.FrontEndVersion);
        hbe.FederatedLearning("model-0", ("hfe-1", Learner("hfe:user-1", one)), ("hfe-2", Learner("hfe:user-2", two)));
        Assert.Equal("1AIH-NNM-V1.1-I02", hbe.ModelAimId);
        Assert.Equal("1.3", hbe.FrontEndVersion);

        // 7. A Front End's Controller follows the Store. user-1's Module is restarted: the Controller finds that the Store has a newer Front End, obtains its Metadata and that of the AIM it
        // names (and only those), with the fingerprints and the AIM Licence, reads the Metadata again, and hands the AIM to where the Module's AIMs execute. There - and only there - the
        // Implementation is obtained, from where the Metadata says, measured and checked with the AIM Licence, and the AIM is built. A rate the Anomaly and Risk Alerting cannot judge
        // (one beat only) is judged by the model: an alert, where there was none.
        {
            var (repository, source) = holdings["hfe-user-1"];
            const string hfe = "1AIH-HFE-V1.1-I01";
            Assert.Equal("1.1", repository.VersionOf(hfe));
            Assert.Empty(source.Obtained);
            one.Dispose();
            using var restarted = new HfeUserAgent(controllers["hfe-user-1"], Door("hfe:user-1", "user-1"), "user-1", Orch("HFE.orch"), Path.Combine(work, "hfe-user-1", "secure-data-vault"));
            Assert.Equal("1.3", repository.VersionOf(hfe));
            Assert.Equal([hfe, "1AIH-NNM-V1.1-I02"], source.Obtained);
            Assert.True(repository.Holds("1AIH-NNM-V1.1-I02"));
            var judged = restarted.Acquire(HealthData("user-1", 100_000), Licence("user-1", "ahd-7", licenseTpu: false));
            Assert.NotNull(judged);
            Assert.Equal("Yellow", judged!["ARAData"]!["Risk"]!.ToString());
            Assert.Equal(2, source.Obtained.Count);                                                   // nothing was obtained again

            // The AIM that wraps the model has an AIM Licence: issued by the MPAI Store to the organisation's Trust Anchor, kept by this Front End's Controller with the AIM, and checked
            // when the Controller built it. The Store issued one (the Front Ends of the organisation share it) and it is revoked when the Store is told to.
            var licence = repository.LicenceOf("1AIH-NNM-V1.1-I02") ?? throw new Xunit.Sdk.XunitException("the Controller holds no AIM Licence for the model AIM");
            Assert.Equal(organisation.Anchor.AnchorId, (string?)licence["Licensee"]!["TrustAnchorID"]);
            Assert.Equal("mpai-store", (string?)licence["Licensor"]!["LicensorID"]);
            Assert.Single(source.Issued);
            repository.CheckLicence("1AIH-NNM-V1.1-I02", source);                                     // the Store confirms it
            source.Revoke(repository.RightOf("1AIH-NNM-V1.1-I02")!);
            Assert.Contains("revoked", Assert.Throws<InvalidOperationException>(() => repository.CheckLicence("1AIH-NNM-V1.1-I02", source)).Message);

            // The Controller and the Module keep a repository each. The Controller holds the Metadata, the fingerprints and the AIM Licence and no Implementation; what executes the
            // Module's AIMs holds the model it obtained itself, and the AIM Licence it checked there.
            const string nnm = "1AIH-NNM-V1.1-I02";
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(repository.Folder, "implementations"), "*", SearchOption.AllDirectories));
            var module = moduleFolders["hfe-user-1"];
            Assert.True(File.Exists(Path.Combine(module, "implementations", nnm, nnm + ".model.json")));
            Assert.True(File.Exists(Path.Combine(module, "repository", nnm + ".licence.json")));
            Assert.Equal(File.ReadAllText(Path.Combine(repository.Folder, "repository", nnm + ".licence.json")), File.ReadAllText(Path.Combine(module, "repository", nnm + ".licence.json")));
        }
        // The ledger holds together, and shows who was refused.
        Assert.True(chain.Verify());
        Assert.Contains(chain.Ledger, e => e.Kind == "Confirm" && e.Requester == "tpu-1" && e.AihDataId == "ahd-2" && !e.Allowed);
    }
}
