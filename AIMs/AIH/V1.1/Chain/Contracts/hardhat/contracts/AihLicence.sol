// SPDX-License-Identifier: MIT
pragma solidity ^0.8.20;

// THE SMART CONTRACT OF AIH-HSP, for experiments (a local node, a test network). It states, as rules, what the Model Licence of an AIH Data allows - the same rules
// as HashChainBlockchain, the test implementation of IBlockchain:
//   - the End User who owns the AIH Data is not bound by the licence: all is allowed to them;
//   - the Back End named by the Back End Licence may Store, may DeIDAndAnonym where the licence states Anonymised or Pseudonymised, and may do AIHDataProcessing;
//   - a Third-Party User named by a Third-Party User Licence may have the AIH Data processed (AIHDataProcessing);
//   - auditing and changing the licence are the End User's.
// Each Smart Contract made and each confirmation asked is an Entry event: this is what an audit reads. A new licence for the same AIH Data replaces the one
// before (the licence change); the version keeps the old one's Third-Party Users apart.
// NOT COMPILED OR DEPLOYED YET: compile with solc or Hardhat (see README.md in this folder). The arguments are strings only, so that the .NET adapter encodes
// them without a library.
contract AihLicence {
    struct Licence {
        uint256 version;
        string owner;
        string backEnd;
        string status;       // the AIH Data status the Back End Licence states: Identified, Anonymised, Pseudonymised
    }

    mapping(bytes32 => Licence) private licences;                          // keyed by keccak256(aihDataId)
    mapping(bytes32 => mapping(uint256 => mapping(bytes32 => bool))) private thirdParties;   // aihDataId, version, keccak256(Third-Party User)
    uint256 public entries;

    event Entry(uint256 index, string kind, string aihDataId, string requester, string detail, bool allowed, uint256 time);

    function make(string calldata aihDataId, string calldata owner, string calldata backEnd, string calldata status) external {
        require(bytes(owner).length > 0 && bytes(aihDataId).length > 0, "no owner");
        bytes32 k = keccak256(bytes(aihDataId));
        licences[k] = Licence(licences[k].version + 1, owner, backEnd, status);
        _log("Contract", aihDataId, owner, "", true);
    }

    function addThirdParty(string calldata aihDataId, string calldata thirdPartyUserId) external {
        bytes32 k = keccak256(bytes(aihDataId));
        require(licences[k].version > 0, "no contract");
        thirdParties[k][licences[k].version][keccak256(bytes(thirdPartyUserId))] = true;
    }

    // The identifier of the Smart Contract of an AIH Data: zero if there is none.
    function contractOf(string calldata aihDataId) external view returns (bytes32) {
        bytes32 k = keccak256(bytes(aihDataId));
        if (licences[k].version == 0) return bytes32(0);
        return keccak256(abi.encodePacked(aihDataId, licences[k].version));
    }

    function allows(string calldata aihDataId, string calldata processType, string calldata requester) public view returns (bool) {
        bytes32 k = keccak256(bytes(aihDataId));
        Licence storage l = licences[k];
        if (l.version == 0) return false;
        bytes32 who = keccak256(bytes(requester));
        if (who == keccak256(bytes(l.owner))) return true;
        bool isBackEnd = bytes(l.backEnd).length > 0 && who == keccak256(bytes(l.backEnd));
        bytes32 t = keccak256(bytes(processType));
        if (t == keccak256("Store")) return isBackEnd;
        if (t == keccak256("DeIDAndAnonym"))
            return isBackEnd && (keccak256(bytes(l.status)) == keccak256("Anonymised") || keccak256(bytes(l.status)) == keccak256("Pseudonymised"));
        if (t == keccak256("AIHDataProcessing")) return isBackEnd || thirdParties[k][l.version][who];
        return false;                                                          // auditing and changing the licence: the End User's
    }

    // Asks, and records that it was asked; the answer is the one allows() gives.
    function confirm(string calldata aihDataId, string calldata processType, string calldata requester) external returns (bool) {
        bool ok = allows(aihDataId, processType, requester);
        _log("Confirm", aihDataId, requester, processType, ok);
        return ok;
    }

    function _log(string memory kind, string memory aihDataId, string memory requester, string memory detail, bool ok) private {
        emit Entry(entries, kind, aihDataId, requester, detail, ok, block.timestamp);
        entries++;
    }
}
