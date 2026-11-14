# AIH-HSP V1.1 - the User Agents and the workflows

`HFE.orch` and `HBE.orch` are the Workflow Descriptions of the Health Front End and the Health Back End. Each binds a device to every Port of the Module's boundary.

| File | What |
|---|---|
| `ModuleSession.cs` | runs a workflow, binds the devices (`ChannelDevice`), sends to and takes from them |
| `HfeUserAgent.cs` | the End User's: registers, acquires Health Data and gives a Model Licence, hears the alert, retrieves, uploads, asks for de-identification and audit; `Join` asks its Controller to join the Back End's service (`MPAI_AIFU_External_Join`) |
| `HbeUserAgent.cs` | the Back End's: carries what the Module asks of the Blockchain, starts the Back End's own audits and processings (request offered at a Port, answer read at a Port), publishes the anchor and the rendezvous of the service (`HBE.orch` makes the Controller its Trust Authority: `ask Controller to admit by`, `MPAI_AIFU_External_Authority`); starts a round of federated learning with one request for all the Front Ends - the Controllers carry the requests and the models |
| `Demo` | `AihDemo`, the first slice narrated (`AllApps\AIH\HSP demo.bat`) |

**Between the Controllers.** `HBE.orch` makes the Back End's Controller the Trust Authority of the service (`ask Controller to admit by "<policy>"`: roles BackEnd and FrontEnd, only they exchange, answers go to the client they answer), and the User Agent publishes the anchor and the rendezvous; both workflows `ask Controller to leave` on Stop. A Front End's User Agent knows the Authority its End User decided to trust (the service `AIH-HBE`, its anchor, its rendezvous, and the name the Back End calls this Front End by) and gives it to the workflow's registry; `HFE.orch` then drives the registration: it starts the Module, keeps the Vault, **joins the service by name** (`ask Controller to join "AIH-HBE" role FrontEnd`) and only then streams the End User's request. The Controllers admit, sign, verify and carry what the Module's External Ports give: the registration request and the Back End's answer (the Front End Module gives the answer, with the Tokens, to the User Agent on its Output REG#2), and the Federated Learn request and the model. The Federated Learn and Register Ports of the Front End and the Back End are External (`IsRemote` true in the L2 and the L3).

The AIH Data Process requests (an upload, a retrieval, a processing) and their answers are External Ports too: a Front End's, and a Third-Party User's - who runs a Health Front End whose User Profile makes it do what a Third-Party User does - travel between the Controllers; the Back End's User Agent does not see them. It answers the Blockchain as the Module asks, all the time (`ModuleSession.Serve`: Smart Contract, Licence Confirm, audit Licence Confirm), and it no longer keeps which AIH Data is whose: AIH Data Processing knows its AIH Data and their copies (`ParentAIHDataID`) and says which are a user's when it authorises an audit.

The de-identification and anonymisation, and the audit, requests are External Ports too (2026-11-13): the Front End's AIH Data Processing sends them to the Back End (De-ID and Anonym / Audit #1) and gives the answer (#2) to its User Agent; in the Back End, AIH Data Processing checks the Tokens and gives De-Identification and Anonymisation, and Auditing, the AIH Data on Channels. No User Agent carries anything to another, nor between the AIMs of its Module. The answers to the Back End's own requests, which concern no Controller, are what `ControllerApi.OutputRead` gives the Back End's User Agent on an External Output Port (`ExternalHub.Unrouted`). The hub matches an answer to its request by the Message's own identifier (the policy's `Correlate`) and names a Controller by `Address` (the HFEID), not by any identifier.

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

In the Secure Profile, add `--secure`: the Secure Implementations (the L3s `1AIH-*-V1.1-I02`), each Controller split into a Secure Controller on a TPM 2.0 of its own and a Non-Secure Controller that relays what it cannot read (`AIF\SecureProfile`). It needs the TPM simulator built in WSL (`~/mpai-tpm`, as for the TPM tests); the demo starts one per party on ports 2461, 2471, 2481 and 2491.

```
dotnet Apps\AIH\HSP\Demo\bin\Debug\net10.0\AihDemo.dll --secure
```

The narrated federated-learning demonstration (`FlDemo`, synthetic images, the federated-learning components called directly: no Controller, no Module) runs the same way; it needs `Test\Data\Fl\frozen-backbone.onnx`. With `--secure`, each party - the Back End and the three End Users' Front Ends - has a TPM 2.0 of its own (the WSL simulator, ports 2501, 2511, 2521 and 2531) and a Security API: the End User's vault is sealed, the Back End's watermark key is a key of its Security API, and each Front End signs the model it returns and gives its Attestation token over a challenge of the Back End, which refuses an update from code that is not approved or whose signature does not verify (`FlSecure.cs`; test `AihFlSecureTests`).

```
dotnet build Apps\AIH\HSP\FlDemo\FlDemo.csproj -nologo -v q
dotnet Apps\AIH\HSP\FlDemo\bin\Debug\net10.0\AihFlDemo.dll
dotnet Apps\AIH\HSP\FlDemo\bin\Debug\net10.0\AihFlDemo.dll --secure
```

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
