# Request account closure

UC06 exposes `POST /api/accounts/me/closure`. Present a current Heimdall human
bearer token and strict UTF-8 `application/json` containing exactly:

```json
{"expectedRevision":1}
```

The acting account is derived from the validated identity. Closure requires one
signed `iat` claim, not in the future and less than 60 seconds old; the database
checks freshness again after lock waits and at the closure mutation. This is
signed-token freshness, not a stronger claim of password reauthentication.
Closure reduces access and requires no decryption key or vault unlock handle.
The body revision must be a positive safe integer. Unknown/missing properties,
duplicate JSON fields, query parameters, compression, unsupported media types,
duplicate access headers and oversized bodies are rejected.

The response contains only `accountId`, `revision`, `revocationGeneration`,
`closureRequestedAt`, `closureDeadline`, `serverTime`, and `alreadyPending`.
Responses use `Cache-Control: no-store`. Closure has a fixed 30-day cancellation
window. A freshly authenticated retry returns the original deadline, including
after the original access session has been revoked. It never extends retention.

One transaction marks the account `ClosurePending`, advances its revision and
revocation generation, revokes every online vault session, consumes pending
unlock challenges, revokes incoming/outgoing collection grants and removes their
recipient wrappers. Each affected outbound recipient's generation advances once.
Own software grants are revoked and their recipient key/target provisioning is
erased. Encrypted account, protection and resource data remain retained; unrelated
owners and grants remain unchanged. A purge work item is due at the original
closure deadline. The account purge/expiry use cases supply its execution.

Pending accounts cannot unlock, read native vault data, synchronize or renew
online authorization. Cancellation requires the restricted fresh identity path;
new sessions and explicit grant provisioning are necessary afterward. Previously
issued disconnected client leases cannot be recalled: clients enforce their
existing policy bounds and must reject revoked access on reconnection. This
server change does not replace the independently gated client/protocol review.

Errors are `validation_failed` (400), `authentication_required` or
`fresh_authentication_required` (401), `not_found` (404), `revision_conflict` (409),
body too large (413), and safe identity/persistence dependency failures (503).
Storage faults and caller cancellation roll back the lifecycle change, reductions
and purge work together. A retry after a lost success response observes the same
pending closure without creating new access.
