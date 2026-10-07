# Cerberus v1 protocol and interoperability harness design

Date: 2026-10-07.

Status: **Written design submitted for owner review. Not an independent security
approval, implementation result, or release authorization.**

## 1. Intent, scope, and approval boundaries

The owner has confirmed that no client exists and authorized a test harness and
research-based protocol preparation. The owner approved the standards-based
.NET/Python approach presented in chat. This document makes that approach precise
enough for a test-first implementation plan and subsequent security review.

Success means two separately implemented harnesses can produce, consume, and
reject the same versioned contracts. Neither implementation may delegate its
cryptographic work or contract serialization to the other. Actual measured results
and executed vectors, rather than placeholder approvals, become review inputs.

This work is a **non-production reference implementation**, outside the API's
production projects. It does not implement or close UC-01, UC-38 through UC-41,
sharing, or offline-authorization issues. NFR-11 and IR-09 remain in force. A
passing harness is interoperability evidence, not an independent security review.

The current `docs/security/protocol-review.md` is an earlier proposal. After this
written design and its implementation plan are approved, harness preparation will
consolidate the selected contract there, identify changes from that proposal, and
leave `docs/security/protocol-review.json` pending. This submitted document does
not silently replace an approved product specification.

### Requirements traced

| Concern | Existing requirements | Harness responsibility |
| --- | --- | --- |
| Encrypted envelopes and client-held keys | NFR-01, FR-KY-01, FR-KY-02, FR-KY-06 | Encoding, key separation, authenticated context, rejection vectors |
| Password/profile protection | FR-PR-11, FR-KY-06, FR-KY-07 | Separate protection slots and wrapper compatibility |
| Recovery | FR-KY-08 through FR-KY-12 | Local recovery decryption, public-key proof, modeled consumption and retry |
| Recipient provisioning | FR-SH-08, FR-SW-02, FR-SW-04 | Recipient-specific wrapped resource keys and sender verification |
| Offline authorization | FR-SY-01 through FR-SY-07 | Signed lease verification and modeled clock/reconnection behavior |
| Safe transport and diagnostics | NFR-02, NFR-03, NFR-04, FR-KY-13 | No live identity calls, secret-free diagnostic output |
| Required review | NFR-11, IR-09 | Preserve pending approval and produce verifiable review artifacts |

Transactions, Heimdall freshness, permission checks, actual storage, and production
concurrency remain obligations of their owning use cases. A harness state-machine
test must not be described as proof of PostgreSQL transaction correctness.

## 2. Threat model and alternatives

Protect vault plaintext and usable vault keys from database/backup disclosure and
from a server that only possesses identity credentials and lease-signing keys.
Detect ciphertext substitution between owners, resources, purposes, recipients,
and epochs. Server-controlled key directories are not independent trust anchors.

Trusted components are the user's client, its verified cryptographic libraries,
its secure randomness, and independently authenticated key fingerprints. An
authorized reader can retain keys or plaintext. AES authentication does not prove
which authorized holder of a symmetric resource key authored a ciphertext.
Read-only API permissions do not cryptographically prevent local modifications.

The API can deny service, withhold updates, and replay previously valid content.
Epoch/revision checks and remembered state limit rollback but cannot guarantee
freshness on a newly installed client with no independently trusted history.
There is no claim of remote erasure of copied secrets, immediate offline
revocation, confidentiality from a compromised client, or hidden ciphertext
length. There is no forward-secrecy claim for stored recipient envelopes.

Considered alternatives:

- **Selected:** standardized AES-GCM, Argon2id, HKDF, HPKE and ECDSA contracts,
  checked by independent .NET and Python libraries. This retains the approved
  outline and makes cross-language review reproducible.
- A libsodium-specific XChaCha20/Curve25519 profile is viable but changes more of
  the proposed suite and its library/encoding contracts.
- A hybrid post-quantum profile would address a different long-term confidentiality
  threat and requires its own supported-library and client-target evaluation. V1
  uses classical P-256 and is not quantum-resistant; do not market it as such.

These are application-design choices informed by the sources in section 12, not
an assertion that those sources certify this combined protocol.

## 3. Primitive profile and key separation

| Operation | Fixed v1 profile |
| --- | --- |
| Symmetric encryption | AES-256-GCM; 32-byte key, 12-byte nonce, 16-byte tag |
| Password derivation | Argon2id v1.3; memory 65,536 KiB, 3 passes, 4 lanes, 16-byte salt, 32-byte output; no secret or Argon2 associated-data input |
| Subkey derivation | HKDF-SHA-256 extract-and-expand, 32-byte output, context-specific `info` |
| Recipient wrapping | RFC 9180 HPKE base mode; KEM `0x0010`, KDF `0x0001`, AEAD `0x0002`; one message per context |
| Application signatures | ECDSA P-256 / SHA-256; RFC 6979 deterministic signing through library APIs |
| Signature encoding | IEEE P1363: unsigned big-endian 32-byte `r` followed by 32-byte `s`, exactly 64 bytes |
| Public-key fingerprint | RFC 7638 SHA-256 JWK thumbprint, unpadded base64url |
| Recovery secret | 32 bytes from the client's cryptographically secure random generator |
| Challenge nonce | 32 bytes from the server/reference verifier's cryptographically secure random generator |
| Offline token | Compact JWS, ES256, explicit Cerberus-specific token type |

Key material has distinct roles: account/profile protection roots, per-resource
roots, recipient KEM private keys, envelope-author signing keys, scoped unlock
signing keys, per-generation recovery signing keys, and server lease-signing keys.
Generate independent asymmetric key pairs; never reuse an ECDSA pair for ECDH or
the same signing pair for these separate roles. A recipient receives only the
resource keys its grant permits, not an owner's account/profile protection root.

Vault passwords are separate from Heimdall login credentials. Convert the exact
vault password to strict UTF-8, with no trimming, case conversion, normalization,
or silent truncation in v1. Reject ill-formed Unicode. Password derivation runs
only in the client/reference harness. The API stores neither the password nor the
Argon2 output. Only the fixed profile above is supported; reject altered parameters
before invoking an expensive KDF. Changes require a new named profile and review,
not a silent fallback to weaker parameters.

Reference-test private keys, recovery secrets and plaintext are conspicuously
public test fixtures with no production value. Production entropy may never be
replaced with seeded fixture randomness.

## 4. Common byte encoding

Define `C(array)` as strict UTF-8 bytes of a JSON array with no insignificant
whitespace. Elements in authenticated arrays below are ASCII strings, nested
arrays, JSON integers, booleans, or `null`. Emit strings without unnecessary
escapes. This constrained serializer is not an arbitrary-object canonicalizer.

- Public GUIDs use lowercase canonical D form and are nonzero.
- Protocol integers are JSON integers, not booleans, decimals, exponent notation,
  or numeric strings. Epochs, generations and revisions are in
  `1..9,007,199,254,740,991` for exact cross-language representation.
- Timestamps are nonnegative integral Unix seconds within the same exact range.
- Binary values use RFC 4648 unpadded base64url; reject padding, whitespace,
  alternative alphabets, noncanonical unused bits, and incorrect decoded lengths.
- Reject duplicate object properties, unknown fields and wrong JSON types at every
  contract boundary. Input object property order does not change meaning.
- P-256 public JWKs contain exactly `crv`, `kty`, `x`, `y`, with `crv=P-256`,
  `kty=EC`, and 32-byte coordinates. Validate that the point is on the curve and
  not infinity using the crypto library. Fingerprint the required RFC 7638 members.

Format-specific parsers use deployment request bounds before allocating decoded
buffers. The approved beta example is 1 MiB for the **entire request**, not 1 MiB
per field. The harness exercises smaller limits as well as the configured bound;
it does not add unapproved limits on plaintext record names or values.

The receiving client reconstructs authenticated context from its expected owner,
resource and operation. It must not accept a valid ciphertext merely by trusting
the ciphertext's own claimed context.

## 5. Symmetric envelope, password and recovery wrapping

### 5.1 Symmetric envelope

Required fields for `cerberus-content-v1`: `format`, `keyEpoch`, `keySalt`,
`nonce`, `ciphertext`, `tag`. `keySalt` is 32 random bytes; `nonce` is 12 random
bytes. `ciphertext` is nonempty and bounded; `tag` is exactly 16 bytes.

For every encryption, including edits and rewrapping, generate a fresh key salt
and nonce. For a 32-byte root key and externally expected context, define:

```text
info = C(["cerberus-aead-key-v1", format, ownerId, resourceKind,
          resourceId, keyEpoch, extraContext])
aesKey = HKDF-SHA-256(rootKey, salt=decodedKeySalt, info=info, length=32)
aad = C(["cerberus-aead-v1", format, ownerId, resourceKind,
         resourceId, keyEpoch, extraContext, encodedKeySalt])
```

For content, `extraContext=[]`; `resourceKind` is exactly `account`, `profile`,
`record`, `folder`, or `collection`. Use the expected immutable public resource
GUID, including the account GUID for account details. Folder/profile movement
does not alter immutable content identity. An owner or epoch change requires new
encryption and the corresponding wrappers.

This per-envelope derived key avoids relying solely on a shared 96-bit random
nonce space across devices. Each derived AES key is used for one encryption.
Fresh salts are still mandatory: repeating a salt/context can repeat a key, and
repeating its nonce is forbidden. A local encryption guard rejects reused
salt/context pairs; production persistence must reject non-idempotent reuse of
an accepted pair. An identical lost-response retry resubmits existing bytes rather
than encrypting again. Failed or offline-unsent encryption still consumes that
pair locally. A guard is not a substitute for secure randomness.

V1 sets a conservative engineering budget of at most `2^32` fresh encryptions per
root-key/context/epoch across writers, then requires root rotation. The random
256-bit salt gives a collision probability below `2^-193` at that budget under
independent uniform generation. This statistical assumption, budget accounting
across offline devices, and library memory handling are explicit security-review
items, not claimed independently verified properties. Harness counters/guards
model the rule; actual client coordination is outside this repository.

### 5.2 Password wrapper

Required fields: `format=cerberus-password-wrap-v1`, `keyEpoch`, `keySalt`,
`nonce`, `ciphertext`, `tag`, and `kdf`.

`kdf` contains exactly `algorithm=argon2id-v1.3`, `memoryKiB=65536`,
`iterations=3`, `parallelism=4`, and the encoded 16-byte `salt`.

The Argon2 output is the root input to the derivation in section 5.1. Context kind
is `account-protection` or `profile-protection`, resource ID is that account or
profile ID, and:

```text
extraContext = ["argon2id-v1.3",65536,3,4,encodedPasswordSalt]
```

The plaintext protection bundle has exactly `format=cerberus-protection-bundle-v1`,
`scopeKind`, `scopeId`, `roots`, and `unlockPrivateKey`. `roots` is a nonempty
array of distinct `{resourceKind,resourceId,keyEpoch,key}` entries sorted by kind
then GUID; `key` encodes a 32-byte root. `unlockPrivateKey` encodes a P-256 PKCS#8
DER key whose public part must match the scoped registered unlock verifier.
The bundle contains only the keys this protection slot authorizes. A profile
slot must not contain an account-wide root or another profile's private key.
Harness fixtures provide an explicit scope-to-root membership map to test this
rule; real client provisioning remains an obligation of the owning use case.

For a password wrapper, `keyEpoch` is the protection slot epoch, not the content
epoch of each root within it. Password changes use fresh password salt, key salt
and nonce, with an incremented protection epoch. Rewrap permitted roots; do not
silently imply that changing a password revokes keys already copied. Changing a
password alone need not change the inner resource roots or their content epochs.

### 5.3 Recovery wrapper

Required fields: `format=cerberus-recovery-wrap-v1`, `keyEpoch`, `keySalt`,
`nonce`, `ciphertext`, `tag`, `generation`, and `proofKeyFingerprint`.

Use the random recovery secret as the root input to section 5.1; it is high-entropy
material, not a password requiring Argon2. Context kind is `recovery`, resource ID
is the account ID, and `extraContext=[generation,proofKeyFingerprint]`.

The client generates an independent recovery ECDSA key pair for each generation.
The plaintext recovery bundle contains exactly
`format=cerberus-recovery-bundle-v1`, `generation`, `proofPrivateKey`, and
`protectionBundle`. `proofPrivateKey` encodes the recovery P-256 private key in
PKCS#8 DER; `protectionBundle` is an account-scope bundle in the schema above.
`keyEpoch` is the account protection epoch. The payload is not returned as
plaintext by the API. After decrypting, the client checks scope and generation
and verifies that the included proof private key's public fingerprint equals the
expected registered recovery verifier. The reference scope/key-ring fixture is
not a claim that a complete product key-provisioning graph exists.

The server's recovery verifier is the **public signing key only**. There is no
custom conversion of the raw recovery secret into an ECDSA scalar. Knowing the
public verifier does not give the server the recovery secret or wrapping key.

### 5.4 Registration binding

UC-01 needs an owner GUID before the client can encrypt account onboarding data.
The proposed registration contract therefore includes a client-generated
`proposedAccountPublicId` beside its idempotency key and envelope. The server
accepts it only as a proposed identifier, atomically enforces uniqueness and
terminal-tombstone checks, and binds it to the verified Heimdall identity.
It never treats that GUID or an email address as proof of ownership.

The same idempotent operation retains that GUID; a retry cannot change it or the
envelope. Linking an existing identity/account requires identity proof and the
actual established account ID. This is an explicit proposed UC-01 wire-contract
extension, not an implemented route or an authorization exception.

## 6. Recipient envelope and key trust

Use HPKE base mode for confidentiality and a **separate author signature** for
sender authentication. The independent Python library's single-shot API supports
this base-mode construction. Never represent base mode alone as sender-authenticated.

Required fields of `cerberus-recipient-wrap-v1`: `format`, `keyEpoch`, `grantId`,
`grantRevision`, `recipientIdentityId`, `recipientKeyFingerprint`,
`authorKeyFingerprint`, `enc`, `ciphertext`, `signature`.

Recipient key and author signing key must have independently verified fingerprints.
Trust is established by an authenticated channel independent of the API's mutable
key directory; the harness uses explicitly provisioned trusted fixtures. No
automatic trust-on-first-use or acceptance of an envelope-supplied verification
key is permitted. A genuine client must supply that trust-establishment UI/channel.

```text
info = C(["cerberus-recipient-wrap-v1",ownerId,resourceKind,resourceId,
          keyEpoch,grantId,grantRevision,recipientIdentityId,
          recipientKeyFingerprint,authorKeyFingerprint])
signedBytes = C(["cerberus-recipient-signature-v1",base64url(info),
                 encodedEnc,encodedCiphertext])
```

HPKE's single-shot `info` authenticates all auxiliary context. Context AEAD
associated data is empty; do not add a second, differently implemented AAD layer.
For the selected P-256 suite, `enc` is a 65-byte uncompressed SEC1 public point.
`ciphertext` includes the HPKE AEAD tag. Encrypt one raw 32-byte permitted resource
root per fresh HPKE context; its ciphertext is 48 bytes. Clients verify the author
signature and trusted fingerprints before decapsulation, then verify the full
expected binding and decrypt with the recipient's KEM private key.

Envelope parsers and libraries reject invalid curve points, wrong suite/format,
wrong recipients, substituted authors, corrupted signatures/tags, and old grant
or key epochs. Regrant uses a new revision; revoked recipients are excluded from
new wrapped-key sets. Key removal cannot destroy previously received material.

A new public key requires independently authenticated verification, or a signed
transition from the previously pinned author key binding account, old/new
fingerprints, key role, and increasing key-directory revision. A signature made
only by the newly introduced key does not establish trust.
The transition signature covers
`C(["cerberus-key-transition-v1",accountId,keyRole,oldFingerprint,newFingerprint,directoryRevision])`;
`keyRole` is `recipient-kem` or `envelope-author`. Its new public JWK must match
`newFingerprint`, and the pinned author key verifies the transition. An old
recipient KEM key is not repurposed as a signing key. Reject transitions without
the previously pinned author, with the wrong role/account, or with a revision
not greater than the remembered directory revision.

## 7. Challenge proofs, recovery consumption, and retries

Proposed supporting route: freshly Heimdall-authenticated
`POST /api/vault/challenges`, before a protected operation. It never creates a
vault session by itself. The reference verifier is local to the harness; no API
route is added by this work.

Challenge fields: `format=cerberus-challenge-v1`, `challengeId` (random GUID),
`nonce` (32 random bytes), `operation`, `identityId`, `accountId`, `scopeKind`,
`scopeId`, `keyEpoch`, `protectionRevision`, `generation`, `requestHash`,
`issuedAt`, `expiresAt`. All fields are required; `generation` is `null` except
for recovery. Operations are `unlock-account`, `unlock-profile`,
`change-protection`, `recover`, and `refresh-recovery`. Scope kinds are `account`
or `profile`; account scope ID equals account ID. Recovery uses account scope.

`requestHash` is SHA-256 of the exact UTF-8 JSON bytes of the intended operation
body **excluding the submitted challenge/signature**, transported separately from
that body. The client supplies this digest when requesting the challenge and
retains the identical body bytes for execution. The server recomputes it from the
received body, not from a parsed/reserialized approximation. Thus object-order
changes require a new challenge but cannot change a verified operation's meaning.
For the proposed HTTP integration, use single-valued
`X-Cerberus-Challenge-Id` (canonical GUID) and `X-Cerberus-Proof` (base64url of a
64-byte signature) headers; neither header is logged. The operation body is
strict UTF-8 JSON without a byte-order mark, with no compression/content encoding.
The bounded raw bytes are hashed before parsing, and the same strict parser then
rejects malformed or ambiguous JSON. The idempotency key is inside that signed
operation body, not an unsigned header.

```text
proofBytes = C(["cerberus-proof-v1",challengeId,encodedNonce,operation,
                identityId,accountId,scopeKind,scopeId,keyEpoch,
                protectionRevision,generation,encodedRequestHash,
                issuedAt,expiresAt])
```

Set expiry to exactly 60 seconds after issuance. Reject `now >= expiresAt`, future
issuance, operation/body/identity/scope mismatch, unknown or already consumed
challenge, stale epoch/revision/generation, bad signature length/scalars, or
invalid signature. No clock-skew extension. Sign using the registered key for
that scope and purpose, not an arbitrary public key supplied with the request.

For recovery, the client freshly authenticates, downloads opaque current recovery
material, decrypts it locally, signs with the recovered generation's private key,
and submits replacement password/protection and recovery wrappers. The signed
body digest covers the new verifier, complete wrapper set, expected revision,
and idempotency key. Verify the **old registered** recovery key, never the proposed
replacement verifier.

In the production use case, challenge consumption, recovery-generation consumption,
revision compare-and-swap, complete replacement wrapper/verifier set, operation
result, and revocation-generation increment must commit atomically. Exactly one
concurrent recovery wins. Failure rolls back all effects. Refresh requires valid
current vault access and its scoped proof, replaces the generation atomically,
and makes the old credential unusable for future server-mediated recovery.

After a committed recovery, an exact retry by the same freshly authenticated
identity with the same idempotency key and original body digest returns the stored
nonsecret outcome before checking the consumed challenge. A different body or
identity cannot reuse that result. A retry does not perform recovery again, return
a plaintext secret, or disclose a result to an unauthenticated caller. Operation
identity is not a signature hash; ECDSA signature malleability cannot create a
second operation. The harness models these transitions with an injectable clock
and an atomic in-memory state machine, labeled non-production.

## 8. Offline lease validation and signing-key rotation

The compact JWS protected header contains exactly:
`alg=ES256`, `typ=cerberus-offline-v1+jwt`, `kid=<trusted signing-key thumbprint>`.
Reject `none`, HS256, alternate algorithms/types, unknown key IDs, token-supplied
keys/URLs, critical extensions, duplicate fields, or malformed compact encoding.
Never accept this token as a Heimdall identity bearer or online vault handle.

The required payload fields are `iss`, `aud`, `sub`, `accountId`, `scopeKind`,
`scopeId`, `keyEpoch`, `protectionRevision`, `policyRevision`,
`revocationGeneration`, `grantRevisions`, `renewalEnabled`, `iat`, `nbf`, `jti`.
`exp` is required only when `renewalEnabled=true`, and forbidden otherwise.
`sub` is the Heimdall public identity GUID; `jti` is a random nonzero GUID.
`aud` is exactly `cerberus-offline-clients-v1`; `iss` equals the explicitly trusted
Cerberus issuer configured for this deployment. Profile scope binds the selected
profile. `grantRevisions` is an array sorted by grant GUID, with distinct
`{grantId,revision}` objects and no grants outside the permitted scope.

Verify the signature over the original compact JWS bytes, not reserialized JSON.
Then validate every expected issuer, audience, identity, account, scope, epoch,
revision, and time claim. `nbf=iat`; future issuance or `now < nbf` is invalid.
When renewal is enabled, `exp > iat` and expiration is exclusive: reject at
`now >= exp`. Default duration is 24 hours; another owner-selected positive
representable duration is permitted. There is no invented universal maximum
renewal duration; reject arithmetic overflow and out-of-range wire timestamps.

When renewal is disabled, explicitly signed `renewalEnabled=false` permits no
periodic expiry. Absence of `exp` in any other case is invalid. Reconnection still
requires current identity, permissions, epochs and revocations before additional
disclosure or synchronization; a previously signed lease cannot renew itself.

On receipt online, anchor elapsed time to a trusted server time and a monotonic
clock. During that process lifetime use at least
`max(currentWallTime, serverTimeAtReceipt + monotonicElapsed)` and retain the
highest observed value. Wall/monotonic regression invalidates enabled-expiry
offline use until online renewal. After process restart or reboot, v1 requires
fresh online renewal for enabled-expiry leases unless a separately reviewed
platform provides a trustworthy continuous time anchor. This deliberately reduces
offline availability across restarts; the harness does not claim a desktop clock
solution proves rollback resistance on future mobile devices.
Only successful authenticated online renewal establishes an anchor, using the
freshly issued signed lease's `iat` and recording its monotonic receipt time.
Importing or re-verifying a cached token never resets an anchor or the remembered
high-water time. The issuer is trusted as the authorization/time authority; this
does not grant it possession of vault decryption keys. The harness supplies a
controlled trusted-renewal event rather than pretending a bare token is evidence
of an online exchange.

The server's lease-signing key is separate from all vault and proof keys. Clients
pin verification keys. A new key is introduced by independent provisioning or an
old-key-signed, increasing-version transition. Key IDs select only locally trusted
keys, never remote URLs supplied by a token. Rotation stops new signing with the
old key but cannot invalidate an offline client's cached key. Retain verification
keys for outstanding enabled-expiry leases; no-expiry leases require retained
trust or fresh online replacement, so scheduled retirement cannot promise
immediate offline revocation. Compromise requires an operator incident response
and client reconnection; no such response is claimed implemented here.
For lease-key rotation, the old pinned lease key signs
`C(["cerberus-lease-key-transition-v1",issuer,oldFingerprint,newFingerprint,directoryRevision])`.
The configured issuer is an ASCII string and must equal the trusted issuer;
verify the new JWK fingerprint and a strictly increasing directory revision.
This is a different domain and trust store from client key transitions.

## 9. Harness architecture, inputs, and diagnostics

Proposed layout:

```text
tools/protocol-harness/
  dotnet/       reference contract library/CLI and its tests
  python/       independently written contract implementation and tests
  README.md     isolated setup, verification and benchmark commands
docs/security/
  protocol-review.md             consolidated reviewed-design input
  interoperability-vectors.json  actual cross-verified public test fixtures
  protocol-review.json           unchanged pending approval until real review
```

The .NET side uses Bouncy Castle managed implementations for AES-GCM, Argon2id,
HKDF, HPKE and ECDSA. The Python side uses `cryptography` and its separate
Rust/OpenSSL-backed implementations, including Argon2id and HPKE. Research at
design time found stable Bouncy Castle 2.7.0 and Python `cryptography` 50.0.2;
implementation must verify available stable versions, pin exact dependency
versions, and run applicable vulnerability checks before adoption.

Each side owns its encoders, strict parsers, context constructors, crypto calls,
and verification rules. Sharing only a data-only vector corpus is allowed. Use
an orchestrator to exchange outputs, not to centralize crypto/validation. Refuse
missing libraries, unsupported primitives, missing tests, or failed cross-checks;
never skip a missing implementation and report interoperability success.

The harness is not referenced by any production API project or packaged in its
Docker image. Its coverage is reported separately and cannot inflate the existing
production coverage metric. Keep dependencies isolated and installation
reproducible. No live Heimdall users, credentials, email, production secrets,
deployment, or new external runtime service is involved.

Machine-readable output lists test identifiers, producing/consuming implementation
versions, results, artifact digests, and benchmark measurements. Diagnostics must
not echo input values, proofs, derived keys, or secret-bearing exception messages.
Deliberately public fixture values live only in the labeled vector corpus, not
general success/failure logs.
Invalid input or cryptographic verification returns a stable `invalid_protocol`
failure without raw exceptions or partially decrypted output. Unsupported/missing
dependencies return `unsupported_dependency` and block the complete verification
command. Deliberately expected rejections count as passing negative tests, not as
skipped work. Normal verification exits zero only when all required positive,
negative, known-answer and cross-implementation checks have completed successfully.

The vector file is created only after both implementations execute successfully.
It contains schema version, explicit `public-test-fixtures` classification,
implementation versions, source references for known-answer tests, and cases with
input context, exact expected bytes/digests, origin and cross-verification result.
It records content and wrapper ciphertexts from both producers, password KDF/HKDF
results, signed proofs/leases, HPKE ciphertexts, negative mutations and expected
rejections. Randomized HPKE outputs need not be identical; each actual output must
decrypt in the other implementation. Fixture private keys are never real users'
keys. Check in generated fixtures, not fabricated execution timestamps/results.

## 10. Verification and performance evidence

Both implementations must run known-answer checks against published vectors for
their primitives before trusting their agreement with each other. Pin vector
sources and preserve license/provenance. Deterministic operations must produce
identical bytes. Randomized encryption must cross-decrypt in both directions.

Required behavioral matrix:

| Area | Positive and rejection evidence |
| --- | --- |
| Encoding | Exact ASCII context bytes; reordered input properties accepted; duplicates, unknown fields, bool-as-int, exponent numbers, malformed GUID/base64url rejected |
| Passwords | Identical Argon2/HKDF output and cross-unwrapping; Unicode-byte cases; wrong password/salt/parameters and oversized work request rejected |
| Content | Both-direction encryption/decryption; altered owner/resource/format/epoch/purpose/salt/nonce/ciphertext/tag rejected; no plaintext release on tag failure |
| Nonce/key use | Fresh entropy in normal encryption; repeated salt/context refused; identical retries preserve bytes; exhaustion and rotation modeled |
| Recipient wrapping | Both-direction HPKE open; pinned author verification; wrong recipient/fingerprint/grant revision, forged sender, invalid points, old epochs and corrupted signatures rejected |
| Proofs | Identical deterministic signatures and cross-verification; all binding changes, wrong key/purpose, replay, expiry-at-boundary and stale generation rejected |
| Recovery | Local-only secret; verifier cannot decrypt; concurrent modeled consumption gives one winner; refresh supersedes; rollback and lost-response retry preserve modeled invariants |
| Leases | Both-direction ES256 verification; wrong issuer/audience/type/algorithm/key/scope/epoch/revisions, duplicate claims, expiry, overflow and malformed no-expiry policy rejected |
| Time and rotation | Forward progression, clock regression, restart, unknown signing key, trusted transition and rejected rollback tested with controlled clocks |
| Diagnostics and gate | Secret-bearing failures stay redacted; pending/stale/absent review remains blocked even with green harness tests |

Measure password derivation in a separate benchmark command with warm-up and
repeated samples. Record OS, CPU architecture, runtime/library versions, profile,
sample count, elapsed-time distribution and process-memory measurement method.
Measure each implementation in an isolated process to avoid confusing cumulative
peak RSS or JIT/runtime memory with Argon2 working memory. These are measurements,
not an invented latency SLO or evidence for untested phones/browsers. Future client
targets must rerun compatibility, entropy and resource checks before adoption.

Before claiming harness completion, run both complete harness suites, the
cross-implementation verification, existing Python helper/specification checks,
and the repository's full unfiltered .NET suite. Report actual counts and outputs.
No test omission, fabricated vector, self-approved security record, or CI bypass
is an acceptable substitute.

## 11. Acceptance, handoff, and sequencing

The implementation plan must sequence failing encoding/primitive tests, symmetric
contracts, recipient wrapping/signatures, challenge/recovery and lease models,
then cross-verification and recorded benchmarks. Each step stays outside product
routes and carries explicit negative tests.

Acceptance for this reference work requires executed bidirectional vectors,
complete negative tests, reproducible isolated commands and honest measurement
limits. It does not mark the 55 business use cases implemented. Consolidate the
actual contract and test evidence into the designated security-review artifacts.
The pending manifest is left pending until an independent security reviewer and
interoperability reviewer record identities, date, outcome, evidence reference,
and hashes of the exact protocol document and actual vector file. Include this
design and implementation sources in the review evidence as well.

Research references, owner design approval, code review and green tests are
different evidence types. None alone is permission to claim NFR-11 satisfied.
The reviewer must assess the composition, including nonce/key budgets, profile
isolation, proof/envelope binding, trust establishment, recovery replacement,
offline clock/restart behavior, rollback, and classical-key migration limitations.

Changes to the production endpoint inventory, registration contract, accepted
formats and technology document are staged for their corresponding reviewed use
cases after the protocol gate is legitimately satisfied. Do not remove the gate
or weaken requirements to make pending review appear green.

## 12. Primary research sources

Sources checked during protocol preparation on 2026-10-07. Exact application
labels, layouts, limits, challenge duration and clock policy above are Cerberus
design decisions submitted for review, not quotations from these documents.

- [OWASP Cryptographic Storage](https://cheatsheetseries.owasp.org/cheatsheets/Cryptographic_Storage_Cheat_Sheet.html): authenticated encryption, maintained hybrid-encryption libraries, key separation, secure randomness and migration considerations.
- [OWASP Password Storage](https://cheatsheetseries.owasp.org/cheatsheets/Password_Storage_Cheat_Sheet.html): Argon2id and parameter measurement; this is not permission to store vault passwords server-side.
- [RFC 9106, section 4](https://www.rfc-editor.org/rfc/rfc9106.html#section-4): the memory-constrained Argon2id profile used as the initial reference baseline.
- [RFC 5869](https://www.rfc-editor.org/rfc/rfc5869.html): HKDF extract/expand and context separation; HKDF is not a password-hardening replacement.
- [NIST SP 800-38D](https://csrc.nist.gov/pubs/sp/800/38/d/final): GCM authentication and IV constraints; the publication's revision notice is not treated as a new finalized algorithm profile.
- [RFC 9180, sections 8 and 9](https://www.rfc-editor.org/rfc/rfc9180.html#section-8): HPKE auxiliary context, base-mode anonymity, application replay/trust obligations, and signatures over encapsulation/ciphertext.
- [RFC 7638](https://www.rfc-editor.org/rfc/rfc7638.html): public JWK thumbprints.
- [RFC 4648](https://www.rfc-editor.org/rfc/rfc4648.html): canonical unpadded base64url under the explicit v1 encoding rules.
- [RFC 6979](https://www.rfc-editor.org/rfc/rfc6979.html): deterministic ECDSA, implemented by libraries rather than handwritten scalar arithmetic.
- [RFC 7518, section 3.4](https://www.rfc-editor.org/rfc/rfc7518.html#section-3.4): ES256 and fixed-width signature encoding.
- [RFC 8725](https://www.rfc-editor.org/rfc/rfc8725.html): algorithm allowlists, explicit token typing and separate validation rules.
- [Bouncy Castle C# downloads](https://www.bouncycastle.org/download/bouncy-castle-c/): maintained reference-library release information.
- [Python cryptography HPKE](https://cryptography.io/en/stable/hazmat/primitives/hpke/), [KDFs](https://cryptography.io/en/stable/hazmat/primitives/key-derivation-functions/), and [ECDSA](https://cryptography.io/en/stable/hazmat/primitives/asymmetric/ec/): independent APIs, supported primitives and runtime constraints.
- [NIST FIPS 203](https://csrc.nist.gov/pubs/fips/203/final): standardized ML-KEM; it does not make this classical P-256 v1 profile quantum-resistant.
