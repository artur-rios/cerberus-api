# Cerberus v1 protocol review input

Status: **Pending independent security review and real-client interoperability review.**

The owner approved a reference design and its Java/Python implementation plan. The
reference harness now provides executed public-fixture evidence. Owner approval,
code review, cross-language agreement and benchmarks are not independent security
review or satisfaction of NFR-11/IR-09. The approval manifest remains pending;
`scripts/verify_protocol.py` must block dependent work and releases.

The [complete byte contract and threat model](../superpowers/specs/2026-10-07-protocol-harness-design.md)
remain the detailed design input. [The plan](../superpowers/plans/2026-10-07-protocol-harness.md)
traces requirements and rejection cases. This document consolidates the implemented
reference behavior and the changes from the earlier proposal. No business route,
registration contract or endpoint inventory is changed by this harness.

## Threat boundary and key roles

Clients hold plaintext, passwords, recovery secrets and usable vault keys. A database,
backup or server holding identity credentials and lease-signing keys must not decrypt
vault material. Online identity/permission enforcement remains a production obligation.
Clients must independently authenticate recipient and author fingerprints; an API key
directory is not a trust anchor. An authorized recipient can retain copied plaintext
or keys. Symmetric authentication does not establish which key holder authored content.

A malicious server can deny service, replay or withhold updates. Remembered revisions
limit rollback but cannot prove freshness for a new client with no trusted history.
No immediate offline revocation, remote erasure, stored-envelope forward secrecy,
compromised-client protection or quantum resistance is claimed. V1 uses classical P-256.

Generate separate asymmetric pairs for recipient KEM, envelope author, scoped unlock,
per-generation recovery proof and server lease signing. Account/profile protection
roots and permitted resource roots remain distinct. Never derive a signing scalar
from a recovery secret or reuse a KEM pair as a signing pair.

## Fixed primitives and byte rules

| Operation | Reference profile |
| --- | --- |
| Symmetric encryption | AES-256-GCM, 32-byte key, 12-byte nonce, 16-byte tag |
| Password KDF | Argon2id v1.3, 65536 KiB, 3 passes, 4 lanes, 16-byte salt, 32-byte output; no secret/AD |
| Envelope subkey | HKDF-SHA-256 extract-and-expand, 32-byte output |
| Recipient wrapping | Native RFC 9180 base mode, KEM/KDF/AEAD 0x0010/0x0001/0x0002, empty AAD |
| Signing | Library RFC 6979 ECDSA P-256/SHA-256, 64-byte P1363 `r || s` |
| Public fingerprints | RFC 7638 SHA-256 JWK thumbprint, canonical unpadded base64url |
| Recovery secret / challenge nonce | Independently generated secure random 32-byte values |

Passwords are exact strict UTF-8: no normalization, trimming, case conversion or
truncation. Reject all unsupported KDF profiles before expensive derivation.
`C(array)` is compact strict UTF-8 JSON of constrained ASCII strings, arrays,
integers, booleans and null, with lowercase hexadecimal control escapes. It is not
a general object canonicalizer. Protocol integers are exact JSON integers in
1..9007199254740991, timestamps in 0..9007199254740991; reject booleans, exponent/
decimal notation and numeric strings. GUIDs are nonzero canonical lowercase D form.
Binary encodings are canonical unpadded base64url with exact length checks.

Enforce the aggregate request byte bound before decoding (reference beta bound:
1048576 bytes). Reject duplicate/unknown fields recursively, malformed Unicode and
invalid EC points/PKCS#8 DER. Public JWKs contain exactly `crv`, `kty`, `x`, `y`.
Trusted expected owner/scope/epoch inputs come from the caller, never the envelope.

## Content, protection and registration

Content fields are `format=cerberus-content-v1`, `keyEpoch`, `keySalt`, `nonce`,
`ciphertext`, `tag`. Ciphertext is nonempty. Each fresh encryption uses an independent
32-byte salt and 12-byte nonce, with one encryption per derived key:

```text
info = C(["cerberus-aead-key-v1",format,ownerId,resourceKind,resourceId,keyEpoch,extraContext])
key = HKDF-SHA-256(rootKey,decodedKeySalt,info,32)
aad = C(["cerberus-aead-v1",format,ownerId,resourceKind,resourceId,keyEpoch,extraContext,encodedKeySalt])
```

Content kinds are account/profile/record/folder/collection, with empty extra context.
Moving a resource preserves its immutable identity; owner/epoch changes require new
encryption. Local guards consume a salt/context even for failed or unsent encryption;
retries preserve existing bytes. The engineering budget is at most 2^32 fresh
reservations per root/context/epoch. Cross-device accounting, persistence and entropy
quality need real-client review; the local guard proves none of those.

Password wrappers use `cerberus-password-wrap-v1`, the base envelope fields and exact
`kdf` metadata. The Argon2 result supplies the root; context is account-protection or
profile-protection with `extraContext=["argon2id-v1.3",65536,3,4,encodedPasswordSalt]`.
The encrypted bundle contains scoped roots and a PKCS#8 unlock private key matching
the externally registered scoped verifier. Roots must belong to external membership
and be distinct, sorted by resource kind/GUID. Password changes use fresh salts/nonces
and an incremented protection-slot epoch, preserving permitted inner roots/epochs.
They do not revoke copied keys.

Recovery wrappers use `cerberus-recovery-wrap-v1`, base fields, generation and
proof-key fingerprint. The random secret supplies the root directly, with recovery
context and `extraContext=[generation,proofKeyFingerprint]`. An **independent recovery**
signing pair is generated for each generation, encrypted with the account protection
bundle, and verified against the registered public recovery verifier on unwrap.
The server holds only that public verifier. No deterministic secret-to-scalar
construction or recovery plaintext endpoint exists.

Proposed UC-01 registration adds client-generated `proposedAccountPublicId` beside
its idempotency key and envelope, permitting encryption before registration. Production
must atomically enforce uniqueness and terminal tombstones and bind that ID to the
verified Heimdall identity. A GUID/email is never ownership proof; retries retain the
same ID and bytes. This is a proposed product extension, not an implemented route.

## Recipient envelopes and trust transitions

HPKE base mode encrypts one permitted 32-byte resource root in each fresh context:
65-byte SEC1 encapsulation, 48-byte ciphertext including tag. A separate author signature
binds trusted owner/resource/epoch, grant/revision, recipient identity/fingerprint,
author fingerprint, encapsulation and ciphertext. Verify trusted pins and the author
signature before native decapsulation. Base mode alone does not authenticate a sender.

```text
info = C(["cerberus-recipient-wrap-v1",ownerId,resourceKind,resourceId,keyEpoch,grantId,grantRevision,recipientIdentityId,recipientKeyFingerprint,authorKeyFingerprint])
signedBytes = C(["cerberus-recipient-signature-v1",base64url(info),encodedEnc,encodedCiphertext])
```

Key transitions require independent authentication or the previously pinned author's
signature over account, role, old/new fingerprints and increasing directory revision.
A new-key self-signature or envelope-supplied key cannot establish trust. Lease-key
transitions use a separate issuer-bound domain/trust store. Rotation/revocation prevents
future disclosure under old grants, not retention of already copied material.

## Proofs, recovery and retries

Proposed freshly authenticated `POST /api/vault/challenges` creates no vault session.
Challenges bind random ID/nonce, operation, identity, account/scope, epoch, protection
revision, nullable recovery generation, request digest and exactly 60 seconds of validity.
Reject at expiry with no clock-skew extension, future issuance, unknown/consumed challenge,
stale state or mismatched binding. Verify the registered scoped/purpose key.

Hash the **original body bytes** before strict parsing. Proofs travel separately in
single-valued `X-Cerberus-Challenge-Id` and `X-Cerberus-Proof` headers; never log either.
Body encoding is uncompressed strict UTF-8 JSON without BOM. Its signed bytes include
the idempotency key, expected revision and complete replacement wrappers/verifiers.
Reordering or reserializing the body requires a new challenge; hashing a reconstructed
object is forbidden.

Recovery verifies the old registered recovery key after fresh authentication and local
bundle decryption. Production must atomically consume challenge/generation, compare
revision, replace the complete wrappers/verifiers, increment revocation generation and
store a nonsecret result. Exactly one concurrent recovery wins; failure rolls back all
state. Exact authenticated identity/idempotency-key/body-digest retries return that
stored outcome before checking consumed challenges. Signature malleability cannot
identify a second operation. Refresh requires current vault access and scoped proof
and supersedes the recovery generation. The local model's full-wrapper refresh also
increments protection slot epoch; a product flow preserving its password wrapper needs
an explicitly reviewed transition.

## Offline policy and clock lifecycle

Compact JWS has exactly `alg=ES256`, `typ=cerberus-offline-v1+jwt` and a trusted local
`kid`. Verify the original signing bytes and all externally expected issuer, audience,
identity/account/scope, epochs, policy/protection/revocation/grant revisions and times.
Audience is `cerberus-offline-clients-v1`; `nbf=iat`; reject future issuance.
Enabled renewal requires `exp > iat`, with exclusive expiry; default duration is 86400
seconds. Other positive representable owner-selected durations are allowed, with overflow
rejected and no invented universal maximum. `renewalEnabled=false` explicitly forbids
`exp` and permits no periodic expiry. It never permits local renewal or bypasses online
revalidation of identity/permissions before new disclosure/synchronization.

Authenticated online renewal anchors server time and a monotonic clock. Use at least
max(wall time, server time plus monotonic elapsed), retaining the high-water time.
Regression and process restart invalidate enabled-expiry offline use until fresh online
renewal. Cached-token import/reverification cannot establish or reset the anchor.
This availability tradeoff is deliberate; a separately reviewed platform may provide a
trustworthy continuous anchor. No mobile rollback-resistance claim follows from this model.
Retain verification trust for outstanding leases; no-expiry keys need retained trust or
online replacement. Signing-key retirement cannot immediately revoke a disconnected client.

## Executed evidence and reproduction

[interoperability-vectors.json](interoperability-vectors.json) is labeled public test
material, never production secrets. It contains 322 cases from each producer, checked
by both consumers, all six primitive known-answer families, native-suite results and
44 source/input/dependency SHA-256 bindings. Java/Bouncy Castle and Python/cryptography
independently own their parsers, context encoders and crypto. The harness has no production
project references and is excluded from Docker and production coverage.

[dependencies.lock.json](../../tools/protocol-harness/dependencies.lock.json) binds exact
JAR/wheel/distribution hashes, installed package versions and bundled Maven identities.
Historical failed and patched OSV reports remain available. A fresh audit checks the
actual graph/bytes and current feed; an old clean report is not current acceptance.
[The harness README](../../tools/protocol-harness/README.md) gives isolated setup and
complete commands. CI runs locked dependency installation/audit, both complete native
suites, published vectors and committed-corpus replay in the existing required `test`
job; it neither regenerates evidence nor bypasses the independent review gate.
The negative-consumer oracle distinguishes protocol rejection from fixture-value
mismatch. Embedded-key cases include malformed inner DER/version/curve/tags/scalar
width and valid controls. The supervisor enforces pipe/result bounds while children
run and terminates their process groups on timeout or excessive output.

[protocol-harness-benchmarks.json](protocol-harness-benchmarks.json) records ten samples
after warm-up in separate processes on the actual Linux host. Java median 164.8325625 ms,
Python median 63.3498705 ms; Linux wait4 peak RSS includes runtime/JIT baseline, not
isolated Argon2 allocation. These measurements impose no SLO and qualify no phone/browser.

Recovery, nonce accounting, trust transitions and clocks are **reference models**.
Actual storage transactions, distributed writers, Heimdall freshness, client key storage,
entropy and platform time anchors remain unimplemented obligations. Future **real-client**
tests must repeat compatibility, strict parsing, byte/nonce behavior, KDF resource
measurement, platform restart/rollback and trust-establishment checks on supported targets.

## Independent review handoff

Review the complete design, these source implementations, dependency locks/provenance,
actual public vectors and measurements together. Assess composition, nonce budgets,
profile isolation, metadata/proof binding, key roles/pinning/rotation, atomic recovery/
retry behavior, offline policy and rollback, memory handling and classical-key migration.
Independent security and interoperability reviewers must record identities, date, outcome,
evidence reference and unresolved findings. Real-client evidence remains a distinct
obligation from these two reference implementations.

Only genuine review evidence may update `protocol-review.json`. It must hash this exact
designated document and the actual `interoperability-vectors.json`, plus the reviewed
sources/design/locks in the evidence reference. Missing/stale approvals remain blocked;
owner authorization, green CI and benchmark results cannot populate reviewer fields.
No business use case is completed and no release is authorized by this harness.

Primary standards/provenance: [RFC 9106](https://www.rfc-editor.org/rfc/rfc9106),
[RFC 5869](https://www.rfc-editor.org/rfc/rfc5869),
[RFC 9180](https://www.rfc-editor.org/rfc/rfc9180),
[RFC 6979](https://www.rfc-editor.org/rfc/rfc6979),
[RFC 7638](https://www.rfc-editor.org/rfc/rfc7638),
[RFC 7518](https://www.rfc-editor.org/rfc/rfc7518),
[RFC 8725](https://www.rfc-editor.org/rfc/rfc8725), and the pinned licensed
[known-answer sources](../../tools/protocol-harness/fixtures/sources.json).
