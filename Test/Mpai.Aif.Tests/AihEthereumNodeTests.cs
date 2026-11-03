using System.Diagnostics;
using System.Text.Json.Nodes;
using Mpai.Aih.Chain;

namespace Mpai.Aif.Tests;

// THE SMART CONTRACT ON A REAL NODE: AihLicence.sol, compiled by Hardhat (AIMs/AIH/V1.1/Chain/Contracts/hardhat), is deployed on a local Hardhat node and EthereumBlockchain is run against it with the
// SAME rules as the test ledger (AihBlockchainTests). Skipped where the Hardhat folder, its installed packages or the compiled artifact are absent. The node is started for the test and stopped after it.
[Trait("Group", "Slow")]
[Trait("Blocks", "No")]
public sealed class AihEthereumNodeTests : IDisposable
{
    private const int Port = 18545;
    private static readonly string Folder = Path.Combine(Repository.Root, "AIMs", "AIH", "V1.1", "Chain", "Contracts", "hardhat");
    private static readonly string Artifact = Path.Combine(Folder, "artifacts", "contracts", "AihLicence.sol", "AihLicence.json");
    private Process? node;

    private string Start()
    {
        Skip.IfNot(File.Exists(Artifact) && Directory.Exists(Path.Combine(Folder, "node_modules", "hardhat")), "Hardhat, or the compiled AihLicence, is not there (see Contracts/README.md).");
        var info = new ProcessStartInfo("cmd.exe", $"/c npx hardhat node --hostname 127.0.0.1 --port {Port}") { WorkingDirectory = Folder, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        node = Process.Start(info)!;
        node.OutputDataReceived += (_, _) => { }; node.ErrorDataReceived += (_, _) => { };
        node.BeginOutputReadLine(); node.BeginErrorReadLine();
        var url = $"http://127.0.0.1:{Port}";
        var rpc = EthereumBlockchain.Http(url);
        for (var i = 0; i < 120; i++)
        {
            try { if (rpc("eth_chainId", []) is not null) return url; } catch { }
            Thread.Sleep(500);
        }
        throw new TimeoutException("The Hardhat node did not start.");
    }

    public void Dispose()
    {
        if (node is { HasExited: false }) { try { Process.Start(new ProcessStartInfo("taskkill", $"/PID {node.Id} /T /F") { CreateNoWindow = true, UseShellExecute = false })!.WaitForExit(15000); } catch { } }
        node?.Dispose();
    }

    private static JsonObject Licence(bool withThirdParty = true)
    {
        var licence = AihSamples.ModelLicence("user-1", "ahd-1");
        if (withThirdParty)
            ((JsonArray)licence["ModelLicenceData"]!["Specific"]!).Add(new JsonObject
            {
                ["ThirdPartyUserLicence"] = new JsonObject
                {
                    ["BackEndID"] = "hbe-1", ["ThirdPartyUserID"] = "tpu-1", ["AIHDataID"] = "ahd-1", ["Time"] = AihSamples.Time("t"),
                    ["LicensableEndUsers"] = new JsonArray(new JsonObject { ["UserID"] = "user-1", ["AIHDataClasses"] = new JsonArray("AIH-ECO") }),
                    ["UserTypes"] = new JsonArray("Non-Profit Entity"), ["ProcessTypes"] = "PhysiologicalRiskPrediction", ["AnomalyTypes"] = new JsonArray("Point Anomaly"), ["UsageTypes"] = "Research"
                }
            });
        return licence;
    }

    private EthereumBlockchain Deployed()
    {
        var url = Start();
        var address = EthereumBlockchain.DeployFromArtifact(url, Artifact);
        return EthereumBlockchain.Connect(url, address);
    }

    // One node for all the rules: the checks of AihBlockchainTests, in the order that leaves the ledger a known list.
    [SkippableFact]
    public void TheContractOnANodeFollowsTheSameRulesAsTheTestLedger()
    {
        var chain = Deployed();

        // a Smart Contract is made of the licence and looks like one; none for another AIH Data
        var id = chain.Make(Licence(), "ahd-1");
        Assert.StartsWith("sc-", id);
        Assert.Equal(id, chain.ContractOf("ahd-1"));
        Assert.Null(chain.ContractOf("ahd-2"));
        Assert.Null(chain.Make(Licence(), "ahd-other"));                                       // a licence that is not of that AIH Data is not a contract
        var empty = Licence(false); empty["ModelLicenceData"]!["Specific"] = new JsonArray();
        Assert.Null(chain.Make(empty, "ahd-1"));                                              // and one that names no one

        // who is allowed what (the Theory of AihBlockchainTests)
        (string Requester, string Process, bool Allowed)[] cases =
        [
            ("user-1", "AIHDataProcessing", true), ("user-1", "Auditing", true),               // the End User is not bound by the licence
            ("tpu-1", "AIHDataProcessing", true), ("tpu-2", "AIHDataProcessing", false),       // the licensed Third-Party User, and another one
            ("tpu-1", "Auditing", false), ("tpu-1", "Store", false),
            ("hbe-1", "Store", true), ("hbe-1", "DeIDAndAnonym", true), ("hbe-1", "LicenceChange", false)
        ];
        foreach (var (requester, process, allowed) in cases)
            Assert.True(allowed == chain.Confirm("ahd-1", process, requester), $"{requester} {process}");
        Assert.False(chain.Confirm("ahd-9", "AIHDataProcessing", "user-1"));                  // without a contract nothing is allowed

        // the ledger has every Contract and Confirm entry, in order, and holds together
        var ledger = chain.Ledger;
        var expected = new[] { "Contract" }.Concat(cases.Select(_ => "Confirm")).Concat(["Confirm"]).ToArray();
        Assert.Equal(expected, ledger.Select(e => e.Kind).ToArray());
        Assert.Equal(new[] { true }.Concat(cases.Select(c => c.Allowed)).Concat([false]).ToArray(), ledger.Select(e => e.Allowed).ToArray());
        for (var i = 1; i < ledger.Count; i++) Assert.Equal(ledger[i - 1].Hash, ledger[i].PreviousHash);
        Assert.Equal(ledger.Select(e => e.Index).OrderBy(i => i).ToArray(), ledger.Select(e => e.Index).ToArray());

        // a new licence replaces the one before: the End User withdrew the Third-Party User
        Assert.True(chain.Confirm("ahd-1", "AIHDataProcessing", "tpu-1"));
        chain.Make(Licence(withThirdParty: false), "ahd-1");
        Assert.False(chain.Confirm("ahd-1", "AIHDataProcessing", "tpu-1"));
    }

    // Auditing and changing the licence are the End User's only.
    [SkippableFact]
    public void AuditingAndLicenceChangeAreOnlyTheOwners()
    {
        var chain = Deployed();
        chain.Make(Licence(), "ahd-1");
        Assert.True(chain.Confirm("ahd-1", "Auditing", "user-1"));
        Assert.True(chain.Confirm("ahd-1", "LicenceChange", "user-1"));
        Assert.False(chain.Confirm("ahd-1", "Auditing", "hbe-1"));
        Assert.False(chain.Confirm("ahd-1", "LicenceChange", "tpu-1"));
    }
}
