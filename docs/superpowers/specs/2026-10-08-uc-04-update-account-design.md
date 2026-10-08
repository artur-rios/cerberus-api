# UC-04 current account update design

Implement issue #5 / FR-AC-09 through protected `PUT /api/accounts/me`, using the
same current Heimdall actor and account-wide vault access boundary as UC-03.
Existing batch authorization replaces stage approvals; independent protocol and
client-operability reviews remain deferred for development only.

The strict JSON request has only `expectedRevision` (positive 64-bit integer) and
`details` (the existing supported six-field encrypted envelope). Caller owner or
identity IDs, unknown properties, duplicate fields, numeric strings, null/invalid
envelopes and query parameters are rejected with 400. Actor and the single canonical
opaque handle come from trusted middleware and the access header, never body fields.
Missing identity/access is 401; malformed/duplicate access is 400; invalid, revoked,
expired, profile-only, foreign or stale-policy/generation access is 403. Absent,
inactive or erased accounts are hidden with 404. No content is decrypted.

Data reads only public account metadata after finding the acting identity's account;
current envelope bytes are never needed. Reuse the same session predicates as UC-03.
For an active authorized account, reject mismatched expected revision with 409.
Perform one conditional UPDATE whose predicates recheck owner, active state, revision,
tombstones and the valid account-wide session, changing only details and revision.
The temporal predicates in this standalone UPDATE use PostgreSQL's current
statement/transaction time, never the earlier captured request time. That prevents
an access session which expires during preflight or command queuing from authorizing
a statement that starts after expiry. This statement is the mutation's linearization point: concurrent writers with the
same expected revision permit exactly one increment. Zero affected rows after the
initial authorization returns 409 and preserves the winning state. A closure,
revocation or erasure that wins before the update's snapshot prevents mutation.
Overlapping operations serialize at their database statement; future lifecycle
writers must retain these account/session predicates. Revision exhaustion returns
409 rather than overflowing. No credentials, identity links, policy, generations
or session records change. Retry after a lost success reloads the new revision;
replaying the old revision conflicts rather than applying another edit.

The handler validates metadata, hashes the canonical access handle, serializes only
the submitted encrypted envelope, and uses injected TimeProvider. The store contract
accepts trusted identity/verifier/expected revision/envelope/time and returns only
public ID/new revision or a stable error. Dependency errors map to redacted 503;
caller cancellation propagates. No new schema or session issuer is needed.

| Flow | Verification |
| --- | --- |
| Main | PostgreSQL + HTTP replacement returns incremented revision; identity link, policy, state and access session stay unchanged; subsequent GET returns the new envelope. |
| AF-01 | Strict body/envelope/revision/header/query cases reject400 without mutation. |
| AF-02 | Missing/inactive/erased own account returns404; no caller owner-ID override. |
| AF-03 | Current identity checks and missing/foreign/profile/expired/revoked/stale access deny without writes. |
| AF-04 | Stale expected revision and two simultaneous writers return409 for the loser; exactly one winning envelope/revision persists. Deterministic provider interception changes state/session/tombstone before conditional UPDATE and proves no write. |
| AF-05 | Actual database failure and controlled Heimdall outage return503 without mutation; cancellation propagates. |

All outputs carry no-store and contain only public account ID/revision on success.
Tests use the family host, controlled Heimdall, real PostgreSQL, actual hashing and
existing opaque-envelope vectors. Full unfiltered tests and >=90% line coverage,
reported branch coverage, spec/OpenAPI/helper checks, one final code review and all
required CI precede the authorized merge. Mark only UC04 done in README/project.
