# Vault protection and account unlock API

Development contract for UC38–41. The independent protocol/client review is deferred
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

## Change the master vault protection (UC39)

Prepare strict JSON for PUT `/api/vault/protection` with exactly `accountId`,
`expectedProtectionRevision`, `expectedAccountRevision`, `mode`, `material`,
`contentReplacements`. Derive the actor from the bearer, never a JSON identity.
Use the current own account-wide `X-Cerberus-Vault-Access` handle. Obtain a fresh
`change-protection` challenge for the SHA256 of the exact prepared body and sign
with the **current old registered unlock key**. Submit its single-valued challenge
and proof headers with those original bytes. An unlock-purpose proof cannot change
protection, and a change-purpose proof cannot unlock.

Replace both complete password/recovery wrappers, each at protection-slot epoch+1,
using fresh keySalt/nonce and a fresh password KDF salt. Keep all four public key
pins and the recovery generation unchanged. Recovery identity changes belong to
recovery/refresh, and recipient/author identity transitions require their separately
specified authenticated transition. The server cannot inspect the encrypted bundle;
clients must validate that its inner roots and private/public correspondence remain
correct. Password change cannot destroy a recipient's already copied keys or data.

`mode:"rewrap"` requires `contentReplacements:[]`. It advances the protection revision
and slot epoch while preserving every existing account ciphertext byte and its
revision/content epoch. The slot epoch in wrapper context is distinct from an inner
content root's epoch, as demonstrated by the native harness rewrap vectors.

`mode:"rotate-content"` replaces the complete current authoritative content set
in the same transaction. Each replacement has `resourceKind`, `resourceId`,
`expectedRevision`, `envelope`. The current set is exactly the own account envelope;
use `resourceKind:"account"`, own `accountId`, the expected account revision and
content epoch equal to the current account envelope epoch+1. Missing, duplicate,
foreign or extra replacements fail400 before challenge consumption. The complete
pair of protection wrappers must wrap the locally rotated inner roots. Current
recipient-grant inventory is empty. Profile/content/grant creation flows must extend
this completeness invariant when those entities become available; this route does
not claim support for nonexistent selected profiles or grants.

Success200 returns only accountId, protectionRevision, keyEpoch (slot epoch),
recoveryGeneration and accountRevision. Content rotation increments the account
revision; rewrap preserves it. Both increment account revocation generation,
invalidating old handles/challenges. Request a new unlock challenge and prove again
to obtain new access. No handle or raw password/key is returned by a change.

Locks serialize the current account and presented own session; session/challenge
expiry is rechecked using fresh database statement time at proof consumption.
All wrapper, optional content, revision and revocation writes commit atomically.
Concurrent changes allow one winner; stale expected revisions return409. A failed
write rolls back consumption and all state. Missing handle401, malformed input400,
invalid current handle403, unbound/expired/consumed proof401, hidden target404 and
required dependency503 all preserve protection. After a lost response, retrieve
current opaque protection and compare the intended revision/wrappers before retrying;
an old expected revision cannot replace the winner or recover a plaintext key.

## Recover vault access (UC40)

Generate the replacement password/recovery wrappers and new independent recovery
key locally. POST `/api/vault/recovery` accepts exactly `operation:"recover"`,
`idempotencyKey` (canonical nonzero GUID), `expectedRevision` (current protection
revision), `passwordWrapper`, `recoveryWrapper`, and `newRecoveryVerifier`.
Both complete wrappers use slot epoch+1 and fresh keySalt/nonce/password KDF salt;
the recovery wrapper uses generation+1 and the new verifier fingerprint. The new
recovery key must differ from every current key role. Unlock, recipient and author
pins and all existing content bytes/revisions stay unchanged.

Obtain a `recover` challenge for the exact body's digest. Its generation is the
current recovery generation. Sign it locally with the **old registered recovery
private key**, then submit the original bytes with the canonical challenge/proof
headers. No existing vault-access session is required. The signed bearer `iat`
must be fresh on submission and after the account lock; current Heimdall identity
is revalidated on each request. This is signed-token freshness, not a stronger
provider authentication-event claim. Never transmit passwords, recovery secrets,
private keys or usable encryption roots. The API returns no secret or access handle.

Success200 returns exactly `status:"committed"`, `protectionRevision`, `generation`
and `revocationGeneration`. One transaction consumes the challenge, replaces both
wrappers and recovery verifier, increments slot/protection/recovery/revocation
counters and stores the nonsecret idempotent outcome. Old access sessions and
challenges become stale; the client unlocks again with the retained unlock key.
Only one competing distinct-key request can consume the current recovery credential;
the loser receives409 `recovery_credential_consumed`. A failed database write rolls
back everything, allowing the identical proof to retry.

After a lost response, retry the exact original bytes and idempotency key with
current fresh identity. The durable own-account/key/body-digest result is returned
before consumed, expired, cleaned-up challenge or current generation checks, even
if the signature has an equivalent valid representation. This is the **original
historical outcome**; GET latest protection before deciding what state is current.
Reuse of the key with different original bytes returns409. Identity freshness and
current account/provider authorization still apply to retries. Missing/invalid proof
401, malformed input400, hidden/inactive account404 and required dependency503
preserve state. The replacement recovery secret is displayed/stored only by the
client; neither the stored outcome nor a replay can recover it.

## Refresh the recovery credential (UC41)

PUT `/api/vault/recovery` accepts the same six fields as recovery, with exactly
`operation:"refresh-recovery"`. POST remains recover-only and PUT refresh-only.
Generate a new secret and independent recovery key locally; replace both complete
wrappers using slot epoch+1, recovery generation+1 and fresh visible contexts.
The native full-wrapper transition requires a fresh password wrapper too. A flow
preserving that wrapper needs a separately reviewed protocol. All content and
unlock/recipient/author pins stay unchanged.

For a new refresh, present current own account-wide `X-Cerberus-Vault-Access`, obtain
a `refresh-recovery` challenge (generation:null), and sign its original-byte binding
with the **old registered unlock key**. The old recovery key cannot refresh; the
unlock key cannot recover. Current provider identity and signed-iat freshness apply
at submission and after account/session locks. Session policy/revocation/scope and
exclusive identity/challenge/session expiry are checked again at the consumption
statement. Missing handle401, malformed400, denied current session403, invalid
proof401, hidden account404, stale revision/challenge409 and dependency503 preserve
all state. A changed policy makes a session bound to the old policy invalid403.

The complete wrappers/new recovery verifier, protection/slot/recovery/revocation
increments, challenge consumption and durable outcome commit together. Superseded
recovery fails for future new operations; new recovery works after refresh. Old
sessions are stale; prove a new unlock before another operation. No secret/access
handle is returned. Response fields match the four recovery outcome fields.

An exact completed refresh retry with fresh current owner identity and the original
canonical handle/body/key can return its historical outcome after the original
handle or challenge expires or is superseded. This native idempotency path performs
no new transition and does not authorize a new operation. Different body bytes or
purpose under the same key conflict409. GET current protection before acting on a
historical result. Current identity/provider/lifecycle and freshness always apply.

## Fresh recovery material read

GET `/api/vault/recovery-material` returns the same own public counters and opaque
protection material as GET protection, with fresh signed-iat/current provider
identity required. It needs no existing vault session, accepts no query/body,
creates no session and consumes no challenge or recovery credential. Responses
are no-store. Missing/hidden/closing/erased protection404, unfresh/invalid identity401,
malformed request400 and required dependency503. Local decryption, key verification
and replacement-secret presentation remain the client's responsibility.
