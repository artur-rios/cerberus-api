# Cancel account closure

UC07 restores only the current owner's unexpired pending closure. Ordinary login
continues to reject inactive Cerberus accounts. Use the restricted identity entry
points to authenticate through the retained Heimdall identity:

- `POST /api/auth/closure/login` with exactly required `email`, `password`.
- `POST /api/auth/closure/2fa/verify` with required `challengeToken` and exactly one
  nonempty `code` or `recoveryCode` when Heimdall requires an MFA challenge.

A challenge returns no Cerberus account or completed token. Successful recovery
returns Heimdall's existing signed human token and only `accountId`, `revision`,
`closureDeadline`, `serverTime` for the unexpired pending account. Cerberus issues
no identity or vault token. Active, absent or erased accounts cannot use this
entry point; an expired own closure is rejected. Credentials/MFA, current identity,
signature, configured scope, actor binding and a single fresh signed `iat` are
validated before recovery metadata is returned.

Present that bearer token to `POST /api/accounts/me/closure/cancel`:

```json
{"expectedRevision":2}
```

The revision must match the pending account. The acting account comes from the
current revalidated Heimdall identity. No decryption key or vault handle is needed.
The request requires a signed issue time less than 60 seconds old and not in the
future, using the established signed-token freshness contract. Reauthenticate
through Heimdall when that window has passed.

The response contains only `accountId`, `revision`, `revocationGeneration`,
`cancelledAt`, `serverTime`. All three endpoints require strict bounded UTF-8
`application/json`, reject extra/missing/duplicate fields, query parameters,
compression, unsupported media and duplicate access headers, and return no-store
responses. Cancellation also rejects numeric strings and unsupported revisions.

The transaction restores Active, advances account revision/revocation generation,
clears closure times and atomically completes the due purge work while clearing
claims. Authorization uses database time after lock waits and requires the
original deadline to be strictly in the future. A stale claimed worker cannot
complete the cancelled work. Counter/storage faults or caller cancellation roll
back all changes; erased account markers can never be revived.

Encrypted data and protection remain retained. Old sessions, consumed challenges,
revoked grants and erased software provisioning remain unchanged. Authenticate
normally and perform a new vault unlock after cancellation. Shared/software access
requires explicit provisioning again. Existing disconnected copies retain their
previous policy bounds; current online access checks continue to enforce revocation.

Concurrent cancellation has one winner. A retry after success receives a state
conflict; obtain current account metadata through normal fresh login and start a
new unlock instead of reusing old access. An explicit later closure creates a new
30-day window and resets the old completed purge work; a closure retry itself
never extends the original deadline.

Cancellation errors are `validation_failed` (400), `authentication_required` or
`fresh_authentication_required` (401), `not_found` (404), `revision_conflict` (409),
`closure_expired` (410), oversized body (413), safe identity/persistence failure
(503). Recovery authentication also propagates known `authentication_forbidden`
(403) and returns no token/account data on failure. Independent security/client
release qualification remains separately gated.
