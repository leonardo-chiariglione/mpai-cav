# AIH-HSP V1.1 - the User Agents and the workflows

`HFE.orch` and `HBE.orch` are the Workflow Descriptions of the Health Front End and the Health Back End. Each binds a device to every Port of the Module's boundary.

| File | What |
|---|---|
| `ModuleSession.cs` | runs a workflow, binds the devices (`ChannelDevice`), sends to and takes from them |
| `HfeUserAgent.cs` | the End User's: registers, acquires Health Data and gives a Model Licence, hears the alert, retrieves, uploads, asks for de-identification and audit, trains when the Back End asks |
| `HbeUserAgent.cs` | the Back End's: carries what the Module asks of the Blockchain, asks AIH Data Processing for the AIH Data De-Identification and Auditing work on and hands it to them, answers the clients (`IBackEnd`), runs a round of federated learning |
| `Trust.cs` | the security model of AIF (MPAI-PTF): the Back End is the Trust Authority; parties are enrolled, establish trust, and sign what they exchange (`BackEndTrust`, `PartyTrust`, `TrustedClient`, `TrustedBackEnd`) |
| `Demo` | `AihDemo`, the first slice narrated (AllApps, 40) |

A User Agent reaches its Module through the Controller API only. In `AihFirstSliceTests` the Controller and the Module of each end run as different processes (the Module's AIMs on an AIM host).
## On Linux

```
./build-aih.sh              # builds the demo and everything the AIH AIMs rest on (Debug; ./build-aih.sh Release)
./run-aih-demo.sh           # builds, then plays the first slice, narrated
AIH_DEMO_TRACE=1 ./run-aih-demo.sh   # the stack of a failure
```

They only wrap `dotnet build Demo/AihDemo.csproj` and `dotnet Demo/bin/<configuration>/net10.0/AihDemo.dll`, which is what `AllApps\40 AIH-HSP demo (first slice, narrated).bat` does on Windows.
Needs the .NET 10 SDK; without root, `dotnet-install.sh --channel 10.0` puts it in `~/.dotnet`. If the system has no ICU library (`apt install libicu-dev`), set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.

Tried on Ubuntu 26.04 (WSL2), .NET 10.0.402, x86-64: it builds (about 10 s, from a clean copy of the tree), and the first slice runs to the end - the Controllers and their AIM hosts as separate processes,
the Trust Authority and the parties' trust, the Smart Contracts, the Third-Party User inside and outside its licence, de-identification, audit, a round of Federated Learning, the chain.
The AIH tests (`dotnet test Test/Mpai.Aif.Tests --filter FullyQualifiedName~Aih`) pass on Linux: 97 of 100, the other 3 - `AihEthereumNodeTests` - are skipped because Hardhat is not installed in a clean copy of the tree.
The AIH projects are plain `net10.0`: nothing in them or in what they rest on is Windows-only. The Blockchain of the demo is the test chain; `EthereumBlockchain` (a real EVM node, with the contracts in
`AIMs/AIH/V1.1/Chain/Contracts`) needs Node.js and was not tried on Linux.
