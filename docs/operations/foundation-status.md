# Foundation implementation status

Foundation issue #1 is implemented and reviewed through [PR #57](https://github.com/artur-rios/cerberus-api/pull/57).
The implementation and verification below describe the foundation snapshot of
2026-10-07. Current business delivery is tracked in the [README backlog](../../README.md);
UC-01 now exposes account registration. The API remains a development artifact.

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
