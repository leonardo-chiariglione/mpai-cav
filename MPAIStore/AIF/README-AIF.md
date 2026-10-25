# AIF V3.0 for Windows (x86-64)

The AIF of a user of the MPAI Store: the Controller, in the MPAI-MAS Service (`service\MasService.exe`), and the AIM host (`aimhost\AIF.AimHost.exe`).
It needs the .NET 10 runtime (x64) and, for the speech programs it obtains, the Microsoft Visual C++ runtime (x64), which most machines have. It carries no L3, no package and
no model: it obtains them, so the first start needs an Internet connection and disk space (a gigabyte or more).

## What it does
1. Reads the L3s of the Modules it offers (a folder, `AmdDirectory`; or the Store, `L3Source` and `StoreUrl`).
2. For each AIM of a Module, obtains the package at the `ImplementationURI` of its L3 into its cache (`PackageCache`), and verifies it.
3. Obtains the data each AIM declares in its L3 (`AccessSources`: an address, a SHA-256, the terms of the data) into `AIF\Access`, once, and checks the SHA-256.
4. Runs the Module for a User Agent that asks, through the Controller API (HTTP).

## Where the workflows are
A Module is run by a User Agent, and the User Agent follows the **workflow** of the App: a Workflow Description (`.orch`) that says what is acquired, what is offered to the Module, what is
asked of it and what is presented. The workflow is not part of the Controller and not of the L3 of the Module. It is in the App's folder, `Apps\<App>`, beside its `app.json` (which names the Module).
The Service lists the Apps of its configuration and hands the workflow of the one a client chooses to that client, which interprets it. The Apps of MPAI for the Store are in `MPAIStore\Apps`.
A Module without an App folder can be run by a User Agent that has its own workflow, but the Service offers nothing for it.

## To run it
1. Unzip it where you want it.
2. Copy `mas-server.store.example.json` to `mas-server.json` and edit it: `Apps` names the Apps to offer; each is a folder `Apps\<App>` with its `app.json`
   (which names its Module) and its workflow; `AmdDirectory` is the folder of the L3s; `SettingsPath` is `aim-settings.json`.
3. `service\MasService.exe mas-server.json`

The first start of a Module obtains what its AIMs declare: speech and vision models that may be a gigabyte or more; it is done once.

## Where it keeps what it obtains
- The data of Access (models, voices, programs): `%LOCALAPPDATA%\MPAI\AIF\Access`. Another place: the environment variable `MPAI_ACCESS`.
- The packages of the AIMs: `PackageCache` of the configuration.
Nothing is written in the folder where the AIF is unzipped, which may be read-only. The folder may have any name and be anywhere, spaces and accents included.

## The address
The example listens on `http://127.0.0.1:5105/` - this machine only, no certificate needed. To serve others, set `ListenUrl`, a certificate and a bearer token: the Service refuses to
answer outside this machine without them.

## The Apps that talk (the conversations)
The Apps with a dialogue (MCA, AMC) need a language model served by Ollama on this machine (`http://127.0.0.1:11434`); AMQ, NPR and MTS do not.
