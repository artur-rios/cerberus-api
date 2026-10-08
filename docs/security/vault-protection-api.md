# Vault protection and account unlock API

Development contract for UC38. The independent protocol/client review is deferred
by the owner; the actual approval record remains pending and release is gated.
Use TLS at the deployed API boundary and to Heimdall. All responses are no-store.

## Initialize and retrieve protection

POST `/api/vault/protection` accepts exactly `accountId`, `expectedAccountRevision`
and `material`. Use the public account ID/revision returned by registration or login;
actor identity is always derived from the current signed/currently revalidated bearer.
The owner account must be active and non-erased. No existing session is required to
bootstrap the first protection state. Success201 returns `accountId`,
`protectionRevision`, `keyEpoch`, `recoveryGeneration` (all three counters1).
An existing initialization or stale expected revision returns409 and preserves it.
A lost response does not authorize replacement: GET current material before retrying.

`material` has exactly `passwordWrapper`, `recoveryWrapper`, `unlockVerifier`,
`recoveryVerifier`, `recipientKey`, `authorKey`. See the [protocol proposal](protocol-review.md)
for client bundle contents, AEAD/HKDF context and key roles. Wrapper keyEpoch starts1,
recovery generation1. The password wrapper KDF metadata is exactly algorithm
`argon2id-v1.3`, memoryKiB65536, iterations3, parallelism4, canonical16-byte salt.
Wrapper formats are `cerberus-password-wrap-v1` and `cerberus-recovery-wrap-v1`;
keySalt32 bytes, nonce12 bytes, tag16 bytes, nonempty ciphertext, all canonical
unpadded base64url. Recovery `proofKeyFingerprint` is the RFC7638 SHA256 thumbprint
of the independent recovery public verifier.

Each public JWK contains exactly `crv=P-256`, `kty=EC`, and canonical32-byte `x`,`y`
coordinates on P256. All four key roles use distinct pairs. Never send private `d`,
PKCS8, raw vault passwords, recovery secrets or usable encryption roots to this API.
Unknown/duplicate/case-mismatched fields, numeric strings/fractions, unsupported
KDF/format/curve, invalid points or reused roles return400 without writes.

GET `/api/vault/protection` accepts no query/body and returns only own protection
counters, public account ID and original opaque material. It requires current owner
identity, not unlocked vault access, so the client can derive/decrypt locally. Hidden,
closing, erased, foreign or absent protection returns404. The API cannot validate
ciphertext plaintext/authentication: clients must verify AEAD and expected trusted
owner/account scope/epoch, key correspondence and distinct permitted roots locally.

## Obtain and prove an account challenge

Prepare the exact strict UTF8, uncompressed JSON body for POST `/api/vault/unlock`:

```json
{"expectedProtectionRevision":1}
```

Hash its original bytes with SHA256 and encode canonical unpadded base64url. POST
`/api/vault/challenges` with exactly `operation:"unlock-account"` and `requestHash`.
The original signed bearer must have exactly one signed `iat` at most59 seconds old
and not in the future; missing/older/future timestamps return401 and require a fresh
Heimdall login. Current Heimdall identity/scope is still revalidated on every route.

Success201 returns `data.challenge` with format `cerberus-challenge-v1`, random GUID
`challengeId`, random32-byte `nonce`, operation, identityId, accountId, scopeKind
`account`, scopeId equal accountId, keyEpoch, protectionRevision, generation:null,
requestHash and integer Unix-second issuedAt/expiresAt exactly60 seconds apart.
No access session is created. Sign compact UTF8 JSON of this exact ordered array,
using the account unlock private key locally, P256/SHA256 and64-byte P1363 r||s:

```text
["cerberus-proof-v1",challengeId,nonce,operation,identityId,accountId,
 scopeKind,scopeId,keyEpoch,protectionRevision,generation,requestHash,issuedAt,expiresAt]
```

Submit the original prepared body to POST `/api/vault/unlock` with single-valued
`X-Cerberus-Challenge-Id` (canonical lowercase GUID) and `X-Cerberus-Proof` (canonical
base64url64-byte signature). Reformatting/reordering the body requires a new challenge.
No BOM, unsupported charset, compression, query or extra body fields are accepted.
The API captures bytes before deserialization; it never signs a reconstructed object.

Success200 returns public accountId, `vaultAccess`, issuedAt and nullable expiresAt.
Use the opaque handle in `X-Cerberus-Vault-Access` for account-wide operations. Only
its SHA256 verifier is stored. Current policy/generation is bound at issuance;
initial renewal policy expires after24 hours. Explicitly disabled renewal has null
expiry. This endpoint issues no offline lease or selected-profile access (UC15).

The proof must bind the current actor, account, scope, revision, epoch, challenge and
raw body. Invalid/missing/expired/future/consumed proof returns401; malformed visible
input400; hidden account404; stale state409; required dependencies503. Consumption
and session insertion are atomic; one concurrent use wins. Database failure rolls
back both. A lost unlock response may leave a consumed challenge and committed
session: request a new challenge and prove again; no raw handle can be recovered
from its stored verifier. Expired/consumed challenges are pruned on the next issuance.

## Encrypted client record contract

FR-KY-03..05 and BR08/09 apply inside ciphertext: every record requires a name and
may contain text, numeric, boolean and hidden-text fields; a selected template's
required fields must be checked locally before encryption. The API accepts no
plaintext record/key material and cannot enforce those obligations by decryption.
Native harness vectors validate the shared cryptographic wire contract; they do
not constitute the deferred independent security or real-client approval.
