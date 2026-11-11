# Test vectors of the Bout (MPAI-AIF V3.0, MPAI-PTF V1.0)

These vectors let an Implementation check that it reads Bouts and Bout Instances exactly as MPAI-AIF V3.0 (Communication 9.1, Conformance 5) and MPAI-PTF V1.0 (Verification Pipeline 11, Conformance Testing BT-01 to BT-08) specify. The whole set is also given as `Bout-test-vectors.zip`.

The key in each set is a TEST key. It shall not be used for anything else.

## Sets

| Folder | What it is |
|---|---|
| `A-each-instance/` | Bout 32769, SigningMode EachInstance, 5 instances, each signed |
| `B-chain/` | Bout 32770, SigningMode Chain, ChainInterval 3, 7 instances; instances 3, 6 and 7 (the last) are signed |

The instances are Spatial Attitudes (OSD-OSA-V1.5) of a CAV, with the Spatial Qualifier TFA-SAQ-V1.5. The schema of the Bout is https://schemas.mpai.community/AIF/V3.0/data/Bout.json.

## Files of each set

| File | What it is |
|---|---|
| `bout.json` | The Bout, signed. Its Dictionary is in Hexadecimal ASCII |
| `bout.unsigned.json` | The Bout before signing |
| `dictionary.zstd` | The Dictionary as bytes: a Zstandard dictionary (RFC 8878, section 5) whose Dictionary_ID is the BoutID |
| `dictionary-content.canon` | The content of the Dictionary: the canonical form of a sample instance |
| `chain-start.hex` | A(0): the SHA-256 of the canonical form of the Bout without its Signature |
| `instance-k.json` | Instance k |
| `instance-k.canon` | The canonical form (PTF-JSON-CANON-V1) of instance k: the bytes that are compressed and signed |
| `instance-k.frame.hex` | Instance k as it travels: one Zstandard frame, here in Hexadecimal ASCII (on a link it is bytes) |
| `decompressed-k.canon` | The frame decompressed with the Dictionary: identical to `instance-k.canon` |
| `instances.unsigned.json` | The instances before signing |
| `test-key.private.pkcs8.hex`, `test-key.public.spki.hex` | The test key of the Controller that signed, KeyID CAV-1: ECDSA P-256 (PTF-ALGO-SIG-ECDSA-P256-SHA256), PKCS#8 and SubjectPublicKeyInfo, in Hexadecimal ASCII |

The `.json` files are written for reading. The `.canon` files are the exact bytes that are compressed, hashed and signed.

## What an Implementation shall reproduce

1. Each frame names its Dictionary: its Dictionary_ID is the BoutID (32769 or 32770), and it states its Frame_Content_Size.
2. Decompressing `instance-k.frame.hex` with `dictionary.zstd` gives exactly `instance-k.canon`.
3. Each instance validates against the schema of the Spatial Attitude (OSD V1.5).
4. H(x) is the SHA-256 of the canonical form of x without `Signature` and `Hash` (in `DataXMData.Security.Integrity`) and without `AuditChainHash` (in `DataXMData.Security`). An unsigned instance carries it in `Integrity.Hash`. A signed instance, and the Bout, carry no Hash.
5. In set B, A(k) = SHA-256(A(k-1) || H(x(k))), the two 32-byte values concatenated, from A(0) in `chain-start.hex`. A(k) equals the `AuditChainHash` of instance k.
6. The Signature of the Bout and of each signed instance verifies with the public key, over the canonical form of the object without its `Signature`.
7. The Binding of the Bout and of each instance is `<Data Type>#<Port Number>@<stamp of its writer>/<BoutID>`, for example `OSD-OSA-V1.5#1@2025-11-06T03:33:20.1030000+00:00/32769`.

Compressing an instance again need not give the same frame bytes, because a Zstandard encoder may compress differently. What is fixed is what the decoder gives back.

## Sizes (bytes)

| Set | Canonical instance | Frame |
|---|---|---|
| A, each instance signed (no Hash) | 1,657 to 1,659 | 161 to 167 |
| B, unsigned instance (with its Hash) | 1,717 to 1,719 | 166 to 170 |
| B, signed instance (no Hash) | 1,740 to 1,742 | 199 to 207 |
| Dictionary | - | 1,795 (A), 1,998 (B) |
