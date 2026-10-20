using AIF.Controller;
using Mpai.Aif.Api;

namespace Mpai.Aif.Tests;

// A MODULE OF AIH-HSP WITH ITS CONTROLLER, the two apart: the Controller and the Module are always separate. The AIMs of the composite execute in a process of their own
// (AIF.AimHost) - the composite's L3 has them with the Relation External - and the Controller, which holds no AIM, reaches them there. The Module has the folder of its own.
public sealed class AihApart : IDisposable
{
    public static readonly string[] FrontEndAims = ["1AIH-MDL-V1.1-I01", "1AIH-HDP-V1.1-I01", "1AIH-ARA-V1.1-I01"];
    public static readonly string[] BackEndAims = ["1AIH-HDP-V1.1-I01", "1AIH-ARA-V1.1-I01", "1AIH-DIA-V1.1-I01", "1AIH-ADT-V1.1-I01", "1AIH-HFL-V1.1-I01"];

    private readonly HostProcess process;
    public ControllerApi Api { get; }

    private AihApart(HostProcess process, ControllerApi api) { this.process = process; Api = api; }

    public static AihApart Start(string work, string settings, string composite, string[] aims)
    {
        var amds = Path.Combine(work, "module-amds"); Directory.CreateDirectory(amds);
        foreach (var file in Directory.EnumerateFiles(Repository.Amds, "*.json")) File.Copy(file, Path.Combine(amds, Path.GetFileName(file)));
        var l3 = File.ReadAllText(Path.Combine(amds, composite + ".json"));
        foreach (var aim in aims)
            l3 = System.Text.RegularExpressions.Regex.Replace(l3, "(\"AIMName\"\\s*:\\s*\"" + aim + "\"\\s*,\\s*\"Relation\"\\s*:\\s*)\"Internal\"", "$1\"External\"");
        File.WriteAllText(Path.Combine(amds, composite + ".json"), l3);
        var process = new HostProcess(amds: amds, provider: Path.Combine(AppContext.BaseDirectory, "Mpai.Aih.dll") + ":Mpai.Aih.AihProvider",
                                      extra: ["--settings", settings], workingDirectory: Repository.Root);
        var api = new ControllerApi(amds, settings, _ => new AIF.Controller.CompositeProvider());
        api.Controller.AimHostKey = HostProcess.Key;
        foreach (var aim in aims) api.Controller.AimHosts[aim] = process.Address;
        return new AihApart(process, api);
    }

    public void Dispose() { Api.Dispose(); process.Dispose(); }
}