using System.Text.Json.Nodes;
using Mpai.Aih;
using Mpai.Aih.Chain;

namespace Mpai.Aif.Tests;

// THE ADAPTER TO AN ETHEREUM NODE (EthereumBlockchain, with Contracts/AihLicence.sol): the hash and the encoding are checked against known values, and the
// calls the adapter makes against a stand-in for the node. The contract itself is tried on a real local node (see Chain/Contracts/README.md), not here.
[Trait("Group", "Fast")]
[Trait("Blocks", "Yes")]
public class AihEthereumTests
{
    [Fact]
    public void Keccak256_gives_the_known_values()
    {
        Assert.Equal("c5d2460186f7233c927e7db2dcc703c0e500b653ca82273b7bfad8045d85a470", Keccak256.Hex(Keccak256.Hash("")));
        Assert.Equal("a9059cbb", EvmAbi.Selector("transfer(address,uint256)"));         // the selector of the ERC-20 transfer
        Assert.Equal("ddf252ad1be2c89b69c2b068fc378daa952ba7f163c4a11628f55a4df523b3ef", EvmAbi.Topic("Transfer(address,address,uint256)")[2..]);
        Assert.Equal(Keccak256.Hex(Keccak256.Hash(new string('a', 136))), Keccak256.Hex(Keccak256.Hash(new string('a', 136))));   // a full block pads into a second one
    }

    [Fact]
    public void Strings_are_encoded_as_offsets_then_lengths_and_bytes()
    {
        var data = EvmAbi.Encode("f(string,string)", "ab", "c");
        var bytes = EvmAbi.Bytes(data[(2 + 8)..]);
        Assert.Equal(EvmAbi.Selector("f(string,string)"), data[2..10]);
        Assert.Equal(64, (int)EvmAbi.Number(bytes, 0));                                // the first string starts after the two offsets
        Assert.Equal(128, (int)EvmAbi.Number(bytes, 1));
        Assert.Equal("ab", EvmAbi.String(bytes, 0));
        Assert.Equal("c", EvmAbi.String(bytes, 1));
    }

    // A stand-in for the node: answers to the calls the adapter makes and keeps the transactions sent.
    private sealed class Node
    {
        public List<string> Sent { get; } = [];
        public bool Answer { get; set; }
        public JsonNode? Rpc(string method, JsonArray p) => method switch
        {
            "eth_call" => (JsonNode?)(p[0]!["data"]!.GetValue<string>().StartsWith("0x" + EvmAbi.Selector("contractOf(string)"))
                ? "0x" + EvmAbi.Word(7) : "0x" + EvmAbi.Word(Answer ? 1 : 0)),
            "eth_sendTransaction" => Send(p[0]!["data"]!.GetValue<string>()),
            "eth_getTransactionReceipt" => new JsonObject { ["status"] = "0x1" },
            "eth_getLogs" => new JsonArray(),
            _ => null
        };
        private JsonNode Send(string data) { Sent.Add(data); return "0xabc"; }
    }

    [Fact]
    public void Make_sends_the_licence_then_each_Third_Party_User_and_gives_the_contract_identifier()
    {
        var node = new Node();
        var chain = new EthereumBlockchain(node.Rpc, "0xc0ffee", "0xaccount");
        var licence = AihSamples.ModelLicence("user-1", "ahd-1");
        var specific = (JsonArray)licence["ModelLicenceData"]!["Specific"]!;
        specific.Add(new JsonObject { ["ThirdPartyUserLicence"] = new JsonObject { ["ThirdPartyUserID"] = "tpu-1" } });
        specific.Add(new JsonObject { ["ThirdPartyUserLicence"] = new JsonObject { ["ThirdPartyUserID"] = "tpu-2" } });

        var id = chain.Make(licence, "ahd-1");

        Assert.StartsWith("sc-", id);
        Assert.Equal(3, node.Sent.Count);
        Assert.StartsWith("0x" + EvmAbi.Selector("make(string,string,string,string)"), node.Sent[0]);
        Assert.StartsWith("0x" + EvmAbi.Selector("addThirdParty(string,string)"), node.Sent[1]);
    }

    [Fact]
    public void Confirm_gives_the_answer_of_the_contract_and_records_that_it_was_asked()
    {
        var node = new Node { Answer = true };
        var chain = new EthereumBlockchain(node.Rpc, "0xc0ffee", "0xaccount");

        Assert.True(chain.Confirm("ahd-1", "Store", "be-1"));
        node.Answer = false;
        Assert.False(chain.Confirm("ahd-1", "Audit", "be-1"));
        Assert.Equal(2, node.Sent.Count);                                              // both are on the chain: an audit reads the refusal, too
    }
}
