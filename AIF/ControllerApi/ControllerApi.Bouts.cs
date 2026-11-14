using System;
using System.Collections.Generic;
using System.Threading;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using AIF.Bout;
using AIF.Controller;
using AIF.Trust;

namespace Mpai.Aif.Api;

// BOUTS AT THE BOUNDARY OF A MODULE (MPAI-AIF V3.0, Basic API 3.1.3 and 3.10; Communication 9.1).
//   Output: the User Agent asks the Controller to give the Messages of a boundary Output Port in a Bout. The Controller makes the Bout from the first
//           Message of the Port and signs it; that Message is read as a full instance, and the following as Bout Instances (BoutOutputRead).
//   Input:  a User Agent holding an Instance Credential of this Controller (CredentialObtain, with its Cryptographic Instance Identity as proof) gives
//           a Bout for a boundary Input Port; the Controller accepts it (PTF AcceptBout) and checks and decompresses each Bout Instance before the AIMs
//           read it.
// When the Controller is destroyed the credentials are revoked and the Bouts given with them closed.
public sealed partial class ControllerApi
{
    private readonly ECDsa _boutKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly string _boutKeyId = "urn:mpai:aif:controller:" + Guid.NewGuid().ToString("N");
    private BoutCredentials? _boutCredentials;
    private readonly Dictionary<string, BoutGiver> _outputBouts = new(StringComparer.Ordinal);
    private readonly Dictionary<string, BoutReceiver> _inputBouts = new(StringComparer.Ordinal);
    private int _nextBout = 32768 + RandomNumberGenerator.GetInt32(1 << 20);

    private BoutCredentials Credentials => _boutCredentials ??= new BoutCredentials(new PtfIssuer(_boutKeyId, _boutKey, _boutKeyId));

    private static string BoutKey(string moduleName, string dataType, int portNumber) => $"{moduleName}/{dataType}#{portNumber}";

    // The Trust Anchor of this Controller (PTF-TRA): with it a User Agent verifies the Bouts the Controller gives.
    public JsonObject BoutAnchor() => new TrustAnchorKey(_boutKeyId, _boutKey, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddYears(10)).Object();

    // ---------------------------------------------------------------- 3.1.3 MPAI_AIFU_Credential_Obtain
    public AifError CredentialObtain(JsonObject proof, out JsonObject? credential)
    {
        (credential, var reason) = Credentials.Issue(proof, BoutCredentials.UserAgent);
        return credential is null ? AifError.NotTrusted : AifError.OK;
    }

    private void RevokeBoutCredentials()
    {
        _boutCredentials?.RevokeAll();
        lock (_tables) _inputBouts.Clear();
    }

    // ---------------------------------------------------------------- 3.10.1 MPAI_AIFU_Bout_Open
    public AifError BoutOpen(int moduleId, string dataType, int portNumber, string signingMode, int chainInterval, out int boutId)
    {
        var moduleName = NameOf(moduleId);
        boutId = 0;
        lock (_tables)
        {
            if (!_running.TryGetValue(moduleName, out var started) || started.Stopped) return AifError.NotStarted;
            if (PortOf(started, "Output", dataType, portNumber) is not { } port) return AifError.NoSuchPort;
            if (!port.Bout) return AifError.NotAuthorised;
            if (signingMode is not (BoutSecurity.EachInstance or BoutSecurity.Chain) || chainInterval < 1) return AifError.Failed;
            var key = BoutKey(moduleName, dataType, portNumber);
            if (_outputBouts.TryGetValue(key, out var previous) && previous.Bout is not null) previous.Close();
            boutId = Interlocked.Increment(ref _nextBout);
            _outputBouts[key] = new BoutGiver(new BoutGiver.Options
            {
                DataType = dataType, PortNumber = portNumber, BoutId = boutId, SigningMode = signingMode, ChainInterval = chainInterval,
                Key = _boutKey, KeyId = _boutKeyId, Issuer = _boutKeyId, SourceAimId = moduleName
            });
            return AifError.OK;
        }
    }

    // MPAI_AIFU_MODULE_Output_Read while a Bout is in force: the first Message of the Port, from which the Bout is made, is returned as Json; the
    // following ones as Bout Instances (Frame). Without a Bout, as OutputRead.
    public (AifError Error, string? Json, byte[]? Frame) BoutOutputRead(int moduleId, string dataType, int portNumber, int timeoutMs = -1)
    {
        var moduleName = NameOf(moduleId);
        var read = OutputRead(moduleId, dataType, portNumber, timeoutMs);
        if (read.Error != AifError.OK || read.Json is null) return (read.Error, read.Json, null);
        BoutGiver? giver;
        lock (_tables) _outputBouts.TryGetValue(BoutKey(moduleName, dataType, portNumber), out giver);
        if (giver is null || JsonNode.Parse(read.Json) is not JsonObject instance) return (AifError.OK, read.Json, null);
        lock (giver)
        {
            if (giver.Bout is null) { giver.Open(instance); return (AifError.OK, read.Json, null); }
            return (AifError.OK, null, giver.Instance(instance));
        }
    }

    // ---------------------------------------------------------------- 3.10.2 MPAI_AIFU_Bout_Get
    public AifError BoutGet(int moduleId, string dataType, int portNumber, out JsonObject? bout)
    {
        var moduleName = NameOf(moduleId);
        lock (_tables) bout = _outputBouts.TryGetValue(BoutKey(moduleName, dataType, portNumber), out var giver) ? giver.Bout : null;
        return bout is null ? AifError.NotFound : AifError.OK;
    }

    // ---------------------------------------------------------------- 3.10.3 MPAI_AIFU_Bout_Close
    public AifError BoutClose(int moduleId, string dataType, int portNumber, out JsonObject? closeBout)
    {
        var moduleName = NameOf(moduleId);
        closeBout = null;
        BoutGiver? giver;
        lock (_tables)
        {
            if (!_outputBouts.Remove(BoutKey(moduleName, dataType, portNumber), out giver)) return AifError.NotFound;
        }
        if (giver.Bout is null) return AifError.NotFound;
        lock (giver) closeBout = giver.Close();
        return AifError.OK;
    }

    // ---------------------------------------------------------------- 3.10.4 MPAI_AIFU_Bout_Give
    public AifError BoutGive(int moduleId, string dataType, int portNumber, JsonObject bout)
    {
        var moduleName = NameOf(moduleId);
        lock (_tables)
        {
            if (!_running.TryGetValue(moduleName, out var started) || started.Stopped) return AifError.NotStarted;
            if (PortOf(started, "Input", dataType, portNumber) is not { } port) return AifError.NoSuchPort;
            if (BoutSecurity.KeyIdOf(bout) is not { } signer || Credentials.KeyFor(signer) is null) return AifError.NotTrusted;
            if (!port.Bout) return AifError.NotAuthorised;
            var key = BoutKey(moduleName, dataType, portNumber);
            if (!_inputBouts.TryGetValue(key, out var receiver))
                _inputBouts[key] = receiver = new BoutReceiver(new BoutReceiver.Options
                {
                    DataType = dataType, PortNumber = portNumber, KeyFor = Credentials.KeyFor, MayGive = Credentials.MayGive
                });
            return receiver.Accept(bout).Ok ? AifError.OK : AifError.NotAuthorised;
        }
    }

    // MPAI_AIFU_MODULE_Input_Write while a Bout is in force: the Bout Instance is checked and decompressed, and each instance it delivers - one, or in a
    // Chain the instances of a segment that verified - is written to the Port.
    public AifError BoutInputWrite(int moduleId, string dataType, int portNumber, byte[] frame, int timeoutMs = -1)
    {
        var moduleName = NameOf(moduleId);
        BoutReceiver.Received got;
        lock (_tables)
        {
            if (!_inputBouts.TryGetValue(BoutKey(moduleName, dataType, portNumber), out var receiver)) return AifError.NotFound;
            got = receiver.Receive(frame);
        }
        if (!got.Ok) return AifError.NotAuthorised;
        foreach (var instance in got.Delivered)
            if (InputWrite(moduleId, dataType, portNumber, instance.ToJsonString(), timeoutMs) is var written and not AifError.OK) return written;
        return AifError.OK;
    }

    // ---------------------------------------------------------------- 3.10.5 MPAI_AIFU_Bout_End
    public AifError BoutEnd(int moduleId, string dataType, int portNumber, JsonObject closeBout)
    {
        var moduleName = NameOf(moduleId);
        BoutReceiver.Received ended;
        lock (_tables)
        {
            if (!_inputBouts.TryGetValue(BoutKey(moduleName, dataType, portNumber), out var receiver)) return AifError.NotFound;
            ended = receiver.Close(closeBout);
            if (ended.ErrorCode == BoutReceiver.SignatureFailure) return AifError.NotTrusted;
            _inputBouts.Remove(BoutKey(moduleName, dataType, portNumber));
        }
        foreach (var instance in ended.Delivered) InputWrite(moduleId, dataType, portNumber, instance.ToJsonString());
        return ended.Ok ? AifError.OK : AifError.Failed;
    }
}
