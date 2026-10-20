# AIH-HSP V1.1 - Health Secure Platform (AIMs)

The AIMs of the Health Front End (HFE) and the Health Back End (HBE), and the Blockchain they use.

| Folder | AIM / part |
|---|---|
| `ARA` | Anomaly and Risk Alerting (a rule on the heart rate of an ECG) |
| `MDL` | Model Licensing (Health Data + Model Licence -> AIH Data) |
| (no Storage) | There is no Storage AIM: Private Storage is part of the AIF infrastructure. AIH Data Processing keeps the AIH Data - by identifier, given back to its End User and to the Back End - through the storage API (`HDP/AihStore.cs`) |
| `HDP` | AIH Data Processing: the Front End's (forwards, alerts, trains locally) and the Back End's (registers, Tokens, Smart Contract, licence, processing); the end is the setting `Role` |
| `DIA` | De-Identification and Anonymisation (pseudonym by keyed hash; perturbation) |
| `ADT` | Auditing (asks the Blockchain whether each derived copy was licensed) |
| `HFL` | Health Federated Learning (federated averaging) |
| `Provider` | `Mpai.Aih.AihProvider`: what a Controller or an AIM host builds the AIMs from |
| `Chain` | `IBlockchain` and `HashChainBlockchain`, a test ledger (each entry holds the hash of the one before) |
| `Common` | the JSON of the Data Types (schemas/AIH1/V1.1) |

The L3 Metadata is in `AIMs/AMDs/1AIH-*-V1.1-I01.json`. The HFE and HBE are Composite AIMs of Continuous execution (their wiring has cycles); every input of the AIMs that serve requests is optional,
since each runs on whatever request reaches it. The Front End and the Back End are different AIF instances: a Front End talks to the Back End only, never to another Front End.

The Data Types leave open what the AIMs do below their Ports; what was decided, and what is asked of the author, is in the questions and decisions note (Downloads, `AIH-HSP-questions-and-decisions.md`).