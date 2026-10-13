using System.Text.Json.Nodes;

using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Providers;

namespace Mpai.AsmStudio;

// THE USER AGENT'S LINK TO THE MODULE. The Studio is a User Agent: it holds a Controller API and nothing else of the Module. It does not hold an AIM, does not read
// or write the Shared Storage, and addresses what it gives and asks for by Data Type and Port Number, as a workflow would: the Controller decides which AIM does what.
// It uses only the calls that also cross a network (IControllerApi), so that Audio Scene Management may be offered remotely, through MPAI-MAS, as the other Apps are.
//
// Where the Module is kept and where its Shared Storage is are asked of the Controller: the Controller is the one that starts the Module and keeps its storage where the
// User says.
public sealed class ModuleLink : IDisposable
{
    public const string Module = "1CAE-ASM-V1.0-I01";

    // Data Types of the Module's boundary, by Port Number (CAE-ASM-V1.0, schemas/CAE3/V1.0/AIMs/AudioSceneManager.json).
    public const string Bao = "OSD-BAO-V1.5", Bas = "OSD-BAS-V1.5", Bso = "OSD-BSO-V1.5", Bto = "OSD-BTO-V1.5", Ucm = "CAE-UCM-V1.0", Tfu = "MMC-TFU-V1.5", Auo = "OSD-AUO-V1.5";

    private readonly IControllerApi api;
    private readonly IDisposable? owner;

    public ModuleLink(IControllerApi api, IDisposable? owner = null) { this.api = api; this.owner = owner; }

    // The Controller of this machine, with the AIMs it can make for this Module: Audio Scene Management's, and the voice's.
    public static ModuleLink InProcess()
    {
        var controller = new ControllerApi(Paths.Amds, Paths.Settings, store => new CompositeProvider(new Mpai.Cae.Asm.AsmProvider(Paths.Amds), new MadProvider(store), new MatProvider(store)));
        return new ModuleLink(controller, controller);
    }

    // The Controller starts the Module and keeps its Shared Storage where the User says.
    public void Start(string storageLocation)
    {
        Check(api.StartFlow(Module), "start the Module");
        Check(api.SharedStorageInit(Module, storageLocation), "keep the storage in " + storageLocation);
    }

    // The ports the editing exchanges leave a datum at that nobody asks for, and those of the voice: read away before an exchange, so that what is read after it is of it.
    private static readonly (string, int)[] EditingOutputs = [(Bao, 1), (Ucm, 1), (Ucm, 2), (Ucm, 3), (Ucm, 4), (Ucm, 5), (Bas, 1), (Auo, 1), ("OSD-BSS-V1.5", 1), ("OSD-BMS-V1.5", 1), (Bso, 1)];
    private static readonly (string, int)[] VoiceOutputs = [(Ucm, 6), (Bto, 1), (Bto, 2), (Tfu, 1), (Bso, 2)];

    public void DrainEditing() => Drain(EditingOutputs);
    public void DrainVoice() => Drain(VoiceOutputs);
    private void Drain((string, int)[] ports)
    {
        foreach (var (type, number) in ports)
            for (var i = 0; i < 8 && api.OutputRead(Module, type, number, 0).Error == AifError.OK; i++) { }
    }

    public void Write(string dataType, int portNumber, string json) =>
        Check(api.InputWrite(Module, dataType, portNumber, json, 60_000), $"give {dataType}:{portNumber}");

    // What the Module gave at that Port, or nothing within the time.
    public string? Read(string dataType, int portNumber, int milliseconds = 60_000)
    {
        var read = api.OutputRead(Module, dataType, portNumber, milliseconds);
        return read.Error == AifError.OK ? read.Json : null;
    }

    public AifError Status() => api.Status(Module).Error;

    private static void Check(AifError error, string what)
    {
        if (error != AifError.OK) throw new InvalidOperationException($"The Controller could not {what} ({error}).");
    }

    public void Dispose()
    {
        try { api.StopFlow(Module); } catch { }
        owner?.Dispose();
    }
}
