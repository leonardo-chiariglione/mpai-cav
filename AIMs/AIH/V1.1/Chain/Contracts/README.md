# AihLicence on a local node (experiments)

`AihLicence.sol` is the Smart Contract of AIH-HSP; `EthereumBlockchain` (in the folder above) is the `IBlockchain` that talks to it over JSON-RPC.
The contract compiles (Hardhat 2, solc 0.8.20) in the hardhat folder here and has been run on a local Hardhat node by Test/Mpai.Aif.Tests/AihEthereumNodeTests.cs: the same rules as the test ledger, and the Ledger decoded from the events. 
ode_modules, rtifacts and cache are not committed: in hardhat, 
pm install and 
px hardhat compile recreate them.

1. In a PowerShell window, in an empty folder of your choice: install Hardhat (`npm init -y`, `npm install --save-dev hardhat`), put `AihLicence.sol` in `contracts/`, run `npx hardhat compile`. The compiled contract is `artifacts/contracts/AihLicence.sol/AihLicence.json`.
2. In a second window: `npx hardhat node` (a local chain on `http://127.0.0.1:8545` with unlocked test accounts; leave it running).
3. Deploy and use, from C#:

```csharp
var address = EthereumBlockchain.DeployFromArtifact("http://127.0.0.1:8545", @"...\AihLicence.json");
IBlockchain chain = EthereumBlockchain.Connect("http://127.0.0.1:8545", address);
```

`chain` replaces `HashChainBlockchain` wherever the Back End User Agent is given its Blockchain. `chain.Ledger` reads the `Entry` events (what an audit reads).

Tried on a local Hardhat node: the deployment, the contract's rules and the event decoding (`Ledger`). Not tried on a test network.

