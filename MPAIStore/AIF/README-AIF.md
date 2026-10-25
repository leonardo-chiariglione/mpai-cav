# AIF V3.0 for Windows (x86-64)

The AIF of a user of the MPAI Store: the Controller, in the MPAI-MAS Service (`service\MasService.exe`), and the AIM host (`aimhost\AIF.AimHost.exe`).
It needs the .NET 10 runtime (x64). It carries no L3, no package and no model: it obtains them.

## What it does
1. Reads the L3s of the Modules it offers (a folder, `AmdDirectory`; or the Store, `L3Source` and `StoreUrl`).
2. For each AIM of a Module, obtains the package at the `ImplementationURI` of its L3 into its cache (`PackageCache`), and verifies it.
3. Obtains the data each AIM declares in its L3 (`AccessSources`: an address, a SHA-256, the terms of the data) into `AIF\Access`, once, and checks the SHA-256.
4. Runs the Module for a User Agent that asks, through the Controller API (HTTP).

## To run it
1. Unzip it where you want it.
2. Copy `mas-server.store.example.json` to `mas-server.json` and edit it: `Apps` names the Apps to offer; each is a folder `Apps\<App>` with its `app.json`
   (which names its Module) and its workflow; `AmdDirectory` is the folder of the L3s; `SettingsPath` is `aim-settings.json`.
3. `service\MasService.exe mas-server.json`

The first start of a Module obtains what its AIMs declare: speech and vision models that may be a gigabyte or more; it is done once.
