# UC-05 identity details update design

Deliver issue #6 / FR-AC-10, BR02/BR03 using protected `PUT /api/identity/me`.
This operation updates Heimdall identity details independently of Cerberus encrypted
account details and vault profiles. Existing batch authorization supplies automated
stage gates. Actual independent protocol/client approval remains deferred for
development only and pending for release.

Freshly inspected reference: Heimdall PersonController.Update uses authenticated
`PUT /api/persons/{public-guid}` and UpdatePersonCommand replaces required name/email;
roleId is optional, and actor/route fields are injected by Heimdall. Its validator
requires nonempty name<=200 and valid email. The handler checks self/role permission,
email uniqueness, clears email verification when email changes and returns public
identity metadata. It supports no caller expected-revision field; business/provider
concurrency errors map to409. Do not invent a provider endpoint, version token or
admin operation.

Cerberus accepts a strict JSON body with only `name` and `email`, mirroring those
permitted changes. No role, password, scope, provider/person/account/profile ID or
vault payload fields are accepted. Require signed/current scoped identity plus
active non-erased account and current account-wide vault access, using the canonical
single `X-Cerberus-Vault-Access` header. Profile-selected, foreign, stale, revoked,
future or expired access is denied. Raw empty headers are malformed400, absence401.
Unknown query/body fields and duplicate JSON/header values are400.

Authorization store obtains the own account lock, then the matching session lock
in that order, without retrieving encrypted details. After acquiring locks, a single
SQL authorization decision checks active state, tombstones, account/session binding,
profile scope, current policy/generation, issuance, revocation and expiry using
PostgreSQL statement_timestamp(), not transaction-begin or captured request time.
This avoids authorizing a queued operation with stale time. No local account, session,
profile or envelope rows are updated. Execute the external update callback while
holding the account/session locks; lifecycle/revocation writers cannot win between
that permission decision and the external request. In-flight operations overlap an
erasure/provider identity change under the existing cross-service request boundary;
no shared transaction or remote rollback is claimed.

The adapter validates that the forwarded original actor token names the supplied
actor, sends only name/email to that actor's public person route, and never uses the
registration service credential for this operation. Require a successful, well-formed
response bound to the same public person, role3 and configured scope, with required
name/email/emailVerified fields. Return only id/name/email/emailVerified. Missing,
malformed, mismatched or credential-like responses fail closed without forwarding
provider payloads. Parse strict JSON/numbers and reject duplicate upstream members.
Map provider400/401/403/404/409 to stable redacted validation/auth/denial/not-found/
identity-conflict codes; timeouts, transport failures and incompatible results503.
Caller cancellation propagates.

Ruling: AF04 follows the inspected provider's own409 uniqueness/concurrency semantics;
no expected identity revision is invented because the actual PUT contract has none.
Cost if wrong: future provider versioning needs a versioned adapter/contract change.
Name/email replacement is retryable, but an unavailable/lost response can follow an
upstream commit; return503 and let the owner retry the same replacement or verify
identity state. Never claim an external rollback or mutate Cerberus vault state.

| Flow | Verification |
| --- | --- |
| Main | Controlled real Heimdall HTTP fixture updates only the actor's name/email, resets email verification, returns curated identity output and leaves Cerberus account ciphertext/revision/link/policy and sessions unchanged. |
| AF01 | Name/email boundaries, unknown role/ID/password/vault fields, duplicate/case/numeric JSON, query and malformed/duplicate/empty headers reject400 before callback. |
| AF02 | Missing/inactive/erased account and upstream absent person return404 without hidden content or vault mutations. |
| AF03 | Current identity, missing/foreign/profile/revoked/stale/expired/future access denied; callback never invoked; actor token and own public route asserted. |
| AF04 | Provider409 uniqueness/concurrency yields identity_conflict409; local vault unchanged. Lock contention proves closure/session revocation cannot interleave the external operation. |
| AF05 | Real database fault, provider outages/timeout/malformed output yield503 without local vault mutation; canceled callers propagate. |

Domain defines identity update result and authorized-callback store contract; Shared
owns the inspected HTTP adapter, Data owns current transactional permission, Command
validates/dispatches, WebApi derives actor/bearer/header and maps public statuses.
No migration or new runtime service. Family unit/functional tooling and real PostgreSQL
cover all flows. Full unfiltered suite,>=90% line coverage (report branch), generated
contract/helper/spec checks, one final code review and all required CI precede merge.
