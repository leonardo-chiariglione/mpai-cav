// NOTICE (third-party models and data). This source file is released under the BSD 3-Clause licence of this repository.
// The models and the databases it uses, and the data derived from them, are not part of it: each has its own licence, and some
// restrict use (for example to non-commercial research). Read THIRD-PARTY-NOTICES.md and the Terms of the AccessSources of the AIM.
using System.Collections.Generic;
using AIF.Controller;

namespace Mpai.Mmc.Edp;

// Plug-in for MMC-EDP-V2.5. Builds and caches its own model instance (loaded once, reused),
// with the model path taken from settings and defaulting to the current location -
// so behaviour is unchanged now and the path becomes portable via settings later.
public sealed class EdpPlugin : IAimPlugin
{
    private OllamaClient? _dep;
    public string AimName => "MMC-EDP-V2.5";
    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings) => Create(ports, settings, null);

    public IAimProcessor Create(AimPortReader ports, IReadOnlyDictionary<string, string> settings, IAccessReader? access)
    {
        var llm = _dep ??= new OllamaClient(Get(settings,"OllamaModel","llama3.1"), Get(settings,"OllamaUrl","http://127.0.0.1:11434"));
        llm.UseAccess(access);
        return new EdpAimProcessor(AimName, llm, ports);
    }
    private static string Get(IReadOnlyDictionary<string,string> s, string k, string d) => s.TryGetValue(k, out var v) && !string.IsNullOrWhiteSpace(v) ? v : d;
}
