# Permanent account deletion

UC08 explicitly and irreversibly deletes the current owner's Active account.
Send `DELETE /api/accounts/me/permanent` with a freshly revalidated Heimdall human
bearer token, current account-wide `X-Cerberus-Vault-Access`, and exactly:

```json
{"expectedRevision":1}
```

The signed token must contain one positive `iat`, less than 60 seconds old and
not in the future. The revision must match. A selected-profile session does not
authorize deleting the whole account; unlock account-wide first. Strict bounded
UTF-8 `application/json` rejects missing, extra or duplicate fields, numeric
strings, queries, compression and duplicate access headers. Responses are no-store.

Canonical account, collection and session locks precede database-time admission.
The existing closure reduction separately rechecks identity freshness before
revoking every session/challenge, incoming/outgoing native grant and software
grant. A wait crossing either authority boundary rejects and rolls back; natural
expiry after the relevant successful guard does not undo that admission. Account
revision and revocation generation advance, and affected outbound recipients'
generations advance once. Foreign owners' collections, content and sibling grants
remain intact. Copied client data cannot be recalled.

One transaction commits typed terminal markers for the account and every owned
profile/record/folder/collection plus affected grants, completes recoverable
closure work, and schedules immediate `account-erase/<UUID>` work. Readback
verifies the jobs, closed owner, intended markers and unrelated collateral before
commit. The account is inaccessible and cannot cancel closure or reuse identifiers
from that point, including after a failed response or caller cancellation.

All typed entries are flushed to the independently preserved Linux erasure ledger
before physical deletion. A live persisted claim, checked against database time
at every mutation, fences the purge. Owned ciphertext, trash, protection/recovery,
sessions/challenges, software provisioning, synchronization and registration
metadata and owned resource work are removed. Incoming/outgoing grants and their
links disappear; other owners' data remains. Minimal terminal identifiers and the
completed erasure work remain. The linked Heimdall identity is retained and can
still authenticate without a Cerberus account. No Heimdall deletion is called.

A successful response contains only `accountId`, `deletedAt` and `state` inside
the standard output envelope. `200` with `completed` confirms physical erasure;
`202` with `pending` means erasure is tracked for worker execution. Required
identity/persistence/ledger outages return safe `503`; committed intent remains
queued and is retried after its claim expires. Correct the dependency and restore
the worker; never remove markers to recover access. A repeated request after
committed intent or physical erasure returns nonrevealing `404`.

Other errors are `validation_failed` (400), `authentication_required` or
`fresh_authentication_required` (401), `vault_access_denied` (403), `not_found`
(404), `revision_conflict` (409), oversized body (413), and safe dependency failure
(503). A pending recoverable closure is a state conflict for this Active-account
endpoint. Expired-closure scheduling belongs to UC53.

Account ledger replay physically removes restored owned trees before opening the
restore traffic gate, retains foreign shared content and reserves typed IDs.
Actual PostgreSQL dump/replay fixtures and race/fault tests verify these server
behaviors. Independent security/client release qualification remains separately
gated; development review does not certify a production restore drill.
