# UC-03 current account read design

Implement issue #4, FR-AC-08 and every UC-03 alternative flow through protected
`GET /api/accounts/me`. Existing batch authorization supplies automated stage gates;
independent protocol/client approval remains deferred for development only.

Resolve the actor from the existing signed/current Heimdall authorization middleware,
never from request identifiers. Return only that identity's active, non-erased account,
with public GUID, revision, state and opaque six-field encrypted details. Do not return
identity profile data, internal IDs, credentials, key material or another owner's account.

Enforce the use case's vault-access precondition with a single-valued
`X-Cerberus-Vault-Access` header, containing a canonical unpadded base64url encoding of
32 random bytes. Store only its SHA-256 verifier. The online session is separately
bound to the account, optional internal profile scope, policy revision and revocation
generation, issuance time, nullable expiry and revocation state. Account details need
an account-wide session; a profile-selected session cannot authorize this account scope.
Reject future issuance and expiry at the exact deadline. Enabled renewal requires an
expiry; disabled renewal requires explicit no-periodic-expiry. Current policy/generation
must match, and both account state and terminal tombstones are checked in the same query.

Ruling: add the read-side session persistence/verification primitives now because UC-03
requires valid selected access. Issuance/unlock remains UC-38 (and profile issuance UC-15);
neither identity login nor this read route creates a session. Functional tests provision
trusted persisted sessions directly as the specified precondition, using actual hashing,
current identity and PostgreSQL. Cost if wrong: later issuance may need to extend the
session schema; this route stays closed until a valid trusted session exists. This is
not an independent cryptographic or client-operability approval.

The GET contract accepts no query parameters or body. Missing vault access returns401;
malformed/duplicate access header or unexpected visible input returns400; invalid, expired,
revoked, stale-policy/generation, wrong-account or profile-only access returns403.
Absent/inactive/erased accounts return nonrevealing404. Required current identity/persistence
outages return stable redacted503. No denial writes state or returns encrypted content.

Domain owns handle validation/session and store result contracts. Data performs a single
no-tracking query whose conditional projection retrieves details only when current account
and session predicates permit it. Query handler validates input, uses injected TimeProvider,
parses only envelope metadata and returns a public output through QueryMediator. Corrupt
stored envelope metadata fails closed with503; no content decryption occurs.

| Flow | Evidence |
| --- | --- |
| Main | Real HTTP + PostgreSQL valid account-wide session returns exact encrypted details and public state; no internal identity or usable keys. |
| AF-01 | Canonical handle/parser unit boundaries and HTTP unknown query/body, duplicate/malformed header reject400 without content or writes. |
| AF-02 | Missing account, inactive state, tombstone and actor isolation tests return404 without another owner's data. |
| AF-03 | Missing/invalid identity returns401; missing access401; wrong scope/owner, profile-only, revoked, stale or expired session403; current identity revalidation still required. |
| AF-04 | Actual PostgreSQL fault and controlled identity outage return503 without details; caller cancellation propagates. |

Alternative: authorizing solely by identity would bypass the stated vault-access precondition.
Keeping sessions in memory would lose revocation/restart behavior. Persisted opaque verifiers
and current account checks preserve the boundary without storing client decryption keys.
