using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

using AIF.Security;
using AIF.SharedStorage;
using AIF.Trust;
using Mpai.Aih.Hsp;
using Xunit;

namespace Mpai.Aif.Tests;

// THE SECURE MODE OF THE FEDERATED-LEARNING DEMONSTRATION (Apps\AIH\HSP\FlDemo --secure), its pieces on the simulated TPM 2.0 (Mpai.Aih.Hsp.FlSecure): the End User's
// vault sealed, the Back End's watermark key kept in its Security API, a signed update verified, an update from unapproved code or with a bad signature refused.
// Three parties, each with a TPM of its own: the Back End (port Alone), an End User's Front End (port Host) and a Front End whose code is altered (port AlteredHost).
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
[Collection(Timing.Name)]
public class AihFlSecureTests : IDisposable
{
    private readonly string folder = Path.Combine(Path.GetTempPath(), "mpai-aih-fl-secure-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(folder, true); } catch { } }

    private static readonly Lazy<SimulatedManufacturer> manufacturer = new(() => SimulatedManufacturer.Make("the FL tests' TPM manufacturer"));
    private static string Bin => AppContext.BaseDirectory;

    [Fact]
    public void TheVaultIsSealedAndOpensOnlyWithTheApprovedCode()
    {
        using var frontEnd = new FlParty("front-end-test", Path.Combine(folder, "fe"), TpmSimulators.Host, manufacturer.Value, Bin);
        Assert.True(frontEnd.Security.Opened);
        var files = Path.Combine(folder, "vault");
        var vault = new SealedSharedStorage(new FileSharedStorage(files, "eu", "eu"), frontEnd.For("End User's vault"));
        var secret = Encoding.UTF8.GetBytes("{\"MLModelObjectID\":\"model-of-the-end-user\",\"Image\":\"pixels of a chest X-ray\"}");
        vault.MPAI_AIFM_SharedStorage_Put("nnmodel/000001", secret);
        Assert.Equal(secret, vault.MPAI_AIFM_SharedStorage_Get("nnmodel/000001"));

        // nothing of it in clear in the files
        foreach (var file in Directory.GetFiles(files))
            Assert.DoesNotContain("model-of-the-end-user", Encoding.Latin1.GetString(File.ReadAllBytes(file)), StringComparison.Ordinal);

        // a value moved to another key does not open
        var plain = new FileSharedStorage(files, "eu", "eu");
        plain.MPAI_AIFM_SharedStorage_Put("nnmodel/000002", plain.MPAI_AIFM_SharedStorage_Get("nnmodel/000001"));
        Assert.Throws<InvalidDataException>(() => vault.MPAI_AIFM_SharedStorage_Get("nnmodel/000002"));

        // with other code measured the Security API does not open, and the vault cannot be made
        frontEnd.Restart(altered: true);
        Assert.False(frontEnd.Security.Opened);
        Assert.Throws<InvalidOperationException>(() => new SealedSharedStorage(new FileSharedStorage(files, "eu", "eu"), frontEnd.For("End User's vault")));

        // with the approved code again it opens, and the value is read
        frontEnd.Restart(altered: false);
        Assert.True(frontEnd.Security.Opened);
        var again = new SealedSharedStorage(new FileSharedStorage(files, "eu", "eu"), frontEnd.For("End User's vault"));
        Assert.Equal(secret, again.MPAI_AIFM_SharedStorage_Get("nnmodel/000001"));
    }

    [Fact]
    public void AnUpdateIsAcceptedOnlyFromApprovedCodeAndWithItsSignature()
    {
        using var backEnd = new FlParty("health-back-end-test", Path.Combine(folder, "be"), TpmSimulators.Alone, manufacturer.Value, Bin);
        using var honest = new FlParty("front-end-honest", Path.Combine(folder, "honest"), TpmSimulators.Host, manufacturer.Value, Bin);
        using var altered = new FlParty("front-end-altered", Path.Combine(folder, "altered"), TpmSimulators.AlteredHost, manufacturer.Value, Bin, altered: true);
        Assert.True(backEnd.Security.Opened && honest.Security.Opened);
        Assert.True(altered.Security.Opened);                                     // sealed under its own (altered) code from its first start: it opens, and it signs

        // the Back End's watermark key: kept in its Security API, the same after a restart of the party
        var key = FlWatermarkKey.Material(backEnd.For("Back End"));
        Assert.Equal(64, key.Length);
        backEnd.Restart(altered: false);
        Assert.Equal(key, FlWatermarkKey.Material(backEnd.For("Back End")));

        var gate = new FlUpdateGate(backEnd.For("Back End"), FlCode.Policy(manufacturer.Value, FlCode.Reference(Bin)));
        gate.Enrol("honest", honest.IdentityKey, FlFrontEndSecurity.PublicKey(honest.For("Front End")));
        gate.Enrol("altered", altered.IdentityKey, FlFrontEndSecurity.PublicKey(altered.For("Front End")));
        byte[] Token(FlParty p, string from, string request) => FlFrontEndSecurity.Token(p.For("Front End"), gate.Challenge(from, request));

        var update = Encoding.UTF8.GetBytes("{\"Header\":\"AIP-MLO-V1.0\",\"MLModelObjectID\":\"model-1\"}");
        var signature = FlFrontEndSecurity.Sign(honest.For("Front End"), update);
        Assert.NotNull(signature);

        // signed by an enrolled Front End whose token proves approved code: accepted
        Assert.Null(gate.Admit("honest", "r1", Token(honest, "honest", "r1"), update, signature));

        // the same challenge does not serve twice
        var token = Token(honest, "honest", "r2");
        Assert.Null(gate.Admit("honest", "r2", token, update, signature));
        Assert.Contains("no challenge", gate.Admit("honest", "r2", token, update, signature));

        // changed on the way: the signature does not verify
        var changed = (byte[])update.Clone(); changed[^3] ^= 1;
        Assert.Contains("signature", gate.Admit("honest", "r3", Token(honest, "honest", "r3"), changed, signature));

        // not signed
        Assert.Contains("not signed", gate.Admit("honest", "r4", Token(honest, "honest", "r4"), update, null));

        // from altered code: signed with its own key, but its token does not prove approved code
        var alteredSignature = FlFrontEndSecurity.Sign(altered.For("Front End"), update);
        Assert.NotNull(alteredSignature);
        var why = gate.Admit("altered", "r5", Token(altered, "altered", "r5"), update, alteredSignature);
        Assert.NotNull(why);
        Assert.Contains("not the code approved (altered)", why);

        // the token of another Front End: refused
        Assert.NotNull(gate.Admit("honest", "r6", Token(altered, "honest", "r6"), update, signature));

        // a Front End the Back End did not enrol: refused
        Assert.Contains("not a Front End", gate.Admit("stranger", "r7", Token(honest, "stranger", "r7"), update, signature));

        // the honest Front End restarted with altered code: its Security API does not open, it cannot sign, and its token is refused
        honest.Restart(altered: true);
        Assert.Null(FlFrontEndSecurity.Sign(honest.For("Front End"), update));
        Assert.Contains("altered", gate.Admit("honest", "r8", Token(honest, "honest", "r8"), update, signature));
    }
}
