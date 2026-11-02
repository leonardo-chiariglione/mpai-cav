using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;

using Mpai.Cui;

namespace Mpai.Cui.Tests;

// THE APP OF COMPANY PERFORMANCE PREDICTION (Apps/CUI/CPP): a User Agent that is a console program (CuiUa) and a Controller that is a program of its own (CuiController). The User Agent reads the file of a
// company, gives each datum to the Module through a device the workflow binds to a Port (by Data Type and Port Number), and prints what the Module gives; it holds no Controller, no Module and no AIM.
[Collection("cui")]
public class CompanyAppTests
{
    private static string Samples => Path.Combine(Repo.Root, "Apps", "CUI", "CPP", "Samples");
    private static string Bin(string project, string dll) => Path.Combine(Repo.Root, "Apps", "CUI", "CPP", project, "bin", "Debug", "net10.0", dll);

    // The data of a company, as a file: the Prediction Horizon and each Data Type the Module takes at a Port.
    private static JsonObject Company(bool healthy) => new()
    {
        ["PredictionHorizon"] = 12,
        ["FinancialDescriptors"] = Companies.Financial(healthy),
        ["GovernanceDescriptors"] = Companies.Governance(healthy),
        ["ClimateRiskDescriptors"] = Companies.Climate(),
        ["CyberRiskDescriptors"] = Companies.Cyber(),
        ["SecondaryRiskStatements"] = Companies.SecondaryRisks()
    };

    // The sample companies of the App are what the tests use: a file that differs is rewritten with MPAI_UPDATE_EXPECTED=1.
    [Theory]
    [InlineData("healthy.json", true)]
    [InlineData("distressed.json", false)]
    public void TheSampleCompaniesAreTheOnesTheTestsUse(string file, bool healthy)
    {
        var path = Path.Combine(Samples, file);
        var text = Company(healthy).ToJsonString(new JsonSerializerOptions { WriteIndented = true }) + "\n";
        if (!File.Exists(path) || Environment.GetEnvironmentVariable("MPAI_UPDATE_EXPECTED") == "1") { Directory.CreateDirectory(Samples); File.WriteAllText(path, text); }
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(text), JsonNode.Parse(File.ReadAllText(path))), $"{file} is not the company the tests use");
    }

    private static (int Exit, string Output, string Error) RunUa(string url, string company, string? jsonOut = null)
    {
        var info = new ProcessStartInfo("dotnet", $"\"{Bin("Ua", "CuiUa.dll")}\" --company \"{company}\" --controller {url}" + (jsonOut is null ? "" : $" --json \"{jsonOut}\"") + " --wait 60")
        { WorkingDirectory = Repo.Root, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true, CreateNoWindow = true };
        using var ua = Process.Start(info)!;
        var output = ua.StandardOutput.ReadToEndAsync();
        var error = ua.StandardError.ReadToEndAsync();
        Assert.True(ua.WaitForExit(TimeSpan.FromSeconds(120)), "the User Agent did not end");
        return (ua.ExitCode, output.Result, error.Result);
    }

    [Fact]
    public void TheUserAgentGivesACompanyToTheModuleOfAControllerThatIsAProgramOfItsOwnAndPrintsWhatItGives()
    {
        Assert.True(File.Exists(Bin("Controller", "CuiController.dll")) && File.Exists(Bin("Ua", "CuiUa.dll")), "the App is not built (Apps/CUI/CPP)");
        var url = "http://127.0.0.1:" + (5300 + Environment.ProcessId % 500);
        var model = Path.Combine(Repo.Root, "AIMs", "CUI", "V2.1", "Models", "FinancialDefault-Polish5y.json");
        var info = new ProcessStartInfo("dotnet", $"\"{Bin("Controller", "CuiController.dll")}\" --listen {url} --model \"{model}\"")
        { WorkingDirectory = Repo.Root, UseShellExecute = false, RedirectStandardOutput = true, CreateNoWindow = true };
        info.Environment["MPAI_ACCESS"] = Path.Combine(Path.GetTempPath(), "cui-app-access-" + Guid.NewGuid().ToString("N")[..8]);
        using var controller = Process.Start(info)!;
        try
        {
            var ready = new ManualResetEventSlim();
            controller.OutputDataReceived += (_, e) => { if (e.Data?.Contains("[MAS] Listening") == true) ready.Set(); };
            controller.BeginOutputReadLine();
            Assert.True(ready.Wait(TimeSpan.FromSeconds(60)), "the Controller did not come up");

            var work = Path.Combine(Path.GetTempPath(), "cui-app-" + Guid.NewGuid().ToString("N")[..8]);
            Directory.CreateDirectory(work);
            try
            {
                var distressed = RunUa(url, Path.Combine(Samples, "distressed.json"), Path.Combine(work, "distressed-out.json"));
                Assert.True(distressed.Exit == 0, $"exit {distressed.Exit}: {distressed.Error}\n{distressed.Output}");
                Assert.Contains("Primary Default probability", distressed.Output);
                Assert.Contains("Primary Discontinuity probability", distressed.Output);
                Assert.Contains("Secondary Discontinuity probability", distressed.Output);

                // What it printed is what the AIM computes with the model the Controller obtained as Access data.
                var out1 = JsonNode.Parse(File.ReadAllText(Path.Combine(work, "distressed-out.json")))!;
                var p1 = (double)out1["CUI-PDD-V2.1"]!["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultProbability"]!;
                var expected = (double)new PrimaryDefaultPrediction("direct", PdModel.Load(model)).Compute(new Dictionary<string, JsonNode>
                {
                    [CuiTypes.PredictionHorizon] = JsonValue.Create(12)!, [CuiTypes.FinancialDescriptors] = Companies.Financial(false)
                }).Single().Item2["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultProbability"]!;
                Assert.Equal(expected, p1, 4);

                var healthy = RunUa(url, Path.Combine(Samples, "healthy.json"), Path.Combine(work, "healthy-out.json"));
                Assert.True(healthy.Exit == 0, $"exit {healthy.Exit}: {healthy.Error}\n{healthy.Output}");
                var p2 = (double)JsonNode.Parse(File.ReadAllText(Path.Combine(work, "healthy-out.json")))!["CUI-PDD-V2.1"]!["PrimaryDefaultDescriptors"]![0]!["PrimaryDefaultProbability"]!;
                Assert.True(p2 < p1, $"the healthy company {p2:F3} is less likely to default than the distressed one {p1:F3}");
            }
            finally { try { Directory.Delete(work, true); } catch { } }
        }
        finally { try { controller.Kill(entireProcessTree: true); } catch { } }
    }

    [Fact]
    public void ACompanyThatGivesNoFinancialDescriptorsIsRefusedBeforeTheModuleIsAsked()
    {
        Assert.True(File.Exists(Bin("Ua", "CuiUa.dll")), "the App is not built (Apps/CUI/CPP)");
        var work = Path.Combine(Path.GetTempPath(), "cui-app-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        try
        {
            var file = Path.Combine(work, "incomplete.json");
            File.WriteAllText(file, """{ "PredictionHorizon": 12 }""");
            var (exit, _, error) = RunUa("http://127.0.0.1:1", file);
            Assert.Equal(2, exit);
            Assert.Contains("FinancialDescriptors", error);
        }
        finally { Directory.Delete(work, true); }
    }
}
