# Foundation implementation status

Issue #1 is undergoing final verification and review. No backlog issue has been merged or closed. This is not a usable
vault application and must not be deployed for production traffic.

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

The development host validates configuration and ledger storage before starting, registers
the database without implicitly migrating it, and exposes no business endpoints. Its single
JSON console logging pipeline admits only deliberately redacted application events and
excludes framework/HTTP/EF payload logging. Restored startup reconciles before HTTP/worker
execution. Each future domain module must register its restore and idempotent retention
handlers; no domain lifecycle feature is considered implemented by these primitives.

Fresh local verification on 2026-10-07 passed 113 unfiltered solution tests, 23 helper tests,
specification validation, OpenAPI drift and formatting checks. Merged production coverage
is 95.3% line (953/1000), 80.4% branch (333/414), from six fresh collector reports, excluding
test/foreign assemblies. The updated development container built successfully. Final review
and required remote CI still determine foundation acceptance; these are not production or
client interoperability results.

The checkpoint code review found and verified fixes for ledger auto-recreation, nonlocal HTTP
transport and protocol-approval artifact binding. Ledger storage must now be durably
preprovisioned; its absence fails startup without writing to an unexpected directory.

## Required decisions

1. Complete the independent security and client-interoperability review required by NFR-11.
   [Protocol proposal](../security/protocol-review.md) lists unresolved questions; no client
   compatibility vectors or review approval currently exist.
2. Actual deployment provisioning and ownership/load checks remain before production traffic.
   [Beta settings](proposed-beta-settings.md) were approved as design inputs by the user's
   latest “Go ahead”; they are not secretly installed defaults or deployed values.

Dependent encryption/recovery implementation must stop at the review gate. IR-09 permits
the foundation to establish that gate while the review is pending. A general instruction to
implement the backlog is not evidence that an independent security/client review occurred.

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
