# AIH-HSP V1.1 - the User Agents and the workflows

`HFE.orch` and `HBE.orch` are the Workflow Descriptions of the Health Front End and the Health Back End. Each binds a device to every Port of the Module's boundary.

| File | What |
|---|---|
| `ModuleSession.cs` | runs a workflow, binds the devices (`ChannelDevice`), sends to and takes from them |
| `HfeUserAgent.cs` | the End User's: registers, acquires Health Data and gives a Model Licence, hears the alert, retrieves, uploads, asks for de-identification and audit; `Join` asks its Controller to join the Back End's service (`MPAI_AIFU_External_Join`) |
| `HbeUserAgent.cs` | the Back End's: carries what the Module asks of the Blockchain, asks AIH Data Processing for the AIH Data De-Identification and Auditing work on and hands it to them, answers the clients (`IBackEnd`), `StartService` asks its Controller to be the Trust Authority of the service (`MPAI_AIFU_External_Authority`); starts a round of federated learning - the Controllers carry the requests and the models |
| `Trust.cs` | the security model of AIF (MPAI-PTF) for the exchanges that still go through the User Agents (registration, requests, de-identification, audit): `BackEndTrust`, `PartyTrust`, `TrustedClient`, `TrustedBackEnd`. The exchange about the models is not here any more: it is between the Controllers (below) |
| `Demo` | `AihDemo`, the first slice narrated (`AllApps\AIH\HSP demo.bat`) |

**Between the Controllers.** The Back End's User Agent asks its Controller to be the Trust Authority of the service (`StartService`: roles BackEnd and FrontEnd, only they exchange, answers go to the client they answer) and publishes the anchor and the rendezvous. A Front End's User Agent knows the Authority its End User decided to trust (the service `AIH-HBE`, its anchor, its rendezvous, and the name the Back End calls this Front End by) and gives it to the workflow's registry; `HFE.orch` then drives the registration: it starts the Module, keeps the Vault, **joins the service by name** (`ask Controller to join "AIH-HBE" role FrontEnd`) and only then streams the End User's request. The Controllers admit, sign, verify and carry what the Module's External Ports give: the registration request and the Back End's answer (the Front End Module gives the answer, with the Tokens, to the User Agent on its Output REG#2), and the Federated Learn request and the model. The Federated Learn and Register Ports of the Front End and the Back End are External (`IsRemote` true in the L2 and the L3).

The AIH Data Process requests (an upload, a retrieval, a processing) and their answers are External Ports too: a Front End's, and a Third-Party User's - who runs a Health Front End whose User Profile makes it do what a Third-Party User does - travel between the Controllers; the Back End's User Agent does not see them. It answers the Blockchain as the Module asks, all the time (`ModuleSession.Serve`: Smart Contract, Licence Confirm, audit Licence Confirm), and it no longer keeps which AIH Data is whose: AIH Data Processing knows its AIH Data and their copies (`ParentAIHDataID`) and says which are a user's when it authorises an audit.

Still through the User Agents: the de-identification and anonymisation, and the audit, requests (their Ports need the same treatment), and the Back End's own questions to AIH Data Processing (the Tokens of those requests, the AIH Data it hands to De-Identification and Auditing). The answers to those, which concern no Controller, are what `ControllerApi.OutputRead` gives the Back End's User Agent on an External Output Port (`ExternalHub.Unrouted`). The hub matches an answer to its request by the Message's own identifier (the policy's `Correlate`) and names a Controller by `Address` (the HFEID), not by any identifier.

The AIH tests start several Controllers and AIM hosts: run them at night, and one at a time (`--filter AihFirstSliceTests`); in a crowded run their timeouts can fire.

A User Agent reaches its Module through the Controller API only. In `AihFirstSliceTests` the Controller and the Module of each end run as different processes (the Module's AIMs on an AIM host).
## Building and running

Both need the .NET 10 SDK (<https://dotnet.microsoft.com/download>). The demo is built from `Demo/AihDemo.csproj`, which pulls in the nine AIH AIMs, the Chain, the User Agents and what they rest on.

### On Windows

From the root of the tree (PowerShell or a Command Prompt):

```
dotnet build Apps\AIH\HSP\Demo\AihDemo.csproj -c Debug -nologo -v q
dotnet Apps\AIH\HSP\Demo\bin\Debug\net10.0\AihDemo.dll
```

or double-click `AllApps\AIH\HSP demo.bat`, which does the same. For Release, use `-c Release` and `bin\Release`.
The AIH tests: `dotnet test Test\Mpai.Aif.Tests --filter FullyQualifiedName~Aih`.

### On Linux

```
cd Apps/AIH/HSP
./build-aih.sh              # Debug; ./build-aih.sh Release
./run-aih-demo.sh           # builds, then plays the first slice, narrated
```

If the scripts are not executable, run `chmod +x *.sh` first. The AIH tests: `dotnet test Test/Mpai.Aif.Tests --filter FullyQualifiedName~Aih`, from the root of the tree.

`AIH_DEMO_TRACE=1 ./run-aih-demo.sh` shows the stack of a failure. What was tried on Linux follows.

The scripts only wrap `dotnet build Demo/AihDemo.csproj` and `dotnet Demo/bin/<configuration>/net10.0/AihDemo.dll`, which is what `AllApps\AIH\HSP demo.bat` does on Windows.
Needs the .NET 10 SDK; without root, `dotnet-install.sh --channel 10.0` puts it in `~/.dotnet`. If the system has no ICU library (`apt install libicu-dev`), set `DOTNET_SYSTEM_GLOBALIZATION_INVARIANT=1`.

Tried on Ubuntu 26.04 (WSL2), .NET 10.0.402, x86-64: it builds (about 10 s, from a clean copy of the tree), and the first slice runs to the end - the Controllers and their AIM hosts as separate processes,
the Trust Authority and the parties' trust, the Smart Contracts, the Third-Party User inside and outside its licence, de-identification, audit, a round of Federated Learning, the chain.
The AIH tests (`dotnet test Test/Mpai.Aif.Tests --filter FullyQualifiedName~Aih`) pass on Linux: 97 of 100, the other 3 - `AihEthereumNodeTests` - are skipped because Hardhat is not installed in a clean copy of the tree.
The AIH projects are plain `net10.0`: nothing in them or in what they rest on is Windows-only. The Blockchain of the demo is the test chain; `EthereumBlockchain` (a real EVM node, with the contracts in
`AIMs/AIH/V1.1/Chain/Contracts`) needs Node.js and was not tried on Linux.
