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

    // The Back End's organisation is a PTF Trust Anchor (the Trust Authority that admits the Controllers of its Front Ends); the MPAI Store signs with a key the Front Ends' Controllers know.
    private readonly System.Security.Cryptography.ECDsa storeKey = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
    private readonly AIF.Trust.TrustAuthority organisation = new("hbe-organisation");

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
            frontEndL3: Apart(File.ReadAllText(Path.Combine(Repository.Amds, "1AIH-HFE-V1.1-I01.json")), FrontEndAims));
        toDispose.Add(ua);
        // The Back End's Controller is the Trust Authority of its service (the exchanges between the Controllers): its workflow asked for it ('ask Controller to admit by').
        Assert.NotNull(ua.Anchor);
        Assert.NotNull(ua.Rendezvous);
        backEndUa = ua;
        return ua;
    }

    private HbeUserAgent? backEndUa;

    // The service of the Back End, as the End User decided to trust it (anchor: the Trust Authority's, unless the User decided to trust another), and the name the Back End calls this Front End by.
    private (string Service, string Anchor, string Rendezvous, string KnownAs) ServiceOf(string user, string? anchor = null, string? name = null) =>
        ("AIH-HBE", anchor ?? backEndUa!.Anchor!, backEndUa!.Rendezvous!, name ?? "hfe-" + user.Split('-')[1]);

    private HfeUserAgent FrontEnd(string user, string? anchor = null, string? name = null)
    {
        var ua = new HfeUserAgent(Controller("hfe-" + user, "FrontEnd"), user, Orch("HFE.orch"), Path.Combine(work, "hfe-" + user, "secure-data-vault"), ServiceOf(user, anchor, name));
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

    // The security model of AIF: the Back End's Controller is the Trust Authority of its service, the Front Ends' Controllers join it, and every request and answer goes between the
    // Controllers, signed - with the Controller and the Module of each end as different processes. No User Agent carries anything to another (2026-11-13: De-ID and Audit too).
    [SkippableFact]
    public void TheFirstSlice()
    {
        using var hbe = BackEnd();
        var one = FrontEnd("user-1");
        var two = FrontEnd("user-2");

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
        // The Third-Party User runs a Health Front End too; its User Profile makes it do what a Third-Party User does: it registers, and asks for AIH Data to be processed as the licence prescribes.
        var tpuFront = FrontEnd("tpu-1", name: "tpu-1");
        var tpuTokens = (JsonObject)AihJson.RegisterResponseOf(tpuFront.Register(AihSamples.ThirdPartyRegisterRequest("tpu-1")))!["Tokens"]!;
        var inside = tpuFront.Process("ahd-1");
        Assert.True(Done(inside));
        Assert.Equal("Green", AihJson.ResponseOf(inside)!["AIHDataOrAIHDataID"]![0]!["AIHData"]!["ARAData"]!["ARAData"]!["Risk"]!.ToString());
        var outside = tpuFront.Process("ahd-2");
        Assert.False(Done(outside));

        // 5. user-1 has the Back End de-identify (licensed: it states Pseudonymised) and user-2 the same (not licensed: it states Identified); the audit shows it.
        Assert.True(AihJson.Parse(one.DeIdentify("ahd-1", "De-Identify", "Hashing").ToJsonString(), "r")["DeIDAndAnonymData"]![0]!["Response"]!["ProcessingState"]!.GetValue<bool>());
        Assert.True(AihJson.Parse(two.DeIdentify("ahd-2", "De-Identify", "Hashing").ToJsonString(), "r")["DeIDAndAnonymData"]![0]!["Response"]!["ProcessingState"]!.GetValue<bool>());
        Assert.Empty(one.Audit()["Audit"]!["Response"]!["AIHDataIDs"]!.AsArray());
        var unlicensed = two.Audit()["Audit"]!["Response"]!["AIHDataIDs"]!.AsArray();
        Assert.Empty(hbe.Errors);                                                                    // what the Module asked of the Blockchain was answered
        Assert.Equal("ahd-2-deid", Assert.Single(unlicensed)!["AIHDataID"]!.ToString());

        // 5b. A Third-Party User may ask for processing only (its Tokens grant AIH Data Processing): a request to de-identify, or to audit, is not served.
        Assert.DoesNotContain("DeIDAndAnonym", AihJson.ServicesOf(tpuTokens));
        var refusedDeId = tpuFront.DeIdentify("ahd-1", "De-Identify", "Hashing");
        Assert.False(refusedDeId["DeIDAndAnonymData"]![0]!["Response"]!["ProcessingState"]!.GetValue<bool>());
        var refusedAudit = tpuFront.Audit();
        Assert.False(refusedAudit["Audit"]!["Response"]!["ProcessingState"]!.GetValue<bool>());
        Assert.Empty(refusedAudit["Audit"]!["Response"]!["AIHDataIDs"]!.AsArray());

        // 6. FEDERATED LEARNING (A8, 2026-11-13). A Front End answers a Federated Learn request with the Neural Network Model that the AIM wrapping it (AIH-NNM) holds, trained on the
        // AIH Data the AIM processed: so the Back End first publishes the AIM with its first model, and the Front End version that has it. Each Front End's Controller follows the
        // Store when its Module starts: it obtains the new Front End and the AIM (and only those), with the fingerprints, and hands the AIM to where the Module's AIMs execute; there
        // the Implementation is obtained, measured and checked, and the AIM is built. The AIM then processes AIH Data and learns (user-1's data is Green, user-2's Yellow).
        const string hfe = "1AIH-HFE-V1.1-I01";
        Assert.Equal("1AIH-NNM-V1.1-I01", hbe.PublishModel(Mpai.Aih.Hfl.FrontEndTrainer.ModelObject("model-0", new float[] { 0f, 0f }, "the Back End's first model")));
        Assert.Equal("1.2", hbe.FrontEndVersion);
        var (repository1, source1) = holdings["hfe-user-1"];
        Assert.Equal("1.1", repository1.VersionOf(hfe));
        Assert.Empty(source1.Obtained);
        one.Dispose(); two.Dispose();
        one = new HfeUserAgent(controllers["hfe-user-1"], "user-1", Orch("HFE.orch"), Path.Combine(work, "hfe-user-1", "secure-data-vault"), ServiceOf("user-1")); toDispose.Add(one);
        two = new HfeUserAgent(controllers["hfe-user-2"], "user-2", Orch("HFE.orch"), Path.Combine(work, "hfe-user-2", "secure-data-vault"), ServiceOf("user-2")); toDispose.Add(two);
        Assert.Equal("1.2", repository1.VersionOf(hfe));
        Assert.Equal([hfe, "1AIH-NNM-V1.1-I01"], source1.Obtained);
        Assert.True(repository1.Holds("1AIH-NNM-V1.1-I01"));
        Assert.Null(one.Acquire(HealthData("user-1", 190), Licence("user-1", "ahd-5", licenseTpu: false)));
        Assert.NotNull(two.Acquire(HealthData("user-2", 125), Licence("user-2", "ahd-6", licenseTpu: false)));

        // The round: the Back End asks each Front End for the model its AIM holds and averages them.
        var model = hbe.FederatedLearning("model-0", "hfe-1", "hfe-2");
        Assert.Equal("model-0-federated-1", model["MLModelObjectID"]!.ToString());
        var weights = new float[2];
        Buffer.BlockCopy(Convert.FromBase64String(model["MLModelData"]![0]!["Data"]!.ToString()), 0, weights, 0, 8);
        Assert.Equal(0.375f, weights[0], 3);                                                         // the mean of 0.25 and 0.5
        Assert.Equal(1f, weights[1], 3);

        // What the Back End put in the MPAI Store is the AIM that wraps this model, and a new version of the Front End that has it as a Sub-AIM; the next round names the AIM.
        Assert.Equal("1AIH-NNM-V1.1-I02", hbe.ModelAimId);
        Assert.Equal("1.3", hbe.FrontEndVersion);
        hbe.FederatedLearning("model-0", "hfe-1", "hfe-2");
        Assert.Equal("1AIH-NNM-V1.1-I03", hbe.ModelAimId);
        Assert.Equal("1.4", hbe.FrontEndVersion);

        // 7. user-1's Module is restarted: its Controller takes the newest Front End and its AIM. A rate Anomaly and Risk Alerting can say nothing of (one beat only) is processed by
        // the model: the AIH Data resulting from the processing, kept in the Vault, names the AIH Data processed and carries what the model found (its mean, 0.375: Yellow).
        {
            var (repository, source) = holdings["hfe-user-1"];
            const string nnm = "1AIH-NNM-V1.1-I03";
            one.Dispose();
            using var restarted = new HfeUserAgent(controllers["hfe-user-1"], "user-1", Orch("HFE.orch"), Path.Combine(work, "hfe-user-1", "secure-data-vault"), ServiceOf("user-1"));
            Assert.Equal("1.4", repository.VersionOf(hfe));
            Assert.Equal([hfe, "1AIH-NNM-V1.1-I01", hfe, nnm], source.Obtained);
            Assert.True(repository.Holds(nnm));
            Assert.Null(restarted.Acquire(HealthData("user-1", 100_000), Licence("user-1", "ahd-7", licenseTpu: false)));   // Anomaly and Risk Alerting: no alert
            JsonObject? processed = null;
            for (var i = 0; i < 20 && processed is null; i++) { processed = restarted.Retrieve("ahd-7-" + nnm); if (processed is null) Thread.Sleep(500); }
            Assert.NotNull(processed);
            Assert.Equal("ahd-7", processed!["AIHData"]!["ParentAIHDataID"]!.ToString());
            Assert.Equal("Yellow", processed["AIHData"]!["ARAData"]!["ARAData"]!["Risk"]!.ToString());
            Assert.Equal(4, source.Obtained.Count);                                                   // nothing was obtained again

            // The AIM that wraps the model has no licence (a trained NN Model has an identifier, and no licence): the Store issued none.
            Assert.Null(repository.LicenceOf(nnm));
            Assert.Empty(source.Issued);

            // The Controller holds the Metadata and no Implementation; the model is data of Access (A7): the L3 names it by its URI and pins it with its SHA-256, and the
            // Controller obtained it into its Access when the Module started.
            Assert.Empty(Directory.EnumerateFiles(Path.Combine(repository.Folder, "implementations"), "*", SearchOption.AllDirectories));
            using var held = System.Text.Json.JsonDocument.Parse(File.ReadAllText(Directory.EnumerateFiles(repository.Folder, nnm + ".json", SearchOption.AllDirectories).First()));
            Assert.Equal("ModelPath", Assert.Single(AIF.Controller.AccessEntry.ReadFrom(held.RootElement)).Setting);
        }
        // The ledger holds together, and shows who was refused.
        Assert.True(chain.Verify());
        Assert.Contains(chain.Ledger, e => e.Kind == "Confirm" && e.Requester == "tpu-1" && e.AihDataId == "ahd-2" && !e.Allowed);
    }

    // WHO STARTS WHAT. The End User starts processing of its own AIH Data (through its Health Front End); the Back End starts an audit and a processing itself - a command of its
    // operator device - and the licence terms decide: AIH Data Processing and Auditing ask the Blockchain, the workflow does not.
    [SkippableFact]
    public void TheEndUserAndTheBackEndStartFunctions()
    {
        using var hbe = BackEnd();
        var one = FrontEnd("user-1");
        var two = FrontEnd("user-2");
        foreach (var (front, user) in new[] { (one, "user-1"), (two, "user-2") })
        {
            var register = AihSamples.RegisterRequest(user); register["RegisterData"]![0]!["Request"]!["RequestedServices"] = new JsonArray(
                new JsonObject { ["Service"] = "Store" }, new JsonObject { ["Service"] = "DeIDAndAnonym" }, new JsonObject { ["Service"] = "Auditing" }, new JsonObject { ["Service"] = "AIHDataProcessing" });
            Assert.True(AihJson.RegisterResponseOf(front.Register(register))!["Success"]!.GetValue<bool>());
        }
        // user-1's licence lets the Back End (hbe-1) keep and process the AIH Data; user-2's names another Back End.
        Assert.Null(one.Acquire(HealthData("user-1", 190), Licence("user-1", "ahd-1", licenseTpu: false)));
        var other = Licence("user-2", "ahd-2", licenseTpu: false, backEndStatus: "Identified");
        ((JsonObject)other["ModelLicenceData"]!["Specific"]![0]!["BackEndLicence"]!)["BackEndID"] = "another-back-end";
        two.Acquire(HealthData("user-2", 190), other);
        Assert.True(Done(one.Upload("ahd-1")));
        Assert.True(Done(two.Upload("ahd-2")));

        // The End User starts a processing of its own AIH Data: done; of another's: refused (an End User works on its own AIH Data only).
        Assert.True(Done(one.Process("ahd-1")));
        Assert.False(Done(one.Process("ahd-2")));

        // The Back End starts a processing: done where the licence names it, refused where it does not (the Blockchain says).
        Assert.True(Done(hbe.ProcessData("ahd-1")));
        Assert.False(Done(hbe.ProcessData("ahd-2")));
        Assert.Contains(chain.Ledger, e => e.Kind == "Confirm" && e.Requester == "hbe-1" && e.AihDataId == "ahd-1" && e.Allowed);
        Assert.Contains(chain.Ledger, e => e.Kind == "Confirm" && e.Requester == "hbe-1" && e.AihDataId == "ahd-2" && !e.Allowed);

        // The End User has an AIH Data de-identified (the Back End's licence says Pseudonymised for user-1); the Back End then starts an audit of user-1's AIH Data: nothing unlicensed.
        Assert.True(AihJson.Parse(one.DeIdentify("ahd-1", "De-Identify", "Hashing").ToJsonString(), "r")["DeIDAndAnonymData"]![0]!["Response"]!["ProcessingState"]!.GetValue<bool>());
        Assert.Empty(hbe.AuditOf("user-1")["Audit"]!["Response"]!["AIHDataIDs"]!.AsArray());
        Assert.Empty(hbe.Errors);
        Assert.True(chain.Verify());
    }

    // THE THIRD-PARTY USER'S WORKFLOW (TPU.orch): it registers, asks for a processing as the licence prescribes, and gets the answer or a refusal; for any other service its own Front End refuses.
    [SkippableFact]
    public void TheThirdPartyUserRegistersAndAsksForProcessing()
    {
        using var hbe = BackEnd();
        var one = FrontEnd("user-1");
        var two = FrontEnd("user-2");
        foreach (var (front, user) in new[] { (one, "user-1"), (two, "user-2") })
            Assert.True(AihJson.RegisterResponseOf(front.Register(AihSamples.RegisterRequest(user)))!["Success"]!.GetValue<bool>());
        Assert.Null(one.Acquire(HealthData("user-1", 190), Licence("user-1", "ahd-1", licenseTpu: true)));
        two.Acquire(HealthData("user-2", 190), Licence("user-2", "ahd-2", licenseTpu: false));
        Assert.True(Done(one.Upload("ahd-1")));
        Assert.True(Done(two.Upload("ahd-2")));

        using var tpu = new TpuUserAgent(Controller("hfe-tpu-1", "FrontEnd"), "tpu-1", Orch("TPU.orch"), Path.Combine(work, "hfe-tpu-1", "secure-data-vault"), ServiceOf("tpu-1", name: "tpu-1"));
        var registered = tpu.Register(AihSamples.ThirdPartyRegisterRequest("tpu-1"));
        Assert.True(AihJson.RegisterResponseOf(registered)!["Success"]!.GetValue<bool>());
        Assert.NotNull(tpu.Tokens);
        Assert.Equal("Green", AihJson.ResponseOf(tpu.Process("ahd-1"))!["AIHDataOrAIHDataID"]![0]!["AIHData"]!["ARAData"]!["ARAData"]!["Risk"]!.ToString());   // inside its licence
        Assert.False(Done(tpu.Process("ahd-2")));                                                                                   // outside it: refused
        Assert.False(Done(tpu.Ask("DeIDAndAnonym", "ahd-1")));                                                                      // not a service of a Third-Party User
        Assert.False(Done(tpu.Ask("Auditing", "ahd-1")));
        Assert.Contains(chain.Ledger, e => e.Kind == "Confirm" && e.Requester == "tpu-1" && e.AihDataId == "ahd-1" && e.Allowed);
        Assert.Contains(chain.Ledger, e => e.Kind == "Confirm" && e.Requester == "tpu-1" && e.AihDataId == "ahd-2" && !e.Allowed);
        Assert.True(chain.Verify());
    }
}
