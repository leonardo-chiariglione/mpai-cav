# MPAI-MMM V2.2 - Technologies (MMM-TEC)

The implementation of MMM-TEC V2.2 on this repository: an M-Instance, its Processes and
Items, operated through the MMM-API. The first target is the Baseline Profile and a
stripped-down Use Case 2.

| Folder | What it holds |
|---|---|
| `Api` | `MMM-API.json`, the MMM-TEC V2.2 API as OpenAPI 3.1, built from the schemas by `build_openapi.py`, with its report `MMM-API-report.txt` |

The schemas are those of `schemas/MMM4/V2.2` (`data` and `actions`) and those they refer
to; they are the published ones, from D:\AI. The API is made non-recursive when it is
built, not in the schemas:

1. a Data Type reached inside a Data Exchange Metadata is taken without its own
   `DataXMData`;
2. where a schema refers back to one it is part of, the inner reference is the
   identifier of the Item, read at `GET /api/v2.2/items/{itemID}`.

`Test/Mpai.Aif.Tests/MmmApiTests.cs` checks that no component schema reaches itself and
that every `$ref` resolves. After a change to the schemas:

```
python MMM/Api/build_openapi.py
```
