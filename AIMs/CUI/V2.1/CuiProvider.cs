using AIF.Controller;
using AIF.SharedStorage;

namespace Mpai.Cui;

// THE AIMs OF CUI-CPP V2.1, as the Controller asks for them by their AIM Instance. The composites - Company Assessment and Prediction and Company Performance Prediction - are
// graphs the Controller builds from their Metadata.
public sealed class CuiProvider : IAimProvider
{
    public const string Fna = "1CUI-FNA-V2.1-I01", Gva = "1CUI-GVA-V2.1-I01", Cdp = "1CUI-CDP-V2.1-I01", Oma = "1CUI-OMA-V2.1-I01", Pdp = "1CUI-PDP-V2.1-I01",
                        Rmg = "1CUI-RMG-V2.1-I01", Prp = "1CUI-PRP-V2.1-I01", Cap = "1CUI-CAP-V2.1-I01", Cpp = "1CUI-CPP-V2.1-I01";

    public bool CanCreate(string aimName) => aimName is Fna or Gva or Cdp or Oma or Pdp or Rmg or Prp;

    public string? ImplementationOf(string aimName) => CanCreate(aimName) ? typeof(CuiProvider).Assembly.Location : null;

    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage) => Create(aimName, settings, storage, null, null, null);

    // With the Access the AIM declared: the model of the Prediction AIMs is Access data, and they keep it up to date (MPAI_AIFM_Model_Update).
    public IAimProcessor Create(string aimName, IReadOnlyDictionary<string, string> settings, ISharedStorage? storage, ISharedStorage? privateStorage, IRuledStorage? moduleStorage, IAccessReader? access) => aimName switch
    {
        Fna => new FinancialAssessment(aimName),
        Gva => new GovernanceAssessment(aimName),
        Cdp => new PrimaryDefaultPrediction(aimName, new ModelSource(settings, access)),
        Oma => new OrganisationModelAssessment(aimName, new ModelSource(settings, access)),
        Pdp => new PrimaryDiscontinuityPrediction(aimName, new ModelSource(settings, access)),
        Rmg => new RiskMatrixGeneration(aimName),
        Prp => new PredictionResultPerturbation(aimName),
        _ => throw new ArgumentException($"{aimName} is not an AIM of CUI-CPP V2.1.")
    };
}
