using System.Reflection;
using System.Text.Json.Nodes;
using Mpai.Aih.Chain;

namespace Mpai.Aif.Tests;

// THE TEST BLOCKCHAIN: the Smart Contract is the Model Licence read as rules; the ledger holds together.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AihBlockchainTests
{
    // A Model Licence of user-1 for ahd-1: the Back End hbe-1 may keep it (pseudonymised); tpu-1 may have it processed.
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

    [Fact]
    public void ASmartContractIsMadeOfALicenceAndLooksLikeOne()
    {
        var chain = new HashChainBlockchain();
        var id = chain.Make(Licence(), "ahd-1");
        Assert.StartsWith("sc-", id);
        Assert.Equal(id, chain.ContractOf("ahd-1"));
        Assert.Null(chain.ContractOf("ahd-2"));
    }

    [Fact]
    public void ALicenceThatIsNotOfThatAihDataOrNamesNoOneIsNotAContract()
    {
        var chain = new HashChainBlockchain();
        Assert.Null(chain.Make(Licence(), "ahd-other"));
        var empty = Licence(false); empty["ModelLicenceData"]!["Specific"] = new JsonArray();
        Assert.Null(chain.Make(empty, "ahd-1"));
    }

    [Theory]
    [InlineData("user-1", "AIHDataProcessing", true)]       // the End User is not bound by the licence
    [InlineData("user-1", "Auditing", true)]
    [InlineData("tpu-1", "AIHDataProcessing", true)]        // the licensed Third-Party User
    [InlineData("tpu-2", "AIHDataProcessing", false)]       // another one
    [InlineData("tpu-1", "Auditing", false)]                // what is not a Third-Party User's
    [InlineData("tpu-1", "Store", false)]
    [InlineData("hbe-1", "Store", true)]                    // the Back End
    [InlineData("hbe-1", "DeIDAndAnonym", true)]            // the licence states Pseudonymised
    [InlineData("hbe-1", "LicenceChange", false)]           // the End User's
    public void AnOperationIsAllowedToWhomTheContractAllows(string requester, string process, bool allowed)
    {
        var chain = new HashChainBlockchain();
        chain.Make(Licence(), "ahd-1");
        Assert.Equal(allowed, chain.Confirm("ahd-1", process, requester));
    }

    [Fact]
    public void WithoutAContractNothingIsAllowed()
    {
        Assert.False(new HashChainBlockchain().Confirm("ahd-1", "AIHDataProcessing", "user-1"));
    }

    [Fact]
    public void TheLedgerRecordsWhatHappenedAndHoldsTogether()
    {
        var chain = new HashChainBlockchain();
        chain.Make(Licence(), "ahd-1");
        chain.Confirm("ahd-1", "AIHDataProcessing", "tpu-1");
        chain.Confirm("ahd-1", "AIHDataProcessing", "tpu-2");
        var ledger = chain.Ledger;
        Assert.Equal(["Contract", "Confirm", "Confirm"], ledger.Select(e => e.Kind).ToArray());
        Assert.Equal([true, true, false], ledger.Select(e => e.Allowed).ToArray());
        Assert.Equal(ledger[0].Hash, ledger[1].PreviousHash);
        Assert.True(chain.Verify());

        // A change to what happened shows.
        var entries = (List<LedgerEntry>)typeof(HashChainBlockchain).GetField("ledger", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(chain)!;
        entries[2] = entries[2] with { Allowed = true };
        Assert.False(chain.Verify());
    }

    [Fact]
    public void ANewLicenceReplacesTheOneBefore()
    {
        var chain = new HashChainBlockchain();
        chain.Make(Licence(), "ahd-1");
        Assert.True(chain.Confirm("ahd-1", "AIHDataProcessing", "tpu-1"));
        chain.Make(Licence(withThirdParty: false), "ahd-1");                                  // the End User withdrew the Third-Party User
        Assert.False(chain.Confirm("ahd-1", "AIHDataProcessing", "tpu-1"));
    }
}
