# UC-38 vault protection initialization design

Implement issue39 / FR-KY-01..05,13 and BR08/09/10/12 with protected
POST /api/vault/protection. The owner generates encryption roots, password/recovery
wrappers and separate asymmetric key pairs locally. The API accepts no vault password,
recovery secret, private key or plaintext record content. TLS is the deployed transport
boundary already required by Operations; isolated local fixtures remain HTTP.

Use the versioned proposal in docs/security/protocol-review.md and its native harness
contracts without changing algorithms. Owner explicitly deferred independent reviews for
development; manifest remains pending and strict main/tag release gate remains intact.
AF06 therefore blocks release while development proceeds under the recorded exception.

Initialization body: accountId (canonical nonzero public GUID), expectedAccountRevision
(positive safe integer), material containing passwordWrapper, recoveryWrapper,
unlockVerifier, recoveryVerifier, recipientKey and authorKey. No actor/role/scope/raw key
fields. Initial wrapper keyEpoch=1 and recovery generation=1. Password wrapper includes
exact argon2id-v1.3/65536KiB/3/4/16-byte salt metadata; both wrappers use fixed format,
32-byte keySalt,12-byte nonce,16-byte tag and nonempty canonical base64url ciphertext.
Recovery wrapper proofKeyFingerprint must match RFC7638 recoveryVerifier thumbprint.
All four public keys are distinct P-256 EC JWKs with exactly crv/kty/x/y and valid
32-byte curve coordinates. Never accept private d or unsupported algorithms/KDFs.

Only the current scoped Heimdall owner can initialize their active, non-erased account.
Lock the account, check its public ID and expected revision, and atomically create one
protection row per account. A concurrent initialization permits one winner; existing
protection or stale account revision returns409 without replacing the winner. No account
ciphertext/revision/policy is changed and initialization itself creates no vault session.
Return only accountId/protectionRevision/keyEpoch/recoveryGeneration, initially1.
A lost init response requires reading the current material; retry never replaces it.

Ruling: bootstrap cannot require a pre-existing vault session. Current owner authentication
plus active own account and expected revision authorize the first initialization; later
protected operations still require proof-based vault access. Cost if wrong: onboarding
would otherwise deadlock before the first session exists.

To make the newly registered account unlock verifier usable, include the directly dependent
account-unlock support routes in this UC rather than fabricate sessions in login:
GET /api/vault/protection returns only own encrypted material/public verifiers and current
protection metadata, never account details. POST /api/vault/challenges accepts operation
unlock-account and the canonical SHA256 requestHash of the intended raw unlock body.
It issues a random60-second challenge bound to current identity/account/account-scope,
keyEpoch/protectionRevision/policy/revocation state; no session is created.
POST /api/vault/unlock accepts only expectedProtectionRevision with single canonical
X-Cerberus-Challenge-Id and X-Cerberus-Proof headers. Hash the original strict UTF8 body
bytes before parsing; reject compression/BOM, duplicates/unknown fields and numeric coercion.
Verify exact cerberus-proof-v1 array bytes against the stored account unlock JWK with native
P-256/SHA256/P1363 verification. Atomically consume the challenge and create an account-wide
random32-byte handle, persisting only its SHA256 verifier and current policy/generation.
Exclusive expiry follows current account policy (initial24h); explicitly disabled renewal
uses no expiry. Wrong actor/proof/body, expired/future/consumed challenge never issues access;
stale current state returns409. Same challenge can commit only one session.

Ruling: support routes belong to UC38 because they make its newly registered scoped verifier
usable for account access; UC15 remains profile-only opening. Cost if wrong: route placement
may need reorganizing, but no unrelated profile, recovery, rotation or lease feature is added.
Ruling: challenge issuance requires a signed current Heimdall token whose signed iat is within
60 seconds, the challenge validity window; missing/future/older iat requires reauthentication.
Inspect actual family JwtHandler creation metadata and test boundaries. Cost if wrong:
clients need a fresh Heimdall login before issuing a challenge; future provider auth_time
support may justify a versioned freshness policy. Initialization/material reads require
current identity, not this additional freshness window.

Plaintext record name/type/template rules are client contract obligations inside ciphertext;
document them, never decrypt or accept plaintext in this API. Public GUIDs bind stored
metadata to authenticated account ownership; ciphertext integrity remains client AEAD/AAD
verification against trusted expected owner/scope/epoch. No server encryption/KDF is run.

| Flow | Verification |
| --- | --- |
| Main | Native wrapper/JWK validation; real PostgreSQL atomic ownership-bound initialization; real HTTP init/read/challenge/sign/unlock then account read; only verifier persisted. |
| AF01 | Unsupported/malformed wrapper/KDF/base64/JWK/private fields, reused key roles, mismatched fingerprint, duplicate/case/numeric JSON, query/header/body encoding reject400 without writes. |
| AF02 | Missing/foreign/inactive/erased account or absent own protection404, no hidden bytes. |
| AF03 | Missing/currently invalid/foreign-scope identity401; invalid proof/challenge401, current account-wide scope only. |
| AF04 | Stale revision/existing protection/concurrent init409 preserves winner; concurrent proof consumption permits one session; lifecycle/policy rechecked under account lock. |
| AF05 | Database/Heimdall failures503; cancellation propagates; rollback leaves no partial protection/challenge/session. |
| AF06 | Owner development exception recorded; actual manifest pending and strict release verifier still rejects it. |

Domain owns strict metadata/native proof contracts; Data persists opaque material and atomic
permission/proof state; Command/Query validate typed inputs; WebApi supplies trusted actor,
fresh signed claims and original body bytes. Additive migration cascades protection/challenges
with account erasure. Full suite unfiltered,>=90% line/report branch, helper/spec/OpenAPI and
one fresh final code review plus all required CI gate the authorized develop merge.
