# Foundation implementation status

Issue #1 is in progress. No backlog issue has been merged or closed. This is not a usable
vault application and must not be deployed for production traffic.

## Implemented and tested

- Six production layers and six mirrored test projects with central dependency versions.
- Required typed configuration validation, environment-key precedence, request byte limits
  and no-store response middleware.
- PostgreSQL context and initial migration, unique constraints, optimistic concurrency and
  transaction rollback tests using disposable PostgreSQL 18 containers.
- Strict Heimdall JWT issuer, audience, signature, expiry, scope and public-identifier checks;
  challenge tokens are rejected. This validator is not yet integrated into business routes.
- Private Linux erasure files with atomic commit and file/directory synchronization, plus
  duplicate-write, corrupt-record, interrupted-write and persistence tests.
- Retention work-item lease state transitions. Database claiming and hosted execution are not
  implemented yet.
- A protocol-review record validator. The actual record remains pending; synthetic approval
  fixtures used by tests are not real approvals.

The development host validates configuration and ledger storage before starting, registers
the database without implicitly migrating it, and exposes no business endpoints. Its single
console logging pipeline has no request/body logging.

Checkpoint verification on 2026-10-07: the unfiltered solution suite passed 49 tests, helper
tests passed 15, specification validation passed, and the dependency vulnerability scan found
no reported vulnerable packages. Fresh merged application coverage was 94.7% line and 81.5%
branch. The development container built successfully. These results do not establish the
unfinished foundation's Definition of Done or production readiness.

The checkpoint code review found and verified fixes for ledger auto-recreation, nonlocal HTTP
transport and protocol-approval artifact binding. Ledger storage must now be durably
preprovisioned; its absence fails startup without writing to an unexpected directory.

## Required decisions

1. Complete the independent security and client-interoperability review required by NFR-11.
   [Protocol proposal](../security/protocol-review.md) lists unresolved questions; no client
   compatibility vectors or review approval currently exist.
2. Choose explicit operator settings under Operations §3.
   [Proposed beta settings](proposed-beta-settings.md) are a review draft, not deployed values.

Batch execution must stop at these gates. A general instruction to implement the backlog
does not constitute an independent security review or approve unspecified deployment values.

## Remaining foundation work

- Scoped Heimdall HTTP adapters and fail-closed online authorization checks.
- Async repository abstractions, concurrent database work claiming, monitored retention
  execution and retry/crash recovery.
- Restore reconciliation and tested restoration of current authorization before traffic.
- Redacted failure-path logging tests, complete startup/security pipeline integration.
- OpenAPI generation/drift checks, automated coverage-tooling verification, final artifact
  verification and required CI. The 90% coverage gate must not be bypassed.

No dependent use case can start until the foundation Definition of Done is met. The per-issue
plan is [the backlog implementation plan](../superpowers/plans/2026-10-07-cerberus-backlog.md).
