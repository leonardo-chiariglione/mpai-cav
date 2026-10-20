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