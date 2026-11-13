using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AIF.Controller;
using AIF.RootOfTrust;
using AIF.SecureProfile;
using AIF.Security;
using AIF.Trust;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// THE CONTROLLER OF THE SECURE PROFILE, SPLIT (Zero Trust and Profiles 6.2; Security API 1 and 3.1), in AIF\SecureProfile. The Secure Controller holds the
// Controller, and so the Module, and the Security API on the root of trust of the party (the simulated TPM 2.0); it is reached over a link (loopback TCP), as
// the area where secure code is executed is from the rest. The Non-Secure Controller interfaces with the User Agent and relays its frames. The User Agent
// switches the Secure Profile on (MPAI_AIFU_Controller_Initialize_Secure): it verifies the Attestation of the Secure Controller, and only then agrees a key
// with it, which the Non-Secure Controller never has. Judged: the Module answers through the split as it answers directly; the Non-Secure Controller sees
// none of the data of the Module; a frame replayed or altered, a session the Non-Secure Controller tries to open itself, a party or code the User Agent does
// not expect, are refused.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class SecureProfileSplitTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "mpai-split-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

    private static readonly Lazy<SimulatedManufacturer> manufacturer = new(() => SimulatedManufacturer.Make());
    private static string Bin => AppContext.BaseDirectory;
    private static Dictionary<string, string> Reference() => PartyCode.Of(Bin).ToDictionary(c => c.File, c => c.Sha256, StringComparer.Ordinal);
    private const string Module = "1TST-RTE-V1.0-I01";
    private const string Secret = "words of the End User that only the Module may read";

    private static ControllerApi Direct() => new(ControllerTests.Amds, Path.Combine(ControllerTests.Amds, "no-settings.json"), new TestAims());

    [Fact]
    public void TheUserAgentReachesTheModuleThroughTheSplitAndTheNonSecureControllerReadsNothing()
    {
        var (host, at) = TpmSimulators.At(TpmSimulators.Alone);
        var file = Path.Combine(Path.GetTempPath(), $"mpai-split-party-{Guid.NewGuid():N}.json");
        TpmParty.Provision(file, "secure-controller", host, at, manufacturer.Value, DateTimeOffset.UtcNow.AddHours(-1), DateTimeOffset.UtcNow.AddDays(1));
        var (anchor, tpm) = TpmParty.Load(file);
        try
        {
            PartyCode.MeasureInto(tpm, Bin);
            using var userAgent = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var served = userAgent.ExportSubjectPublicKeyInfo();
            using var security = new SecurityApi(tpm, folder);
            using var controller = Direct();
            using var secure = new SecureController(controller, security, id => id.AsSpan().SequenceEqual(served));
            using var area = new FrameServer(secure);
            using var toSecure = new TcpFrameLink(area.Port);
            var nonSecure = new NonSecureController(toSecure);
            var policy = new Attestation.Policy(manufacturer.Value.Root, m => PartyCode.Approve(Reference(), m));
            var client = new SecureControllerClient(nonSecure, userAgent, anchor.PublicKey, policy);

            // Before the Secure Profile is switched on, nothing reaches the Module.
            Assert.Equal(AifError.NotInitialized, client.Advance(Module, [new ControllerApi.Datum(ControllerTests.Text, Secret)]).Error);

            // Switched on: the Attestation of the Secure Controller verified, a key agreed. The Module answers as it answers directly.
            Assert.Equal(AifError.OK, client.MPAI_AIFU_Controller_Initialize_Secure());
            var through = client.Advance(Module, [new ControllerApi.Datum(ControllerTests.Text, Secret)]);
            Assert.Equal(AifError.OK, through.Error);
            using (var direct = Direct())
            {
                var expected = direct.Advance(Module, [new ControllerApi.Datum(ControllerTests.Text, Secret)]);
                Assert.Equal(expected.Outputs.Select(o => (o.DataType, o.PortNumber, o.Json)), through.Outputs.Select(o => (o.DataType, o.PortNumber, o.Json)));
            }
            using (var direct = Direct()) Assert.Equal(direct.Status(Module).Error, client.Status(Module).Error);        // not started: NotStarted, either way

            // The Non-Secure Controller relayed every frame and read none of the data of the Module: neither the words given nor the words returned.
            Assert.True(nonSecure.Relayed >= 3);
            var seen = string.Join("\n", nonSecure.Seen.Select(Encoding.UTF8.GetString));
            Assert.DoesNotContain(Secret, seen);
            Assert.DoesNotContain(Secret.ToUpperInvariant(), seen);
            Assert.DoesNotContain(ControllerTests.Text, seen);

            // A request replayed by the Non-Secure Controller: refused.
            var sealedRequest = nonSecure.Seen.Select(Frames.Parse).First(f => (string?)f?["Type"] == "Sealed")!;
            var replayed = Frames.Parse(nonSecure.Send(Frames.Bytes(sealedRequest)))!;
            Assert.Equal("Refused", (string?)replayed["Type"]);
            Assert.Contains("replay", (string?)replayed["Reason"]);

            // A request altered on its way: refused by the Secure Controller; the User Agent is told.
            nonSecure.Tamper = frame =>
            {
                var f = Frames.Parse(frame)!;
                if ((string?)f["Type"] != "Sealed") return frame;
                var data = Convert.FromBase64String((string)f["Data"]!);
                data[0] ^= 1;
                f["Data"] = Convert.ToBase64String(data);
                return Frames.Bytes(f);
            };
            Assert.Equal(AifError.NotTrusted, client.Pause(Module));
            Assert.Contains("altered", client.Refusal);
            nonSecure.Tamper = null;
            Assert.Equal(AifError.NotStarted, client.Status(Module).Error);                                    // the session goes on

            // A session the Non-Secure Controller tries to open with a key of its own: refused - it is not a User Agent this Controller serves.
            using (var impostor = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var own = new SecureControllerClient(nonSecure, impostor, anchor.PublicKey, policy);
                Assert.Equal(AifError.Failed, own.MPAI_AIFU_Controller_Initialize_Secure());
                Assert.Equal("not a User Agent this Controller serves", own.Refusal);
            }

            // A User Agent that expects another party, or code it does not approve: it does not switch the Secure Profile on.
            using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            {
                var wrongParty = new SecureControllerClient(nonSecure, userAgent, other, policy);
                Assert.Equal(AifError.NotTrusted, wrongParty.MPAI_AIFU_Controller_Initialize_Secure());
                Assert.Equal("the token is not signed by the identity key of the party", wrongParty.Refusal);
            }
            var altered = Reference();
            altered[altered.Keys.First(k => k.StartsWith("AIF.SecureProfile", StringComparison.Ordinal))] = new string('0', 64);
            var wrongCode = new SecureControllerClient(nonSecure, userAgent, anchor.PublicKey, new Attestation.Policy(manufacturer.Value.Root, m => PartyCode.Approve(altered, m)));
            Assert.Equal(AifError.NotTrusted, wrongCode.MPAI_AIFU_Controller_Initialize_Secure());
            Assert.Equal(AifError.NotInitialized, wrongCode.Advance(Module, [new ControllerApi.Datum(ControllerTests.Text, "x")]).Error);

            client.StopFlow(Module);
        }
        finally
        {
            tpm.Dispose();
            File.Delete(file);
        }
    }
}
