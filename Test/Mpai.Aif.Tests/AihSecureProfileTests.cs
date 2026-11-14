using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.RootOfTrust;
using AIF.SecureProfile;
using AIF.Security;
using AIF.Trust;
using Mpai.Aih;
using Mpai.Aih.Chain;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// AIH-HSP IN THE SECURE PROFILE (2026-11-13): the Secure L3s 1AIH-*-V1.1-I02. The Health Back End - and, in the last test, the Health Front End - runs in a Secure Controller (AIF\SecureProfile) on the
// simulated TPM 2.0 - its AIMs, Internal to the composite, execute in the same area - and its User Agent reaches it through the Non-Secure Controller after
// verifying its Attestation (MPAI_AIFU_Controller_Initialize_Secure). AIH Data Processing keeps the AIH Data and the registrations in the Private Storage of
// the Module sealed with a key of its own in the Security API; De-Identification and Anonymisation makes its pseudonyms with an HMAC key of its own that never
// leaves the root of trust. Judged: an End User registers, uploads, and has a de-identified copy made, through the split; nothing of the End User is in clear
// in the files of the Back End or in what the Non-Secure Controller relayed; after a restart of the Secure Controller the Back End still knows the End User and
// gives the AIH Data back; a Secure L3 is not built where there is no Security API.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class AihSecureProfileTests : IDisposable
{
    private const string Hbe = "1AIH-HBE-V1.1-I02";
    private readonly string work = Path.Combine(Path.GetTempPath(), "aih-secure-" + Guid.NewGuid().ToString("N"));
    private readonly HashChainBlockchain chain = new();
    private static readonly Lazy<SimulatedManufacturer> manufacturer = new(() => SimulatedManufacturer.Make());
    private static string Bin => AppContext.BaseDirectory;

    public AihSecureProfileTests() => Directory.CreateDirectory(work);
    public void Dispose() { try { Directory.Delete(work, true); } catch { } }

    private string Settings()
    {
        var file = Path.Combine(work, "aim-settings.json");
        File.WriteAllText(file, new JsonObject
        {
            ["1AIH-HDP-V1.1-I02"] = new JsonObject { ["Role"] = "BackEnd", ["BackEndId"] = "hbe-1" },
            ["1AIH-ADT-V1.1-I02"] = new JsonObject { ["BackEndId"] = "hbe-1" }
        }.ToJsonString());
        return file;
    }

    // A Module (the Back End, the Front End) in its Secure Controller, and its User Agent's client through a Non-Secure Controller.
    private sealed class SecureModule : IDisposable
    {
        public readonly int ModuleId;                                                                   // the MODULE_ID the Controller returned when it started the Module
        public readonly ControllerApi Controller;
        public readonly SecurityApi Security;
        public readonly SecureController Secure;
        public readonly FrameServer Area;
        public readonly TcpFrameLink Link;
        public readonly NonSecureController NonSecure;
        public readonly SecureControllerClient Client;

        public SecureModule(SimulatedTpm tpm, ECDsa anchor, string work, string settings, ECDsa userAgent, string module = Hbe, string storage = "secure-storage")
        {
            Security = new SecurityApi(tpm, Path.Combine(work, "security"));
            var security = Security;
            Controller = new ControllerApi(Repository.Amds, settings, store => new AihProvider(store) { Security = security });
            var served = userAgent.ExportSubjectPublicKeyInfo();
            Secure = new SecureController(Controller, Security, id => id.AsSpan().SequenceEqual(served));
            Area = new FrameServer(Secure);
            Link = new TcpFrameLink(Area.Port);
            NonSecure = new NonSecureController(Link);
            var policy = new Attestation.Policy(manufacturer.Value.Root, m => PartyCode.Approve(PartyCode.Of(Bin).ToDictionary(c => c.File, c => c.Sha256, StringComparer.Ordinal), m));
            Client = new SecureControllerClient(NonSecure, userAgent, anchor, policy);
            Assert.Equal(AifError.OK, Client.MPAI_AIFU_Controller_Initialize_Secure());
            Assert.Equal(AifError.OK, Client.StartFlow(module, out ModuleId));
            Assert.Equal(AifError.OK, Client.SharedStorageInit(ModuleId, Path.Combine(work, storage)));
        }

        public void Dispose()
        {
            Client.StopFlow(ModuleId);
            Link.Dispose(); Area.Dispose(); Secure.Dispose(); Controller.Dispose(); Security.Dispose();
        }
    }

    private static void Write(SecureModule m, string type, int n, JsonNode json) => Assert.Equal(AifError.OK, m.Client.InputWrite(m.ModuleId, type, n, json.ToJsonString(), 10_000));

    private static JsonObject Read(SecureModule m, string type, int n, int ms = 15_000)
    {
        var read = m.Client.OutputRead(m.ModuleId, type, n, ms);
        Assert.True(read.Error == AifError.OK, $"nothing at {type}#{n}: {read.Error}");
        return JsonNode.Parse(read.Json!)!.AsObject();
    }

    private static JsonObject Register(SecureModule api, string user, params string[] services)
    {
        var request = AihSamples.RegisterRequest(user);
        request["RegisterData"]![0]!["Request"]!["RequestedServices"] = new JsonArray(services.Select(s => (JsonNode)new JsonObject { ["Service"] = s }).ToArray());
        Write(api, AihJson.Register, 1, request);
        return (JsonObject)Read(api, AihJson.Register, 2)["RegisterData"]![0]!["Response"]!["Tokens"]!;
    }

    private JsonObject Contract(SecureModule api)
    {
        var asked = Read(api, AihJson.Blockchain, 1);
        var data = asked["AIHDataID"]!.GetValue<string>();
        var licence = asked["BlockchainLicenceData"]![0]!["Request"]!["ModelLicence"]!.AsObject();
        var id = chain.Make(licence, data);
        return AihJson.BlockchainResponse(asked["UserID"]!.GetValue<string>(), data, id ?? "", id is not null);
    }

    [SkippableFact]
    public void TheSecureBackEndKeepsTheEndUserSealedAndAnswersThroughTheSplit()
    {
        var (host, at) = TpmSimulators.At(TpmSimulators.Alone);
        var file = Path.Combine(work, "party.json");
        TpmParty.Provision(file, "health-back-end", host, at, manufacturer.Value, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        var (anchor, tpm) = TpmParty.Load(file);
        using var userAgent = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var settings = Settings();
        using (tpm)
        {
            PartyCode.MeasureInto(tpm, Bin);
            JsonObject tokens;
            using (var hbe = new SecureModule(tpm, anchor.PublicKey, work, settings, userAgent))
            {
                var api = hbe;
                // Registered; the AIH Data uploaded, its Smart Contract made; a de-identified copy made in the Module.
                tokens = Register(api, "user-1", "Store", "DeIDAndAnonym");
                Write(api, AihJson.Process, 1, AihBackEndModuleTests.Request("user-1", "Store", tokens, AihBackEndModuleTests.UploadData("ahd-1")));
                Write(api, AihJson.Blockchain, 1, Contract(api));
                var stored = AihJson.ResponseOf(Read(api, AihJson.Process, 2))!;
                Assert.True(stored["ProcessingStatus"]!.GetValue<bool>());
                Write(api, AihJson.DeId, 1, AihBackEndModuleTests.DeIdRequest("user-1", "ahd-1", "De-Identify", "Hashing", tokens));
                var done = Read(api, AihJson.DeId, 2);
                Assert.True(done["DeIDAndAnonymData"]![0]!["Response"]!["ProcessingState"]!.GetValue<bool>());
                Assert.Equal("ahd-1-deid", done["DeIDAndAnonymData"]![0]!["Response"]!["NewAIHDataID"]!.ToString());

                // The Non-Secure Controller relayed all of it and read nothing of the End User.
                var relayed = string.Join("\n", hbe.NonSecure.Seen.Select(Encoding.UTF8.GetString));
                foreach (var word in new[] { "user-1", "ahd-1", "AIHDataID", AihJson.Register }) Assert.DoesNotContain(word, relayed);
            }

            // Nothing of the End User in clear in the files of the Back End: the AIH Data, its copy and the registrations are sealed; no pseudonym key is kept in a file.
            foreach (var f in Directory.GetFiles(work, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("aim-settings.json") && !f.EndsWith("party.json")))
            {
                var content = Encoding.UTF8.GetString(File.ReadAllBytes(f));
                Assert.False(content.Contains("user-1") || content.Contains("AIHDataID") || content.Contains("HealthData"), $"in clear in {f}");
            }
            Assert.Empty(Directory.GetFiles(work, "pseudonym-key*", SearchOption.AllDirectories));

            // The Secure Controller started again on the same root of trust and code: the Back End still knows the End User and gives the AIH Data back.
            using (var again = new SecureModule(tpm, anchor.PublicKey, work, settings, userAgent))
            {
                Write(again, AihJson.Process, 1, AihBackEndModuleTests.Request("user-1", "Store", tokens, "ahd-1"));
                var back = AihJson.ResponseOf(Read(again, AihJson.Process, 2))!;
                Assert.True(back["ProcessingStatus"]!.GetValue<bool>());
                Assert.StartsWith("sc-", back["AIHDataOrAIHDataID"]![0]!["AIHData"]!["SmartContractID"]!.ToString());
            }
        }

        // Where there is no Security API, the Secure L3 is not built: the Module is refused with FAILED and the reason (Zero Trust and Profiles 6.3).
        using var basic = new ControllerApi(Repository.Amds, settings, store => new AihProvider(store));
        Assert.Equal(AifError.Failed, basic.StartFlow(Hbe, out _));
        Assert.Contains("declares the Secure Profile", basic.StartRefusal);
    }

    // THE SUB-AIMs OF A COMPOSITE AIM DECLARE ITS PROFILE (Zero Trust and Profiles 6.3): a Back End whose L3 declares the Basic Profile over the Secure Sub-AIMs
    // is refused before anything is built - FAILED, and the reason names the Sub-AIM and the two Profiles.
    [Fact]
    public void ACompositeWhoseSubAimsDeclareAnotherProfileIsRefused()
    {
        var amds = Path.Combine(work, "amds");
        Directory.CreateDirectory(amds);
        foreach (var file in Directory.GetFiles(Repository.Amds, "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));
        var hbe = Path.Combine(amds, Hbe + ".json");
        var text = File.ReadAllText(hbe);
        Assert.Contains("\"APIProfile\":       \"Secure\"", text);
        File.WriteAllText(hbe, text.Replace("\"APIProfile\":       \"Secure\"", "\"APIProfile\":       \"Basic\""));
        using var security = new SecurityApi(new SecurityApiTests.TestRoot(), Path.Combine(work, "security"));
        using var api = new ControllerApi(amds, Settings(), store => new AihProvider(store) { Security = security });
        Assert.Equal(AifError.Failed, api.StartFlow(Hbe, out _));
        Assert.Contains("declares the Secure Profile and its Composite AIM 1AIH-HBE-V1.1-I02 the Basic Profile", api.StartRefusal);
    }

    private const string Hfe = "1AIH-HFE-V1.1-I02";

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

    // What the End User asks of its Front End: the AIH Data of its Vault, by identifier.
    private static JsonObject? Kept(SecureModule api, string id)
    {
        var ask = new JsonObject
        {
            ["Header"] = "AIH-AHP-V1.1", ["UserID"] = "user-1", ["AIHDataProcessID"] = AihJson.NewId("ahp"), ["AIHDataProcessTime"] = AihJson.Time(),
            ["AIHDataProcess"] = new JsonArray(new JsonObject { ["Request"] = new JsonObject { ["AIHDataOrAIHDataID"] = new JsonArray(id), ["ProcessingType"] = "Store", ["Tokens"] = AihSamples.Tokens() } })
        };
        Write(api, AihJson.Process, 1, ask);
        return AihJson.ResponseOf(Read(api, AihJson.Process, 2))!["AIHDataOrAIHDataID"]![0] as JsonObject;
    }

    // THE SECURE FRONT END (1AIH-HFE-V1.1-I02) through the split: the End User's Health Data and Model Licence make AIH Data, which Anomaly and Risk Alerting
    // judges (the alert leaves at the boundary) and AIH Data Processing keeps in the Secure Data Vault, sealed; the End User's registration goes to the Back End
    // as it came. Nothing of the End User in clear in what the Non-Secure Controller relayed or in the files of the Front End; after a restart the AIH Data is
    // given back.
    [SkippableFact]
    public void TheSecureFrontEndKeepsTheVaultSealedAndAnswersThroughTheSplit()
    {
        var (host, at) = TpmSimulators.At(TpmSimulators.Alone);
        var file = Path.Combine(work, "party.json");
        TpmParty.Provision(file, "health-front-end", host, at, manufacturer.Value, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        var (anchor, tpm) = TpmParty.Load(file);
        using var userAgent = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var settings = Path.Combine(work, "aim-settings.json");
        File.WriteAllText(settings, new JsonObject { ["1AIH-HDP-V1.1-I02"] = new JsonObject { ["Role"] = "FrontEnd" } }.ToJsonString());
        using (tpm)
        {
            PartyCode.MeasureInto(tpm, Bin);
            using (var hfe = new SecureModule(tpm, anchor.PublicKey, work, settings, userAgent, Hfe, "secure-data-vault"))
            {
                var api = hfe;
                // A beat every half second (120 a minute): a risk, Yellow. The alert leaves at the boundary; the AIH Data is kept and given back.
                Write(api, AihJson.HealthData, 1, HealthData(125));
                Write(api, AihJson.ModelLicence, 1, AihSamples.ModelLicence("user-1", "ahd-1"));
                Assert.Equal("Yellow", Read(api, AihJson.AraData, 1)["ARAData"]!["Risk"]!.ToString());
                Assert.Equal("Yellow", Kept(api, "ahd-1")!["AIHData"]!["ARAData"]!["ARAData"]!["Risk"]!.ToString());

                // The registration goes to the Back End (at the boundary Output) as the End User wrote it.
                var register = AihSamples.RegisterRequest("user-1");
                Write(api, AihJson.Register, 1, register);
                Assert.Equal(register.ToJsonString(), Read(api, AihJson.Register, 1).ToJsonString());

                var relayed = string.Join('\n', hfe.NonSecure.Seen.Select(Encoding.UTF8.GetString));
                foreach (var word in new[] { "user-1", "ahd-1", "Yellow", "HealthDataData", AihJson.HealthData }) Assert.DoesNotContain(word, relayed);
            }

            foreach (var f in Directory.GetFiles(work, "*", SearchOption.AllDirectories).Where(f => !f.EndsWith("aim-settings.json") && !f.EndsWith("party.json")))
            {
                var content = Encoding.UTF8.GetString(File.ReadAllBytes(f));
                Assert.False(content.Contains("user-1") || content.Contains("AIHDataID") || content.Contains("HealthData"), $"in clear in {f}");
            }

            using (var again = new SecureModule(tpm, anchor.PublicKey, work, settings, userAgent, Hfe, "secure-data-vault"))
                Assert.Equal("Yellow", Kept(again, "ahd-1")!["AIHData"]!["ARAData"]!["ARAData"]!["Risk"]!.ToString());
        }
    }
}
