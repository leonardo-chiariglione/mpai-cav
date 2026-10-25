using System.Runtime.InteropServices;
using System.Text.Json;

using AIF.Controller;

namespace Mpai.Aif.Tests;

// AN ENTRY OF ACCESSSOURCES MAY BE FOR ONE ENVIRONMENT (AIM Metadata L1: OperatingSystem and Architecture, as in Implementations). A program of Access
// has an entry for each environment, with the same Name and Setting; the Controller considers only the one for the machine where the AIM executes, so
// a Linux server never obtains the program of Windows. An entry that names no environment is for all (the models, the databases).
[Trait("Group", "Fast")]
[Trait("Blocks", "No")]
public class AccessEnvironmentTests
{
    private const string Terms = "\"Terms\": {\"Licence\": \"test\", \"Uri\": \"https://example.org/terms\"}";
    private static string Entry(string name, string os, string arch, string sha, string setting = "ExecutablePath") =>
        "{\"Name\": \"" + name + "\", " + (os.Length > 0 ? "\"OperatingSystem\": \"" + os + "\", " : "") + (arch.Length > 0 ? "\"Architecture\": \"" + arch + "\", " : "") +
        "\"Uri\": \"https://example.org/" + sha[..4] + ".zip\", \"Sha256\": \"" + sha + "\", \"Setting\": \"" + setting + "\", " + Terms + "}";

    private static readonly string ShaA = new string('A', 64), ShaB = new string('B', 64), ShaC = new string('C', 64);

    private static JsonElement Aim(params string[] entries) => JsonDocument.Parse("{\"AccessSources\": [" + string.Join(",", entries) + "]}").RootElement.Clone();

    private static string Here => RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "Windows" : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "Linux" : "MacOS";
    private static string Other => Here == "Windows" ? "Linux" : "Windows";

    [Fact]
    public void OnlyTheEntriesForThisMachineApply()
    {
        var aim = Aim(Entry("program", Here, "x86-64", ShaA), Entry("program", Other, "x86-64", ShaB), Entry("model", "", "", ShaC, "ModelPath"));
        Assert.Equal(3, AccessEntry.ReadFrom(aim).Count);
        var here = AccessEntry.ReadForThisMachine(aim);
        Assert.Equal(["program", "model"], here.Select(e => e.Name));
        Assert.Equal(ShaA, here[0].Sha256);
    }

    [Fact]
    public void AnEntryForAnotherArchitectureDoesNotApply()
    {
        var aim = Aim(Entry("program", Here, RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "x86-64" : "ARM64", ShaA));
        Assert.Empty(AccessEntry.ReadForThisMachine(aim));
    }

    [Fact]
    public void OneNameAndSettingForEachEnvironmentIsConsistent()
    {
        var aim = Aim(Entry("program", "Windows", "x86-64", ShaA), Entry("program", "Linux", "x86-64", ShaB), Entry("program", "Linux", "ARM64", ShaC));
        Assert.Equal("", AccessEntry.Check(AccessEntry.ReadFrom(aim)));
    }

    [Fact]
    public void TheSameNameOrSettingFortheSameEnvironmentIsRefused()
    {
        Assert.Contains("more than one entry", AccessEntry.Check(AccessEntry.ReadFrom(Aim(Entry("program", "Linux", "", ShaA), Entry("program", "Linux", "", ShaB)))));
        Assert.Contains("more than one entry", AccessEntry.Check(AccessEntry.ReadFrom(Aim(Entry("program", "", "", ShaA), Entry("program", "Linux", "", ShaB)))));
        Assert.Contains("Setting", AccessEntry.Check(AccessEntry.ReadFrom(Aim(Entry("a", "Linux", "", ShaA), Entry("b", "Linux", "", ShaB)))));
    }

    [Fact]
    public void TheProgramsOfTheReferenceAimsAreForWindows()
    {
        foreach (var (l3, name) in new[] { ("1MMC-ASR-V2.5-I01.json", "asr-program"), ("1MMC-ASR-V2.5-I01.json", "asr-server"), ("1MMC-TTS-V2.5-I01.json", "tts-program") })
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(Repository.Amds, l3)));
            var e = Assert.Single(AccessEntry.ReadFrom(doc.RootElement), x => x.Name == name);
            Assert.Equal("Windows", e.OperatingSystem);
            Assert.Equal("x86-64", e.Architecture);
            Assert.Equal("", AccessEntry.Check(AccessEntry.ReadFrom(doc.RootElement)));
        }
    }
}
