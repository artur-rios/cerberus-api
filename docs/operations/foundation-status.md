# Foundation implementation status

UC45 adds the [owner-only offline policy read](../security/offline-policy-api.md)
at `GET /api/accounts/me/offline-policy`. Current identity, account lifecycle and
account-wide or valid own-profile vault authority protect one metadata-only
snapshot. It returns the saved renewal setting/duration and policy/revocation
versions, defaults to 24 hours, and fails closed on corrupt policy or unavailable
dependencies. It issues no lease and reads no ciphertext. The owner authorized
continuing M-05 while UC43 remains blocked on Heimdall #178.

Foundation issue #1 is implemented and reviewed through [PR #57](https://github.com/artur-rios/cerberus-api/pull/57).
The implementation and verification below describe the foundation snapshot of
2026-10-07. Current business delivery is tracked in the [README backlog](../../README.md);
UC-01 now exposes account registration. The API remains a development artifact.

UC44 adds [owner-only software access revocation](../security/software-access-revocation-api.md)
through strict `DELETE /api/software-grants/{grantId}` with exactly one expected
revision. Current human identity and vault-session authority guard one
Active-to-Revoked compare-and-set transition; account-owned grant administration
also accepts a current selected-profile session without widening its selection.
The transaction advances the revision, empties the recipient public key and
deletes every typed record/collection target and recipient envelope. Affected-row
and post-write checks reject partial erasure or collateral changes before commit.
Vault data, unrelated grants and owner sessions/generation remain unchanged.
Success returns only grant ID/revision. Command, PostgreSQL store/concurrency and
HTTP/OpenAPI test families cover strict transport, nonrevealing denial, captured
state races, UC42 reactivation, rollback/clean retry, no-store and non-disclosure.
This path makes no Heimdall application call; UC43 remains blocked. Copied
ciphertext cannot be recalled, UC48 offline delivery remains separate, and
independent security/protocol/client review remains a release obligation.

UC42 adds [explicit owner-only software grants](../security/software-access-api.md)
through strict `POST /api/software-grants`. A scoped Heimdall application lookup
precedes the guarded PostgreSQL transaction. Current owner/selected-profile scope
and every native target/epoch/software-key binding validate before one normalized
grant and complete typed target set commit. Exact revoked reactivation replaces
the whole key/scope at the next revision; active replay conflicts. Success returns
only grant ID/revision, with no vault-resource mutation. HTTP tests prove strict
transport, upstream classifications, native foreign denials, selected access,
rollback and clean retry. UC44 revocation is recorded above. UC43 software
authentication/retrieval remains blocked; UC48 offline delivery, cross-service
deletion revalidation and independent security/protocol/client and production
qualification remain release obligations.

## Implemented and tested

- Six production layers and six mirrored test projects with central dependency versions.
- Required typed configuration validation, environment-key precedence, request byte limits
  and no-store response middleware.
- PostgreSQL context and initial migration, unique constraints, optimistic concurrency and
  transaction rollback tests using disposable PostgreSQL 18 containers.
- Strict Heimdall JWT validation and controlled scoped HTTP adapters; protected endpoint
  middleware rejects challenge tokens and requires current online identity revalidation.
- Private Linux erasure files with atomic commit and file/directory synchronization, plus
  duplicate-write, corrupt-record, interrupted-write and persistence tests.
- Async PostgreSQL repositories, exclusive work leases, fenced completion, bounded hosted
  execution, retry/crash recovery and redacted failure monitoring.
- Erasure replay and current authorization verification before restored traffic, with a
  tested actual PostgreSQL dump/restore procedure and generation-safe traffic barrier.
- Explicit configuration/migration/reconciliation CLI commands that do not start HTTP.
- Generated OpenAPI drift checks, fresh merged production coverage, vulnerability scanning,
  separate test categories plus unfiltered CI tests and a non-root Linux container.
- A protocol-review record validator. The actual record remains pending; synthetic approval
  fixtures used by tests are not real approvals.

At that foundation snapshot, the development host validates configuration and ledger storage before starting, registers
the database without implicitly migrating it, and exposes no business endpoints. Its single
JSON console logging pipeline admits only deliberately redacted application events and
excludes framework/HTTP/EF payload logging. Restored startup reconciles before HTTP/worker
execution. Each future domain module must register its restore and idempotent retention
handlers; no domain lifecycle feature is considered implemented by these primitives.

Fresh local verification on 2026-10-07 passed 124 unfiltered solution tests, 23 helper tests,
specification validation, OpenAPI drift and formatting checks. Merged production coverage
is 95.2% line (974/1023), 80.9% branch (356/440), from six fresh collector reports, excluding
test/foreign assemblies. The updated development container built successfully. Required
remote CI gates every merge; these are not production or
client interoperability results.

The checkpoint code review found and verified fixes for ledger auto-recreation, nonlocal HTTP
transport and protocol-approval artifact binding. Ledger storage must now be durably
preprovisioned; its absence fails startup without writing to an unexpected directory.
The final whole-branch review found dependency-outage status mapping and timer-range defects;
the fix pass reproduced both and verified 503 versus 401 and the full supported timer range.
Malformed database configuration was also reproduced and fixed: validation parses provider
settings without connecting or exposing their values. The complete suite then passed again.

## Required decisions

1. Complete the independent security and client-operability review before release.
   [Protocol proposal](../security/protocol-review.md) lists unresolved questions. The
   executable reference harness and compatibility vectors exist; actual approval remains pending.
2. Actual deployment provisioning and ownership/load checks remain before production traffic.
   [Beta settings](proposed-beta-settings.md) were approved as design inputs by the user's
   latest “Go ahead”; they are not secretly installed defaults or deployed values.

The owner's [explicit development deferral](../security/development-review-deferral.json)
allows backlog implementation while review is pending. The strict main/tag release gate
remains; this deferral is not evidence that an independent review occurred.

## Foundation requirement evidence

| Requirements | Evidence |
| --- | --- |
| IR-01, IR-02, IR-07 | Six layers/mirrored tests; pinned compatible stable dependency graph; .NET 10 build and real PostgreSQL fixtures. |
| IR-03, IR-06, IR-14 | Explicit configuration precedence/validation, secret-safe failures/logs, no-store and bounded HTTP bodies, including concurrent oversize requests. |
| IR-04, IR-10 | Snake-case migrations, async repositories, uniqueness/concurrency/rollback tests, real blank-database migration CLI and non-root artifact. |
| IR-05 | [Inspected Heimdall contracts](heimdall.md); strict JWT, scoped registration and current identity/scope fixtures. |
| IR-08, IR-09 | Full CI/coverage/OpenAPI gates; review validator fails closed for pending, altered or incomplete approval evidence. |
| IR-11, IR-12, IR-13 | Private external durable ledger, [isolated restore runbook](restore.md), actual dump/restore replay, current authority checks, exclusive timed claims/retry and monitored worker. |

32 concurrent oversize HTTP requests verify the configured host rejects each at its byte
bound with no-store responses. Bounded real-database work queries and concurrent claim tests
exercise infrastructure limits; no production SLO or encrypted sync/export load benchmark is
claimed. Real client payload/device measurements belong to protocol review and later UCs.

No dependent use case can start until the foundation Definition of Done is met. The per-issue
plan is [the backlog implementation plan](../superpowers/plans/2026-10-07-cerberus-backlog.md).

UC23 adds [encrypted owned folder creation](../security/folder-api.md) using the
existing folder schema and complete protection inventory. Parent ancestry and
current selected authority are checked again at the guarded insert; partial
folder/link/parent writes roll back together. Independent protocol/security and
real-client qualification remain deferred for development only.

UC24 adds [encrypted folder listing](../security/folder-api.md#list-encrypted-folders-uc24)
with native RO/RW member-folder descendants and current selected-profile scope.
One SQL snapshot admits permitted folders before ancestry, native evidence and
pagination; record-only authority cannot expose a containing folder. Reads are
no-store and make no mutations. Independent security/client and production
qualification remain outstanding release gates.

UC25 adds [encrypted folder detail](../security/folder-api.md#get-an-encrypted-folder-uc25)
with current owned/native recipient scope and only permitted profile/collection/parent
references. A target-first database snapshot checks complete ancestry and all native
contributors, with no locks or mutations. Record-only access cannot expose its container.
Formal security/client and production release qualification remain outstanding.

UC26 adds [encrypted folder content replacement](../security/folder-api.md#update-encrypted-folder-content-uc26)
for current owner/native read-write scope. Expected revision and a final current
permission guard preserve winning edits; read-only recipients cannot write.
Only target ciphertext/metadata advance, with no parent/child/association changes.
Formal security/client and durable offline release qualification remain outstanding.

UC27 adds [recoverable owned folder cascades](../security/folder-api.md#recoverably-delete-a-folder-uc27)
with retained opaque folders/records, typed association snapshots and one720hour
operation/queue. Independent trash keeps its deadline; active recipient reads
hide deleted content while grants remain active. Complete protection rotation
includes retained members. UC48 durable inherited visibility and UC50–53 trash
listing/restore/empty/physical expiry remain release obligations; missing trash
handlers currently retry. UC35 below records the completed implicit recipient-account
FK audit against strong owner account writers. Shared metadata and independent formal
security/protocol/client qualifications remain development-only deferred.

UC28 adds [owned folder parent moves](../security/folder-api.md#move-a-folder-uc28).
Every active affected folder/record retains its direct associations and opaque
content while selected access rejects new effective profile/collection scope.
Current result-native grants, including contained direct records, require exact
fresh bindings; the server does not fabricate keys or certify client decryption.
Only root and distinct changed immediate parents advance metadata. Independent
trash/deadlines stay unchanged. Current GET/list inheritance recalculates, while
UC48 durable inherited visibility and independent client qualification remain
release obligations. Collection-before-content locking aligns existing recipient
editors; UC35 below records the completed implicit recipient-account FK audit.

UC29 adds [owned encrypted collection creation](../security/collection-api.md).
Current selected access permits only already-visible owned members; foreign native
RO/RW content cannot become an owned collection member. Collection, typed initial
links and each distinct directly affected profile/folder/record counter commit
atomically. Existing opaque content, keys, grants, recipient metadata and prior
trash remain exact. New collections enter complete native protection rotation.
UC48 durable inherited visibility, external key provisioning/decryption, shared
OpenAPI metadata and independent security/protocol/client release qualification
remain obligations. UC35 below records the completed recipient-account FK audit.

UC31 adds [encrypted collection detail](../security/collection-api.md#get-an-encrypted-collection-uc31)
with exact selected-profile collection admission, current native RO/RW bindings,
actor-only profiles and sorted direct typed members. One target-first read-only
snapshot walks only relevant upward ancestry, preserves explicit overlapping
membership and omits hidden/incomplete members without descendant expansion.
Required native, ancestry or projection corruption fails without partial output;
unrelated corruption stays isolated. Actual creation/access HTTP flows cover strict
transport, current permission removal and no mutation. Independent security/protocol
and external-client qualification remain release obligations.

UC32 adds [owner-only collection updates](../security/collection-api.md#update-an-owned-collection-uc32).
Wide or directly linked selected owners may replace opaque envelopes at the current
revision without changing key epoch, membership, grants or recipient wrappers.
Visible native RO/RW recipients receive 403 only after their relevant signed
evidence is validated; hidden targets receive 404. Owner updates are independent
of grants. Account/session/selection/target locks, current guarded authority and
post-write verification protect lifecycle races, lost retries and rollback.
All HTTP responses are no-store; successful updates return only collection ID,
revision, server sequence and normalized UTC edit time. Shared OpenAPI requiredness,
wrapper and empty-413 metadata limitations remain; runtime HTTP tests define those
contracts. Independent security/protocol and external-client qualification remain
release obligations.

UC33 adds [recoverable owner-only collection deletion](../security/collection-api.md#recoverably-delete-a-collection-uc33).
One atomic transaction retains the collection for 720 hours, removes all typed
memberships, bumps active directly linked owned resources and revokes active
grants while preserving their wrappers. Records/folders and direct noncollection
access survive; recipient online visibility disappears without revoking sessions.
The restricted trash snapshot keeps public association IDs, including recipient
profile links. UC51 must revalidate restored links and never reactivate revoked
grants; UC53 must supply collection physical expiry. Missing retention handlers
currently retry. Retained collections stay in complete protection rotation.
UC48 durable offline visibility removal, shared OpenAPI metadata and independent
security/protocol/client qualification remain release obligations. UC35 below
records the completed recipient-account FK audit.

UC34 adds [atomic collection membership replacement](../security/collection-api.md#replace-collection-membership-uc34).
An owned collection's direct records/folders and same-epoch opaque client key
manifest change together. Wide and selected native owner flows are supported;
selected handles can retain/promote currently visible members but cannot import
private scope. All active target grants validate before membership changes, while
their revisions and wrappers remain exact. Only direct symmetric-difference
members advance structural metadata. Current recipient GET/list visibility
recalculates immediately, including dynamic descendants. HTTP integration covers
strict transport, current authority precedence, native recipient denials, one
same-revision winner, rollback and clean retry. UC48 offline removals, client
manifest/decryptability qualification, shared OpenAPI metadata and independent
protocol/security approval remain separate obligations. UC35 below records the
completed recipient-account FK audit. Operational retry guidance is in the linked
API contract.

UC35 adds [owner-only collection access grants](../security/collection-api.md#grant-collection-access-uc35).
Initial RO/RW grants and explicit same-ID revoked regrants validate current native
owner/recipient pins, identity, collection epoch and grant revision. The mandatory
recipient-account FK audit is complete: PostgreSQL reproduced the implicit
key-share reciprocal deadlock, and the writer now locks both accounts together
in public-UUID order before any collection/grant lock. Current authority is
rechecked after waits and at the guarded mutation. One grant row and its exact
direct-member output commit or roll back together, without collection/member or
recipient protection/profile/session changes. HTTP integration proves strict
transport, selected-profile isolation, regrant and immediate recipient GET/list
visibility. UC37 revocation is recorded below; UC48 durable offline
visibility, authenticated recipient-key discovery/client decryptability, shared
OpenAPI metadata and independent security/protocol approval remain separate
obligations. The linked API contract records conflict and clean-retry guidance.

UC36 adds [owner-only collection permission changes](../security/collection-api.md#change-collection-access-uc36).
The exact active grant keeps its identity, collection and recipient while access,
native next-revision envelope, revision and concurrency stamp change. The same
guarded statement increments recipient account revocation generation exactly once;
both mutations commit or roll back together. Ordered strong dual-account locks
precede session, selected profile, collection and grant locks. Current authority,
native pins and stored grant evidence are rechecked before replacement. HTTP tests
prove old wide/selected recipient handles become stale, real fresh unlocks retain
read visibility, read-write permits member content edits and read-only rejects
them without mutation. Selection remains exact; revoked grants require POST
regrant. This is online account-wide handle invalidation. UC37 explicit revocation
is recorded below. UC48 durable offline changes, external key discovery/decryptability, shared
OpenAPI metadata and independent security/protocol/client approval remain separate
release obligations.

UC37 adds [owner-only collection access revocation](../security/collection-api.md#revoke-collection-access-uc37).
The strict DELETE action consumes the current grant revision and returns only
collection ID, grant ID and the advanced revision. One guarded PostgreSQL statement
revokes the active grant, erases its stored recipient wrapper and increments the
recipient account generation exactly once. Canonical account-pair locks serialize
adjacent lifecycle, protection, session and sharing writers. Post-write checks
reject partial or collateral mutations; failures roll back and permit clean retry.
The owner reduction path does not depend on parsing the old recipient wrapper.
Current visible foreign administration still requires valid native authority
before denial; hidden targets remain nonrevealing.

Real HTTP/native integration covers read-only and read-write grants, wide and
selected owner/recipient handles, pre-revocation permissions, stale old handles,
fresh-unlock absence, denied direct/inherited member writes and unrelated owned
data survival. Existing selected profile links are retained but inert. An explicit
UC35 regrant at the revoked revision requires a new native envelope and restores
only the profile scope already linked. Generation invalidation is account-wide:
even handles used for unrelated owned data must be unlocked again. Revocation
cannot destroy copied roots, ciphertext or plaintext, does not perform client key
rotation, and does not implement UC48 durable offline permission delivery. Shared
generated OpenAPI metadata limits and independent protocol/security/client review
remain release obligations.
