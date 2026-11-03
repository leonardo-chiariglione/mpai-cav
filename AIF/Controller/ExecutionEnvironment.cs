using System.Runtime.InteropServices;

namespace AIF.Controller;

// THE ENVIRONMENT AN AIM EXECUTES IN, as the AIM Metadata names it: the Architecture and the OperatingSystem of an Implementation (L1, Implementations), and
// of an entry of AccessSources, which is then for that environment only (a program of Access for Windows, another for Linux). Named nothing, it is for all.
public static class ExecutionEnvironment
{
    private static string Arch(string a) => a.ToLowerInvariant().Replace("-", "").Replace("_", "") switch
    {
        "x8664" or "x64" or "amd64" => "x64",
        "arm64" or "aarch64"        => "arm64",
        "x86" or "i386"             => "x86",
        _                            => a.ToLowerInvariant()
    };

    // Whether what is named is this machine: the one that executes.
    public static bool IsHere(string architecture, string operatingSystem)
    {
        var here = RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        var ok = architecture.Length == 0 || Arch(architecture) == Arch(here);
        if (operatingSystem.Length > 0)
        {
            var os = operatingSystem.ToLowerInvariant();
            ok &= os.Contains("window") ? RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                : os.Contains("linux")  ? RuntimeInformation.IsOSPlatform(OSPlatform.Linux)
                : os.Contains("mac") || os.Contains("osx") ? RuntimeInformation.IsOSPlatform(OSPlatform.OSX)
                : true;
        }
        return ok;
    }

    // Whether two entries could both apply somewhere: one of them names no environment, or both name the same.
    public static bool Overlap(string architectureA, string operatingSystemA, string architectureB, string operatingSystemB) =>
        (architectureA.Length == 0 || architectureB.Length == 0 || Arch(architectureA) == Arch(architectureB)) &&
        (operatingSystemA.Length == 0 || operatingSystemB.Length == 0 || string.Equals(operatingSystemA, operatingSystemB, StringComparison.OrdinalIgnoreCase));
}
