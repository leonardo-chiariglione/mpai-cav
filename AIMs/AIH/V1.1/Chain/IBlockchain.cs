using System.Collections.Generic;
using System.Text.Json.Nodes;

namespace Mpai.Aih.Chain;

// WHAT THE BACK END ASKS OF A BLOCKCHAIN. Each provision of, or access to, AIH Data requires a Smart Contract stored on a Blockchain (the specification);
// the Back End asks two things of it: to make the Smart Contract of a Model Licence - which gives the identifier that goes into the AIH Data - and to confirm,
// before an operation, that the Smart Contract allows it to the one who asks. Any blockchain that can answer these two is one the Platform can use; the test
// implementation (HashChainBlockchain) is a ledger in memory.
public interface IBlockchain
{
    // Makes the Smart Contract of a Model Licence for the AIH Data it names; null if the Model Licence cannot be one (it names no licensed party, say).
    string? Make(JsonObject modelLicence, string aihDataId);

    // Whether the Smart Contract of that AIH Data allows the operation to the requester (an End User, the Back End, or a Third-Party User).
    bool Confirm(string aihDataId, string processType, string requester);

    // The Smart Contract's identifier of the AIH Data, if it has one.
    string? ContractOf(string aihDataId);

    // What the ledger holds, oldest first: the Smart Contracts made and the confirmations asked. This is what an audit reads.
    IReadOnlyList<LedgerEntry> Ledger { get; }
}

// One entry of the ledger: what happened, when, and the hash that joins it to the entry before.
public sealed record LedgerEntry(int Index, string Kind, string AihDataId, string Requester, string Detail, bool Allowed, string Time, string PreviousHash, string Hash);
