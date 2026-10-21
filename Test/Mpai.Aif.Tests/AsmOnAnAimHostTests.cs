using System.Text.Json.Nodes;
using AIF.Controller;
using Mpai.Aif.Api;
using Mpai.Cae.Asm;
using Mpai.Core;
using Mpai.Core.OSD;
using Mpai.Providers;
using Mpai.SpatialAudio;

namespace Mpai.Aif.Tests;

// THE ASM MODULE'S AIMs IN A PROCESS OF THEIR OWN (M3217): the Controller and the Module are different processes that talk through the API. The L3 of
// Audio Scene Management places its five Audio Scene Management AIMs (Relation External); they run on an AIM host - AIF.AimHost, a program of its own,
// given the ASM provider - and the Controller reaches them through the Remote transport. Judged: the Module gives what it gives when its AIMs are built
// by the Controller (AsmModuleTests), and the Controller built none of the placed AIMs.
[Trait("Group", "Models")]
[Trait("Blocks", "Yes")]
public class AsmOnAnAimHostTests
{
    private const string Asm = AsmProvider.Module;
    // Every AIM of the Module: the five of Audio Scene Management and Text and Speech Translation, which Speech Object Editing uses.
    private static readonly string[] Placed =
    [
        AsmProvider.Aoe, AsmProvider.Ase, AsmProvider.Soe, AsmProvider.Sse, AsmProvider.Bme, "1MMC-TST-V2.5-I01"
    ];

    // The L3s of the repository, with the ASM Module's Audio Scene Management AIMs placed elsewhere.
    private static string AmdsWithAsmPlaced()
    {
        var folder = Path.Combine(Path.GetTempPath(), "asm-placed-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        foreach (var file in Directory.EnumerateFiles(Repository.Amds, "*.json")) File.Copy(file, Path.Combine(folder, Path.GetFileName(file)));
        var module = Path.Combine(folder, Asm + ".json");
        var lines = File.ReadAllLines(module);
        for (int i = 0; i < lines.Length; i++)
            if (Placed.Any(p => lines[i].Contains($"\"AIMName\": \"{p}\"")))
                lines[i] = lines[i].Replace("\"Relation\": \"Internal\"", "\"Relation\": \"External\"");
        File.WriteAllLines(module, lines);
        return folder;
    }

    private static BasicAudioObject Captured(string name, double hz) => new()
    {
        BasicAudioObjectID = name,
        BasicAudioObjectData = [new InlineAudioData(Convert.ToBase64String(SceneAudio.Wav(
            Enumerable.Range(0, 48000).Select(i => (float)(0.3 * Math.Sin(2 * Math.PI * hz * i / 48000.0))).ToArray(), 1, 48000)))],
        DescrMetadata = name
    };

    [SkippableFact]
    public void TheAudioSceneManagementAimsRunOnAnAimHost()
    {
        Skip.IfNot(Directory.Exists(Path.Combine(Repository.Root, "Models")), "Models is absent: the model files are obtained separately.");
        var amds = AmdsWithAsmPlaced();
        var work = Path.Combine(Path.GetTempPath(), "asm-host-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        var settings = Path.Combine(work, "aim-settings.json");
        File.WriteAllText(settings, File.ReadAllText(Path.Combine(Repository.Root, "AIMs", "aim-settings.json")));

        var bin = AppContext.BaseDirectory;
        using var host = new HostProcess(amds: amds, provider: Path.Combine(bin, "Mpai.Cae.Asm.dll") + ":Mpai.Cae.Asm.AsmProvider",
            extra: ["--provider", Path.Combine(bin, "Mpai.Providers.dll") + ":Mpai.Providers.LeafAimsProvider",
                    "--settings", settings],
            workingDirectory: Repository.Root);
        // The Controller holds no provider at all: every AIM of the Module is placed, so it could build none of them.
        using var api = new ControllerApi(amds, settings, _ => new CompositeProvider());
        api.Controller.AimHostKey = HostProcess.Key;
        foreach (var aim in Placed) api.Controller.AimHosts[aim] = host.Address;
        try
        {
            try { Assert.Equal(AifError.OK, api.StartFlow(Asm)); }
            catch (InvalidOperationException e) { throw new InvalidOperationException(e.Message + "\nThe AIM host said:\n" + host.Output, e); }
            api.SharedStorageInit(Asm, Path.Combine(work, "assets"));

            api.InputWrite(Asm, "OSD-BAO-V1.5", 1, MpaiJson.ToJson(Captured("low", 440)));
            var read = api.OutputRead(Asm, "OSD-BAO-V1.5", 1, 60_000);
            Assert.True(read.Error == AifError.OK, $"the sound did not come back as the open Object ({read.Error})");
            Assert.Equal("OSD-BAO-V1.5", JsonNode.Parse(read.Json!)!["Header"]!.ToString());

            // The placed AIMs ran in the host's process: the Channels between them and the Controller cross the Remote transport.
            Assert.Contains(api.ChannelAccounts(Asm), a => a.Contains("(Remote)"));
        }
        finally { api.StopFlow(Asm); }
    }
}
