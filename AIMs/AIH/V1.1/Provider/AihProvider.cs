using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.Security;
using AIF.SharedStorage;
using AIF.Store;

namespace Mpai.Aih;

// THE AIMs OF AIH-HSP V1.1, as the Controller asks for them by their AIM Instance. An AIM host builds this provider from the AIM Metadata it holds
// (AIF.AimHost --provider <assembly>:Mpai.Aih.AihProvider).
//
// TWO IMPLEMENTATIONS OF EACH AIM (2026-11-13). The L3s 1AIH-*-V1.1-I01 declare the Basic Profile; the L3s 1AIH-*-V1.1-I02 declare the Secure Profile. The code is
// the same; an I02 AIM is built only where the party it executes in gave this provider its Security API (Security, ISecurityAware: the Secure Controller, or an
// AIM host with a root of trust), opened, and each I02 AIM has a handle on it whose Owner is the AIM: AIH Data Processing keeps the AIH Data and the
// registrations in the Private Storage of the Module sealed under a key of its own (SealedRuledStorage); De-Identification and Anonymisation makes the
// pseudonyms with an HMAC key of its own that never leaves the root of trust. The other I02 AIMs keep nothing and use no key.
public sealed class AihProvider : IAimProvider, ISecurityAware
{
    // The Security API of the party where the AIMs execute, in the Secure Profile.
    public SecurityApi? Security { get; set; }

    // The Secure Implementation of an AIM (I02), and the AIM it implements (its I01 name, which the code is written for).
    public static bool IsSecure(string aimName) => aimName.StartsWith("1AIH-", StringComparison.Ordinal) && aimName.EndsWith("-V1.1-I02", StringComparison.Ordinal);
    private static string Of(string aimName) => IsSecure(aimName) ? aimName[..^2] + "01" : aimName;

    public const string Ara = "1AIH-ARA-V1.1-I01", Mdl = "1AIH-MDL-V1.1-I01", Hdp = "1AIH-HDP-V1.1-I01", Dia = "1AIH-DIA-V1.1-I01", Adt = "1AIH-ADT-V1.1-I01", Hfl = "1AIH-HFL-V1.1-I01";

    private readonly AmdStore _amds;

    private readonly AimRepository? _repository;
    public AihProvider(AmdStore amds) => _amds = amds;
    // With the Controller's repository, the model of an AIM that wraps one is the file the Store approved that the repository kept, measured when the AIM is built.
    public AihProvider(AmdStore amds, AimRepository repository) { _amds = amds; _repository = repository; }

    // A Neural Network Model wrapped as an AIM has an identifier of its own (the Store gives each model its Implementation number); its model is data of Access, given as ModelPath.
    private static bool IsModel(string aimName) => aimName.Contains(Nnm.ModelAimPublisher.Type, StringComparison.Ordinal);

    public bool CanCreate(string aimName) => Of(aimName) is Ara or Mdl or Hdp or Dia or Adt or Hfl || IsModel(aimName);

    // WHICH BINARY IMPLEMENTS AN AIM: the assembly of its processor - the one its L3 names as BinaryName (Mpai.Aih.Hdp, Mpai.Aih.Dia, ...), so that a Controller that measures
    // what it runs finds what the L3 declares (AIF V3.0 MET-3; ImplementationEvidence.Check). This provider (Mpai.Aih) only builds them.
    public string? ImplementationOf(string aimName) => Of(aimName) switch
    {
        Ara => typeof(Ara.AraAimProcessor).Assembly.Location,
        Mdl => typeof(Mdl.MdlAimProcessor).Assembly.Location,
        Hdp => typeof(Hdp.HdpAimProcessor).Assembly.Location,
        Dia => typeof(Dia.DiaAimProcessor).Assembly.Location,
        Adt => typeof(Adt.AdtAimProcessor).Assembly.Location,
        Hfl => typeof(Hfl.HflAimProcessor).Assembly.Location,
        _ when IsModel(aimName) => typeof(Nnm.NnmAimProcessor).Assembly.Location,
        _ => null
    };

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) => Create(aimName, settings, storage, null, null);

    private const string PseudonymKeyName = "pseudonym-key";
    private static string PseudonymKey(string aimName, ISharedStorage? privateStorage)
    {
        if (privateStorage is null) throw new InvalidOperationException($"{aimName} makes pseudonyms with a key: give it the setting PseudonymKey, or its Private Storage to keep one in.");
        if (privateStorage.MPAI_AIFM_SharedStorage_Exists(PseudonymKeyName)) return System.Text.Encoding.UTF8.GetString(privateStorage.MPAI_AIFM_SharedStorage_Get(PseudonymKeyName));
        var key = Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32));
        privateStorage.MPAI_AIFM_SharedStorage_Put(PseudonymKeyName, System.Text.Encoding.UTF8.GetBytes(key));
        return key;
    }

    // THE PSEUDONYMS IN THE SECURE PROFILE: HMAC-SHA-256 with a persistent key of the AIM in the Security API, generated once, with no EXPORT in its Policy - the
    // key never leaves the root of trust; its identifier is kept in a Secure Storage area of the AIM, so that the same End User keeps the same pseudonym.
    private static Func<byte[], byte[]> SecurePseudonyms(SecurityCaller security)
    {
        var key = SealedRuledStorage.KeyOf(security, "pseudonym-key", new KeyAttributes(KeyType.Hmac, 256, KeyLifetime.Persistent, Algorithm.HmacSha256, KeyUsage.SignMessage));
        return data =>
        {
            var state = new MacState();
            var mac = new byte[32];
            if (security.MPAI_AIFMAC_Sign_Setup(state, key, Algorithm.HmacSha256) != AifError.OK || security.MPAI_AIFMAC_Update(state, data) != AifError.OK
                || security.MPAI_AIFMAC_Sign_Finish(state, mac, out _) != AifError.OK)
                throw new InvalidOperationException("The Security API did not make the pseudonym.");
            return mac;
        };
    }

    // AIH Data Processing keeps AIH Data in the Private Storage of the Module (moduleStorage), the handle the Controller binds to it; the other AIMs keep nothing.
    // WHETHER AN AIM IS A SECURE IMPLEMENTATION: what its L3 declares (APIProfile Secure) - the reference L3s I02, and the model AIMs a Back End publishes in the
    // Secure Profile under its own Implementer ID; where the L3 is not at hand, its name.
    private bool DeclaresSecure(string aimName)
    {
        if (_amds.FindByAimName(aimName) is { } id)
        {
            var l3 = _amds.GetAMD(id);                                             // the store's own document: not disposed here
            if (l3.RootElement.TryGetProperty("APIProfile", out var profile) && profile.ValueKind == System.Text.Json.JsonValueKind.String)
                return profile.GetString() == "Secure";
        }
        return IsSecure(aimName);
    }

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage, ISharedStorage? privateStorage, IRuledStorage? moduleStorage)
    {
        var ports = AimPortReader.Load(_amds, aimName);
        SecurityCaller? security = null;
        if (DeclaresSecure(aimName))
        {
            if (Security is not { Opened: true } api)
                throw new InvalidOperationException($"{aimName} declares the Secure Profile: it is built only where the party it executes in gives it the Security API, opened (a Secure Controller, or an AIM host with a root of trust).");
            security = api.For(aimName);
            if (moduleStorage is not null) moduleStorage = new SealedRuledStorage(moduleStorage, security);
        }
        return Of(aimName) switch
        {
            Ara => new Ara.AraAimProcessor(aimName, ports),
            Mdl => new Mdl.MdlAimProcessor(aimName, ports),
            // The end of the Platform this AIF instance is: FrontEnd, else BackEnd.
            // It keeps AIH Data in the Private Storage of the Module, through the storage API (the Secure Data Vault of a Front End, the Secure Storage of the Back End); the Back End's identifier is a setting.
            Hdp => settings.GetValueOrDefault("Role") == "FrontEnd"
                ? new Hdp.FrontEndHdpAimProcessor(aimName, ports, moduleStorage ?? throw new InvalidOperationException($"{aimName} keeps AIH Data in the Private Storage of the Module, and the Module has none."))
                : new Hdp.BackEndHdpAimProcessor(aimName, ports, moduleStorage ?? throw new InvalidOperationException($"{aimName} keeps AIH Data in the Private Storage of the Module, and the Module has none."), settings.GetValueOrDefault("BackEndId") ?? ""),
            // The key the pseudonyms are made with: the setting PseudonymKey where the AIF instance gives one; else a random key made once and kept in the AIM's Private Storage
            // (the same End User keeps the same pseudonym after a restart). No key is written in the code (review finding A10, 2026-11-13).
            Dia when security is not null => new Dia.DiaAimProcessor(aimName, ports, SecurePseudonyms(security)),
            Dia => new Dia.DiaAimProcessor(aimName, ports, settings.GetValueOrDefault("PseudonymKey") ?? PseudonymKey(aimName, privateStorage)),
            Adt => new Adt.AdtAimProcessor(aimName, ports, settings.GetValueOrDefault("BackEndId") ?? "hbe-1"),
            Hfl => new Hfl.HflAimProcessor(aimName, ports),
            // its model is data of Access: the Controller obtained it and gives its file as the ModelPath setting (AccessSources of the L3)
            _ when IsModel(aimName) => new Nnm.NnmAimProcessor(aimName, ports, settings.GetValueOrDefault("ModelPath") ?? throw new InvalidOperationException($"{aimName} wraps a model, and its Access gave none (ModelPath).")),
            _ => throw new ArgumentException($"{aimName} is not an AIM this provider builds.")
        };
    }
}
