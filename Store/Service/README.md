# MPAI Store Service

Actor A of MPAI-MAS V1.0, *Architecture and Operation*: a repository of approved
L3s, with a REST API over HTTPS. Implementers register and submit L3s; the Controllers
obtain them (MPAI-AIF V3.0, MPAI Store, Workflow).

## Registering and submitting (STR-4)

An Implementer is a member of the trusted MPAI ecosystem, and becomes one in three steps:

1. **Registers** on the page `/MPAI/Store/Register` (`https://localhost:5020/MPAI/Store/Register` when the Service runs as below) and receives an **Implementer ID** and a
   **registration code**, shown once. ID 1 is MPAI itself; the others are assigned from 2.
2. **Gives the Store its key**: the Store App makes a key pair on the Implementer's computer and gives the Store the identity of the key (a PTF Cryptographic Instance Identity),
   with the registration code (`POST /MPAI/Store/Implementer/{id}/key`).
3. **MPAI approves** on the page `/MPAI/Store/Approve` (the admin key of the Store: `--AdminKey`, else the file `<Root>/admin-key.txt` made at the first start). The Store, the Trust
   Authority for Implementers, issues the Implementer a credential (MPAI-PTF Instance Credential) for that identity.

Then **each L3 is submitted with the Implementer's signature** (header `MPAI-Signature`: its signature over the canonical form of the L3, PTF-JSON-CANON-V1). The Store refuses
(403) an L3 whose Implementer ID it has not assigned or MPAI has not approved, and (401) one that is unsigned, signed by another key, or changed after it was signed. MPAI's own ID 1
submits with the admin key (header `MPAI-Admin`), as it is the Store; `Submit-L3s.ps1 -AdminKey` does that. Who submitted each version, and the signature, are kept with it
(`versions/<id>/<n>.submission.json`). The registrations are in `<Root>/implementers.json`, the Store's key in `<Root>/store-key.hex`.
## What happens to a submitted L3

- **Validated against the AIM Metadata schema** of AIF V3.0
  (`schemas/AIF/V3.0/data/AIMMetadata.json`): the whole L3, every field. An L3 that does not validate is
  **refused** (422), with the list of its `violations`.
- **Validated against its L2**, the standard-level instance of the AIM it implements,
  the one its `Header` names (`1MMC-AMQ-V2.5-I01` against `MMC-AMQ-V2.5`, found in
  `schemas/*/V*/AIMs`). An L3 declares only what its Implementation supports: each of
  its Ports is a Port of the L2, with the same optionality for an input; what it
  leaves out is optional in the L2; each Sub-AIM of a composite is one of the L2's,
  or a composite containing one. An L3 that is not an instance of its L2 - or has no
  L2 - is **refused**, with its `nonconformities`.
- **Its package looked for** at the `ImplementationURI` of each `Implementations`
  entry - present, with its `BinaryName`.dll: **signalled**, not refused.
- **Its fingerprints recorded** (STR-3): the SHA-256 of `BinaryName`.dll in a package inside the packages folder
  (a `file:` URI) is recorded in `<Root>/approved/<AIMName>.json` as submitted and approved. A package anywhere else, or
  at an `https:` address, is signalled and no fingerprint is recorded for it: only local packages are measured now. **The fingerprint is never given out**: a Controller measures what it obtained and\n  asks (`POST /MPAI/Store/L3/{id}/verify`), and the Store says Same, Different or NotRecorded (MPAI_AIFS_VerifyImplementation).
- **Refused** if a Sub-AIM's L3 is in neither the composite's package (which carries
  the L3 of each AIM it bundles) nor the Store - or if the L3 has no `Identifier.AIMName`.

A published L3 is never overwritten: submitting it again publishes version n+1.

## The API

| Route | |
|---|---|
| `GET /MPAI/Store/Register` | the page where an Implementer registers |
| `POST /MPAI/Store/Implementer` | register (`name`, `organisation`, `email`): `201` with the `implementerId` and the `registrationCode` |
| `POST /MPAI/Store/Implementer/{id}/key` | the Implementer gives the identity of its key, with the code |
| `GET /MPAI/Store/Implementer/{id}` | `200` with its status (Registered, KeyBound, Approved) and credential if that ID has been assigned, `404` if not |
| `GET /MPAI/Store/Approve`, `GET /MPAI/Store/Implementer`, `POST /MPAI/Store/Implementer/{id}/approve` | MPAI approves the registrations (admin key) |
| `POST /MPAI/Store/L3` | submit an L3: `201` published, with `findings`; `422` refused, with `violations`, `missing` or `nonconformities` |
| `GET /MPAI/Store/L3[?name=...]` | approved L3s, latest versions; `name` may be the standard name (`MMC-TIQ-V2.5`) |
| `GET /MPAI/Store/L3/{id}[?version=n]` | one L3; header `MPAI-Store-Version` |
| `GET /MPAI/Store/L3/{id}/versions` | its versions |
| `GET /MPAI/Store/L3/{id}/findings[?version=n]` | what the Store found in it |
| `GET /MPAI/Store/L3/{id}/implementations` | the names of the binaries it has a fingerprint of |
| `POST /MPAI/Store/L3/{id}/verify` | `{BinaryName, Fingerprint}`: is it the fingerprint approved? `Same`, `Different`, `NotRecorded` |
| `POST /MPAI/Store/Licence` | `MPAI_AIFS_GetLicence`: `{AIMName, TrustAnchorID[, ControllerID]}`. The Store sublicenses the AIM under the Implementer's `AIMLicence` in the L3 it published, at the price with its mark-up, signed with its key: `201` the first time, `200` and the same AIM Licence after; `404` if the AIM has no `AIMLicence` (it needs none) or is not in the Store; `403` with the reason if the offer does not allow sublicensing |
| `GET /MPAI/Store/Licence/{id}` | an AIM Licence the Store issued |
| `GET /MPAI/Store/Licence/{id}/status` | `MPAI_AIFS_VerifyLicence`: `valid` or `revoked`, with the reason and the time |
| `POST /MPAI/Store/Licence/{id}/revoke` | the operator revokes it: `{Reason}` (admin key, header `MPAI-Admin`) |
| `GET /MPAI/Store/Key/{keyId}` | the public key (ECDSA P-256, SPKI in hexadecimal) of the Store that signs AIM Licences; for experiments - the trust of a Controller in it comes from the Trust Anchor of the Store |

## Submitting many

`Submit-L3s.ps1` submits a set of L3s in an order that lets each composite find its
Sub-AIMs, naming in each submitted copy where its package is:

```
powershell -ExecutionPolicy Bypass -File MAS\Store\Service\Submit-L3s.ps1 -Folder D:\BI\AIMs\AMDs -Ids 1MMC-TIQ-V2.5-I01,1MMC-ASR-V2.5-I01 -Packages D:\MPAI\Packages
```

## AIM Licences

The Service issues the AIM Licence of an AIM whose L3 has an `AIMLicence` (the Implementer's licence to the Store, with `Sublicensing.Allowed`). What it issued is kept in `<Root>/licences` (one file per AIM Licence, an index, and a `.revoked.json` for what was revoked), so that a restart gives the same AIM Licence again and what was revoked stays revoked.

Options: `--MarkupPercent` (the Store's mark-up on the price the Implementer proposed, default 0), `--StoreId` (the `LicensorID`, default `mpai-store`), `--StatusMaxAge` (default `P7D`), `--PublicUrl` (the address Controllers use; it makes the `StatusURI` of each AIM Licence, default `--Urls`). The key is the Store's own (`store-key.hex`, KeyID `mpai-store-1`).

`HttpAimSource` is the Controller's side: `new HttpAimSource(storeUrl, null, trustAnchorId[, controllerId])`; `GetLicence` calls the route, `IsRevoked` calls the status route (null if the Store cannot be asked), `StoreKey(keyId)` fetches the Store's key. The caller of `POST /MPAI/Store/Licence` is not authenticated yet: it names the Trust Anchor it asks for, and the Controller proves it is under it when it checks the AIM Licence.
