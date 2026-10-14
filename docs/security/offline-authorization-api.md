# Offline authorization leases

UC47 adds `POST /api/offline-authorizations` for a human owner. Heimdall
authentication is revalidated online for every request. Supply one current
`X-Cerberus-Vault-Access` handle and an owned profile:

```json
{"profileId":"527a1001-8ef5-4c9b-a565-222222222222","expectedProfileRevision":3}
```

Both fields are required. JSON is case-sensitive, has no unknown or duplicate
members, and uses canonical lowercase UUIDs and positive safe integer revisions.
Transport accepts `application/json` with optional UTF-8 charset, without query
parameters, compression, a BOM or duplicate vault headers. The configured request
limit applies; oversized requests return an empty 413. Responses prohibit storage
through `Cache-Control: no-store`.

An account-wide session may narrow to the requested profile. A profile session
must select that same active owned profile. Missing, foreign, trashed or terminally
erased profiles are hidden. Expired/revoked sessions, stale policy or revocation
versions, future session issuance and incompatible disabled-policy expiry deny
renewal. A cached authorization lease cannot replace current online vault access;
unlock again before renewal when the online session expires or becomes stale.

## Signed scope and policy

The normal success envelope contains `lease`, `accountId`, `profileId`, `keyEpoch`,
`protectionRevision`, `policyRevision`, `revocationGeneration`, `grantRevisions`,
`renewalEnabled`, `issuedAt` and `expiresAt`. `expiresAt` is explicitly null when
renewal is disabled. No encrypted content, vault root, wrapper or signing private
key is returned. Issuance preserves all account/profile/grant/session state.

The compact JWS follows the [reviewed native lease contract](protocol-review.md#offline-policy-and-clock-lifecycle):
header fields are exactly `alg=ES256`, `typ=cerberus-offline-v1+jwt` and `kid`, the
RFC7638-style P-256 public JWK thumbprint used by the existing native clients.
Claims are `iss`, `aud=cerberus-offline-clients-v1`, `sub`, `accountId`,
`scopeKind=profile`, `scopeId`, `keyEpoch`, `protectionRevision`, `policyRevision`,
`revocationGeneration`, `grantRevisions`, `renewalEnabled`, `iat`, `nbf`, `jti` and
conditional `exp`. Identity/scope identifiers are canonical UUIDs; `jti` is fresh
for every issued lease. Times are integer Unix seconds and `nbf=iat`.

`keyEpoch` comes from the selected profile's saved native envelope metadata;
`protectionRevision` is its current profile revision, matching profile unlock
proofs. The store extracts the epoch in PostgreSQL without returning the profile
ciphertext to the application. Policy and revocation counters come from the
current owning account. Grant bindings are sorted unique `{grantId,revision}`
entries for currently active foreign collection grants associated with this
profile. Revoked/erased grants, unavailable collections and inactive/erased owners
are omitted. Corrupt active grant metadata fails closed. Local account-owned
collections do not add sharing grant bindings.

Enabled renewal uses the saved positive whole-second interval; the default is
86,400 seconds. `exp=iat+interval` and expiry is exclusive. Unsupported fractions,
corrupt intervals and unrepresentable expiry do not produce a lease. Disabled
renewal omits `exp` entirely from the signed claims, while retaining all current
identity, scope and permission checks during online issuance.

## Current authority and failures

The store captures metadata, locks the associated account set in public-ID order,
then the session, profile and associated collection/grant rows. Sharing/lifecycle
writers use the same account prefix. It rechecks metadata after each lock wait,
uses database statement time to authorize signing and rechecks authority after
signing before disclosure. A signer that outlives the online session cannot return
its minted lease. This database transaction does not provide a distributed
transaction with Heimdall; identity revalidation uses the established request-time
dependency contract.

| Status | Stable error / result |
| --- | --- |
| 200 | `offline_authorization_renewed`, signed current profile policy |
| 400 | `validation_failed`, malformed visible input/transport |
| 401 | `authentication_required` or `vault_access_required` |
| 403 | `vault_access_denied`, online vault authority is invalid |
| 404 | `not_found`, absent/hidden profile or account |
| 409 | `revision_conflict`, stale profile revision or state changed during a wait |
| 413 | Empty body, configured request limit exceeded |
| 503 | `identity_unavailable` or `persistence_unavailable`, including unavailable signing configuration |

## Signing configuration and client trust

Provision `CERBERUS_OFFLINE_LEASE_ISSUER` (a nonempty ASCII issuer) and
`CERBERUS_OFFLINE_LEASE_PRIVATE_KEY_PEM` (a private P-256 signing key) through
protected configuration. JSON aliases are `Cerberus:OfflineLeaseIssuer` and
`Cerberus:OfflineLeasePrivateKeyPem`. This key is separate from vault encryption,
recovery, recipient KEM and Heimdall JWT keys. The server generates no production
signing credential. Do not commit the PEM, put it in ordinary logs or include it in
client responses.

Both settings absent keep the existing API available but disable lease issuance
with 503. A partial or invalid configured pair fails configuration validation.
Deployments offering offline renewal must provision both before enabling the
feature. Configuration errors identify setting names without key material.

Clients establish issuer-bound public-key trust separately, through their trusted
deployment/provisioning channel. Neither a token nor an API field establishes its
own trust anchor. Verify the original signed bytes, fixed header and externally
expected identity/account/profile/epochs/counters/grants/times before use. Retain
old verification pins for outstanding leases during rotation. Disabled no-expiry
leases require retained trust or an online replacement; key retirement cannot
instantly revoke a disconnected client.

## Compliant client behavior and evidence

An authenticated online renewal establishes the server-time/monotonic clock
anchor. Enabled-expiry clients use at least the maximum of wall time and anchored
server time plus monotonic elapsed, retain the high-water time, and lock on expiry.
Clock regression or process restart requires fresh online renewal in the reviewed
model. Importing or reverifying a cached lease cannot establish an online anchor.

On reconnection, apply discovered policy/grant revocations and visibility removals
before accepting new synchronized content. An old disconnected lease retains its
last issued policy until reconnection or its existing expiry. Disabled periodic
renewal never exempts online revalidation. Online-only/web clients must avoid
persistent vault storage and caching. These are client obligations; this API does
not certify an external platform's clocks or storage behavior.

`scripts/offline_leases.py` runs the production .NET signer against independently
implemented native Python and Java verifiers in the pinned protocol environment.
Four public fixtures cover default, one-second, custom and disabled policy with
separate expected bindings/pins. Negative checks cover tampering, wrong trust,
changed policy, future issuance and exclusive enabled expiry. Required CI runs it
alongside the unchanged 322-case native corpus. Independent security/client
approval remains required before release.
