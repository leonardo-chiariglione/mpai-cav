# Thalia: what goes into the release

Thalia is the MAS-App: the MPAI-MAS **Service** (`MAS/Service`), the **desktop client**, the **browser client**
and its **host**, offering the Apps MAD, AMQ, MAT, MPD, ACR and MAC (`Apps/MAS-App/app.json`).

| File | What it is |
|---|---|
| `entries.txt` | the four projects Thalia is built from |
| `projects.txt` | the project folders they need (every `ProjectReference`, followed) |
| `data.txt` | what is not a project: L3 descriptors reachable from the six Apps, Apps, workflows, assets, schemas, notices |
| `Copy-Thalia.ps1` | copies exactly that, keeping the structure, from a full tree into a new folder |

`projects.txt` was derived from the tree of 2026-10-09. The script derives the list again from the
`.csproj` files of the tree you give it and reports the differences, so a newer tree is fine.

```
.\Copy-Thalia.ps1 -Source D:\DI -Dest D:\CI-new -CheckOnly    # look first
.\Copy-Thalia.ps1 -Source D:\DI -Dest D:\CI-new               # then copy
```

It writes only to `-Dest`, which must be new or empty, and never deletes. Not copied: `bin`, `obj`, `.vs`,
`*.user`, `*.pdb`, `SharedStorage`, `Models`, `TestData`. The biometric gallery, with Leonardo's descriptors
removed, is added in a separate step. After the copy it lists any file that names `D:\DI` or `D:\CI`.

## Tests

`Test/Mpai.Aif.Tests` is not in the manifest: it references 19 more projects (MMM, CAV Recordings, CAE and
others), 65 projects in all. Either those travel with Thalia, or the tests Thalia needs are split out.
That is open.

## Not verified

- The script has not been run on Windows. Run `-CheckOnly` first.
- Nothing has been built from the copy yet; the first check is `dotnet build` of the four entry projects in `-Dest`.
- Projects loaded by name at run time (not by `ProjectReference`) would be missed: the smoke test of each App finds them.
