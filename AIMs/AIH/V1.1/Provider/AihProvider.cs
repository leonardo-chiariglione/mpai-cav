using System;
using System.Collections.Generic;

using AIF.Controller;
using AIF.SharedStorage;
using AIF.Store;

namespace Mpai.Aih;

// THE AIMs OF AIH-HSP V1.1, as the Controller asks for them by their AIM Instance. An AIM host builds this provider from the AIM Metadata it holds
// (AIF.AimHost --provider <assembly>:Mpai.Aih.AihProvider).
public sealed class AihProvider : IAimProvider
{
    public const string Ara = "1AIH-ARA-V1.1-I01", Mdl = "1AIH-MDL-V1.1-I01", Hdp = "1AIH-HDP-V1.1-I01", Dia = "1AIH-DIA-V1.1-I01", Adt = "1AIH-ADT-V1.1-I01", Hfl = "1AIH-HFL-V1.1-I01";

    private readonly AmdStore _amds;

    private readonly AimRepository? _repository;
    public AihProvider(AmdStore amds) => _amds = amds;
    // With the Controller's repository, the model of an AIM that wraps one is the file the Store approved that the repository kept, measured when the AIM is built.
    public AihProvider(AmdStore amds, AimRepository repository) { _amds = amds; _repository = repository; }

    // A Neural Network Model wrapped as an AIM has an identifier of its own (the Store gives each model its Implementation number); its model is a setting, ModelPath.
    private static bool IsModel(string aimName) => aimName.Contains(Nnm.ModelAimPublisher.Type, StringComparison.Ordinal);

    public bool CanCreate(string aimName) => aimName is Ara or Mdl or Hdp or Dia or Adt or Hfl || IsModel(aimName);

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? typeof(AihProvider).Assembly.Location : null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage)
    {
        var ports = AimPortReader.Load(_amds, aimName);
        return aimName switch
        {
            Ara => new Ara.AraAimProcessor(aimName, ports),
            Mdl => new Mdl.MdlAimProcessor(aimName, ports),
            // The end of the Platform this AIF instance is: FrontEnd, else BackEnd.
            // It keeps AIH Data in the Module's storage, through its API (the Secure Data Vault of a Front End, the Secure Storage of the Back End); the Back End's identifier is a setting.
            Hdp => settings.GetValueOrDefault("Role") == "FrontEnd"
                ? new Hdp.FrontEndHdpAimProcessor(aimName, ports, storage ?? throw new InvalidOperationException($"{aimName} keeps AIH Data in the Module's storage, and the Module has none."))
                : new Hdp.BackEndHdpAimProcessor(aimName, ports, storage ?? throw new InvalidOperationException($"{aimName} keeps AIH Data in the Module's storage, and the Module has none."), settings.GetValueOrDefault("BackEndId") ?? ""),
            // The key the pseudonyms are made with, and the identifier of this Back End: settings of the AIF instance.
            Dia => new Dia.DiaAimProcessor(aimName, ports, settings.GetValueOrDefault("PseudonymKey") ?? "mpai-aih-test-pseudonym-key"),
            Adt => new Adt.AdtAimProcessor(aimName, ports, settings.GetValueOrDefault("BackEndId") ?? "hbe-1"),
            Hfl => new Hfl.HflAimProcessor(aimName, ports),
            _ when IsModel(aimName) => new Nnm.NnmAimProcessor(aimName, ports, settings.GetValueOrDefault("ModelPath") ?? _repository?.FileOf(aimName, aimName + ".model.json") ?? throw new InvalidOperationException($"{aimName} wraps a model, and neither its settings (ModelPath) nor the repository name one.")),
            _ => throw new ArgumentException($"{aimName} is not an AIM this provider builds.")
        };
    }
}
