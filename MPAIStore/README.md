# MPAIStore

What MPAI, as a submitter, gives the MPAI Store: the L3s of the AIMs and where the code of each AIM is. The MPAI Store itself (`Store/`) only verifies
L3s, at submission and at execution; it holds no code. The code is kept by the submitter - for MPAI, as release assets of
`github.com/leonardo-chiariglione/packages` - and the L3 names where.

| Folder | What it holds |
|---|---|
| `L3s/AMQ` | the L3s of Answer to Multimodal Question for the Store: `1MMC-AMQ-V2.5-I02`, the Composite AIM (no code: it names its sub-AIMs), and the four leaf AIMs `1MMC-ASR-V2.5-I03`, `1MMC-TIQ-V2.5-I03`, `1MMC-TTS-V2.5-I03`, `1PAF-PMX-V1.6-I03`, whose `ImplementationURI` is the address of their package |
| `L3s/make-amq-l3s.py` | makes them from the -I01 L3s of `AIMs/AMDs`, given the address of the packages |
| `Packages` | the packages of the AIMs, as built (not kept in git): one zip per AIM and platform, `<ImplementationID>-win-x64.zip` |

**Making the packages:**
```
pwsh AIMs/Build-Packages.ps1 -Amds MPAIStore/L3s/AMQ -Packages <a working folder> -Ids 1MMC-ASR-V2.5-I03,1MMC-TIQ-V2.5-I03,1MMC-TTS-V2.5-I03,1PAF-PMX-V1.6-I03 -ZipTo MPAIStore/Packages
```
A package is the build of one AIM for the platform its L3 names (`win-x64` for Windows, x86-64), with the native libraries of that platform and no other.

**Pointing the L3s at the published packages** (the address is that of the release):
```
python MPAIStore/L3s/make-amq-l3s.py https://github.com/leonardo-chiariglione/packages/releases/download/amq-v2.5
```
The L3s made here name `MPAIStore/Packages` on this machine, to try them before publishing.

**Trying them:** `Test/Mpai.Aif.Tests/AmqFromPackagesTests.cs` gives the Controller only these five L3s; it fetches the packages one by one, loads the AIMs and runs AMQ.
