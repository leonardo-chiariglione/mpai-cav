using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json.Nodes;
using System.Threading;

namespace Mpai.Aih.Chain;

// A BLOCKCHAIN FOR EXPERIMENTS: IBlockchain over an Ethereum node (a local Hardhat or Anvil node, or a test network) reached by JSON-RPC, with the Smart Contract
// of Contracts/AihLicence.sol. The node signs the transactions of the account it holds (a local development node has unlocked accounts); no key is handled here.
// The rules are the contract's; they are those of HashChainBlockchain.
public sealed class EthereumBlockchain : IBlockchain
{
    private const string Make_ = "make(string,string,string,string)";
    private const string AddThird = "addThirdParty(string,string)";
    private const string Confirm_ = "confirm(string,string,string)";
    private const string ContractOf_ = "contractOf(string)";

    private readonly Func<string, JsonArray, JsonNode?> rpc;
    private readonly string contract;
    private readonly string from;

    // rpc: sends one JSON-RPC call (method, parameters) and returns its result; EthereumBlockchain.Http builds one for a node's URL.
    public EthereumBlockchain(Func<string, JsonArray, JsonNode?> rpc, string contractAddress, string fromAccount)
    {
        this.rpc = rpc; contract = contractAddress; from = fromAccount;
    }

    public static EthereumBlockchain Connect(string nodeUrl, string contractAddress, string? fromAccount = null)
    {
        var rpc = Http(nodeUrl);
        var account = fromAccount ?? rpc("eth_accounts", [])?.AsArray().FirstOrDefault()?.GetValue<string>() ?? throw new InvalidOperationException("The node has no account.");
        return new EthereumBlockchain(rpc, contractAddress, account);
    }

    // Publishes the contract: bytecode is the compiled AihLicence (the "bytecode" of a Hardhat artifact, see DeployFromArtifact). Returns the contract's address.
    public static string Deploy(Func<string, JsonArray, JsonNode?> rpc, string fromAccount, string bytecode)
    {
        var hash = rpc("eth_sendTransaction", [new JsonObject { ["from"] = fromAccount, ["data"] = bytecode.StartsWith("0x") ? bytecode : "0x" + bytecode }])!.GetValue<string>();
        return Receipt(rpc, hash)["contractAddress"]?.GetValue<string>() ?? throw new InvalidOperationException("The deployment gave no address.");
    }

    public static string DeployFromArtifact(string nodeUrl, string artifactPath, string? fromAccount = null)
    {
        var rpc = Http(nodeUrl);
        var account = fromAccount ?? rpc("eth_accounts", [])!.AsArray()[0]!.GetValue<string>();
        var bytecode = JsonNode.Parse(File.ReadAllText(artifactPath))!["bytecode"]!.GetValue<string>();
        return Deploy(rpc, account, bytecode);
    }

    public static Func<string, JsonArray, JsonNode?> Http(string nodeUrl)
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        var id = 0;
        return (method, parameters) =>
        {
            var body = new JsonObject { ["jsonrpc"] = "2.0", ["id"] = ++id, ["method"] = method, ["params"] = parameters.DeepClone() };
            using var content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            var answer = JsonNode.Parse(client.PostAsync(nodeUrl, content).GetAwaiter().GetResult().Content.ReadAsStringAsync().GetAwaiter().GetResult())!;
            if (answer["error"] is JsonNode error) throw new InvalidOperationException(method + ": " + error["message"]);
            return answer["result"];
        };
    }

    public string? Make(JsonObject modelLicence, string aihDataId)
    {
        var owner = modelLicence["EndUserID"]?.GetValue<string>() ?? "";
        var specific = (modelLicence["ModelLicenceData"]?["Specific"] as JsonArray)?.OfType<JsonObject>().ToList() ?? [];
        var backEnd = specific.Select(s => s["BackEndLicence"] as JsonObject).FirstOrDefault(b => b is not null);
        var third = specific.Select(s => s["ThirdPartyUserLicence"] as JsonObject).Where(t => t is not null).Select(t => t!["ThirdPartyUserID"]?.GetValue<string>() ?? "").Where(i => i.Length > 0);
        if (owner.Length == 0 || (modelLicence["AIHDataID"]?.GetValue<string>() ?? aihDataId) != aihDataId || specific.Count == 0) return null;

        Send(EvmAbi.Encode(Make_, aihDataId, owner, backEnd?["BackEndID"]?.GetValue<string>() ?? "", backEnd?["AIHDataStatus"] is JsonValue s ? s.GetValue<string>() : ""));
        foreach (var party in third.Distinct(StringComparer.Ordinal)) Send(EvmAbi.Encode(AddThird, aihDataId, party));
        return ContractOf(aihDataId);
    }

    public bool Confirm(string aihDataId, string processType, string requester)
    {
        var data = EvmAbi.Encode(Confirm_, aihDataId, processType, requester);
        var answer = EvmAbi.Bytes(Call(data));                                     // what the contract answers now ...
        Send(data);                                                                // ... and the entry it records
        return EvmAbi.Bool(answer, 0);
    }

    public string? ContractOf(string aihDataId)
    {
        var id = EvmAbi.Bytes(Call(EvmAbi.Encode(ContractOf_, aihDataId)));
        return id.Length < 32 || id.All(b => b == 0) ? null : "sc-" + Keccak256.Hex(id)[..12];
    }

    // What the contract recorded, oldest first. Hash is the hash of the transaction that recorded it, PreviousHash that of the entry before.
    public IReadOnlyList<LedgerEntry> Ledger
    {
        get
        {
            var logs = rpc("eth_getLogs", [new JsonObject { ["fromBlock"] = "0x0", ["toBlock"] = "latest", ["address"] = contract, ["topics"] = new JsonArray(EvmAbi.Topic(EvmAbi.EntrySignature)) }])?.AsArray() ?? [];
            var entries = new List<LedgerEntry>();
            var previous = new string('0', 64);
            foreach (var log in logs.OrderBy(l => Convert.ToInt64(l!["blockNumber"]!.GetValue<string>(), 16)).ThenBy(l => Convert.ToInt64(l!["logIndex"]!.GetValue<string>(), 16)))
            {
                var e = EvmAbi.DecodeEntry(log!["data"]!.GetValue<string>());
                var hash = log["transactionHash"]!.GetValue<string>()[2..];
                entries.Add(new LedgerEntry(e.Index, e.Kind, e.AihDataId, e.Requester, e.Detail, e.Allowed, DateTimeOffset.FromUnixTimeSeconds(e.Time).ToString("yyyy-MM-ddTHH:mm:ssZ"), previous, hash));
                previous = hash;
            }
            return entries;
        }
    }

    private string Call(string data) =>
        rpc("eth_call", [new JsonObject { ["from"] = from, ["to"] = contract, ["data"] = data }, "latest"])!.GetValue<string>();

    private void Send(string data)
    {
        var hash = rpc("eth_sendTransaction", [new JsonObject { ["from"] = from, ["to"] = contract, ["data"] = data }])!.GetValue<string>();
        var receipt = Receipt(rpc, hash);
        if (receipt["status"]?.GetValue<string>() == "0x0") throw new InvalidOperationException("The contract refused the transaction " + hash);
    }

    private static JsonNode Receipt(Func<string, JsonArray, JsonNode?> rpc, string hash)
    {
        for (var i = 0; i < 100; i++)                                              // a development node mines at once; a test network takes seconds
        {
            if (rpc("eth_getTransactionReceipt", [hash]) is JsonNode receipt) return receipt;
            Thread.Sleep(200);
        }
        throw new TimeoutException("No receipt for the transaction " + hash);
    }
}
