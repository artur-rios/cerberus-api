# Expire retained deletions Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox syntax for tracking.

**Goal:** Purge expired trash and uncancelled account closures safely through the existing internal retention worker.
**Architecture:** Original durable jobs, PostgreSQL-time admission and claim fences, typed database intent, complete filesystem ledger, then atomic physical erasure. A bounded pre-pass repairs missing due jobs and cleans abandoned streams.
**Tech Stack:** .NET 10, EF Core, Npgsql, PostgreSQL 18, xUnit/Testcontainers.
**Spec:** docs/superpowers/specs/2026-10-15-uc-53-expire-retained-deletions.md

## Global Constraints
- Original exclusive retention window: 30 days = 720 hours; UTC microsecond timestamps.
- No public worker route, human impersonation, Heimdall mutation, migration or new dependency.
- No database transaction spans durable ledger I/O; persisted claim and database time guard mutations after waits.
- One issue/branch/PR at a time; full unfiltered Release suite and fresh six-assembly >=90% line coverage before merge.
- Existing user authorization replaces human gates; verification remains mandatory.

## Review Focus
1. Old restored backups missing queue rows: reconstruct only due current work without bypassing a restore/cancel winner (Task 4).
2. Same UUID in different resource kinds or corrupt operation ownership: no foreign/active erasure (Task 2).
3. Lease expiry during actual SQL or ledger I/O: no stale mutation/completion (Tasks 1–3).
4. Foreign grant graph and account cancellation race: no late lock inversion, unrelated payload changes or account revival (Task 3).
5. Expired sync streams locked by downloads and active-account idempotency retention: skip busy streams and preserve sync operations (Task 4).

### Task 1: Database-time expiry queue and preparation contract
**Files:** Domain/Operations/IRetentionMaintenance.cs; Shared/Operations/RetentionRunner.cs; Data/Operations/RetentionWorkStore.cs; Data.Tests/RetentionExpiryQueueTests.cs; Shared.Tests/RetentionRunnerPreparationTests.cs (under existing full project paths).
**Interfaces:** Produces `IRetentionMaintenance.PrepareAsync(int pageSize,CancellationToken):Task<RetentionPreparationResult>` with ScheduledJobs/ExpiredStreams counts; optional fourth runner constructor dependency. Existing two positional run-result fields and three-argument constructor remain compatible; new init counts default zero. Expiry trash/account-purge queue methods use persisted DB UTC; generic work retains injected clock behavior.
- [x] Write tests: a future original expiry job cannot be claimed with a fast caller clock; due expiry can be claimed with a slow clock; stale DB lease cannot complete with a slow clock. Add runner preparation-before-discovery, no-op, invalid bounds, cancellation and failure/count tests.
- [x] Run focused tests; Expected: behavioral failures showing caller-clock bypass/missing preparation.
- [x] Implement DB-time due/claim/completion for the two expiry key families, runner validation and optional preparation contract, preserving existing generic-clock tests.
- [x] Run `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --filter 'FullyQualifiedName~Retention' --no-restore` and Shared.Tests equivalent; Expected: all PASS.
- [x] Commit `feat: fence retention expiry queue with db time`.

### Task 2: Original-operation trash expiry
**Files:** Data/Erasure/TrashExpiryHandler.cs; Data/Trash/TrashErasureInventory.cs; Data.Tests/TrashExpiryHandlerTests.cs and focused partial test files.
**Interfaces:** Consumes Task 1 claim semantics; produces public `TrashExpiryHandler(factory,ledger):IRetentionHandler`, Kind `trash`, ExecuteAsync(RetentionClaim,CancellationToken). Adds optional RetentionClaim to internal marker admission helper without changing existing callers.
- [x] Write actual original-job/real-native tests asserting expired operation erased and completed, all four kinds/cascades, independent later trash/foreign content/native bytes preserved, original deadlines unchanged on failure, exact deadline boundaries, malformed keys/metadata fail closed, missing already-erased operation no-op, typed UUID separation and restore row-wait winner. Ledger failure/cancellation/partial success/lease loss must prevent physical deletion; retry with new token succeeds.
- [x] Run `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --filter 'FullyQualifiedName~TrashExpiry' --no-restore`; Expected: behavioral missing-handler failure before implementation.
- [x] Implement owner/job/op/resource locks, strict current-operation selection, claim/deadline guarded terminal markers, complete-set ledger outside locks, relock/verify/physical operation-only purge. Use existing typed physical helpers; retain active executing job.
- [x] Run focused expiry plus existing trash/erasure tests; Expected: all PASS.
- [x] Commit `feat: expire retained trash operations`.

### Task 3: Original account-closure expiry
**Files:** Data/Erasure/AccountExpiryHandler.cs; AccountErasure.cs; AccountPurgeHandler.cs; AccountErasureExecution.cs; Data.Tests/AccountExpiryHandlerTests.cs and focused partial files.
**Interfaces:** Consumes Task 1 claim semantics; produces public `AccountExpiryHandler(factory,ledger):IRetentionHandler`, Kind `account-purge`, ExecuteAsync. Shared internal `AccountErasureExecution.ExecuteAsync(factory,ledger,claim,id,ct)` carries mature ledger/physical phases for both account handlers. AddIntentAsync accepts optional persisted claim.
- [x] Write real due closure/native-grant tests reproducing missing expiry handler and own executing-job deletion; assert full typed durable set before physical deletion, retained current job completed, foreign native payload unchanged, Heimdall identity untouched, future/invalid/reduced-state denial, early cancellation row-wait winner, slow clock/lease expiry and partial-ledger rollback/retry. Preserve existing account-erase behavior.
- [x] Run `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --filter 'FullyQualifiedName~AccountExpiry' --no-restore`; Expected: behavioral failures for absent handler/current-job deletion.
- [x] Implement graph-first current-state/deadline admission, guarded typed intent, shared execution and exclusion of exact executing job from account work cleanup.
- [x] Run account expiry/deletion/erasure/closure regressions; Expected: all PASS.
- [x] Commit `feat: expire uncancelled account closures`.

### Task 4: Restricted maintenance and hosting
**Files:** Data/Operations/RetentionMaintenance.cs; WebApi/Startup.cs; WebApi/Operations/RetentionWorker.cs; Data.Tests/RetentionMaintenanceTests.cs; WebApi.Tests/RetentionExpiryTests.cs; worker tests; operations docs; README.md.
**Interfaces:** Consumes Task 1 maintenance result and Task 2/3 handler kinds. Produces registered internal worker preparation and safe metric counts for completed/failed/scheduled/expired-stream totals.
- [x] Write tests for zero writes on no due targets, missing original-job repair after owner wait, denied real DB role before all writes/ledger calls, due/native current-state filtering, busy stream skip, cleanup cascades and preserved SyncOperation, actual hosted worker/HTTP hidden expired data under outage/retry, restore gate and isolated backup/ledger replay.
- [x] Run focused maintenance/HTTP tests; Expected: behavioral missing maintenance/registration/metric failures.
- [x] Implement bounded rights-checked candidate recheck/upsert and SKIP LOCKED abandoned-stream deletion, registrations and safe metrics. Use long test-only background interval where necessary to isolate explicit worker tests from unrelated fixture snapshots.
- [ ] Run focused integration and helper gates; Expected: all PASS, no externally visible new route/schema or secret logging. Mark UC53 README Done (54 Done,2 Todo; M06 7/9).
- [ ] Run locked restore, dependency/native audits, helper/spec/protocol/client/OpenAPI/Docker checks, zero-warning Release build, full unfiltered six-project Release tests with fresh coverage; Expected: 0 failed/skipped, >=90% six-assembly line coverage; report branch coverage.
- [ ] Commit `feat: schedule restricted retention maintenance` after focused checks to freeze a clean source head; complete the full local gates above before recording this implementation task complete.

## Delivery gates after implementation
- [ ] Prepare the whole-branch review package. Dispatch exactly one fresh read-only reviewer; fix Important/Critical findings RED→GREEN and rerun the full suite; ledger all rulings and defer Minor findings.
- [ ] Open PR closing #54, exact-head branch-policy/test/docker must all succeed. Verify unchanged develop base, normal squash merge, source/merged tree equality, Closed/project Done/README, owned branches removed and hash evidence archive. Then load UC54 automatically.

## Gate 1 self-check
FR-TR-06→Task 2, FR-TR-07→Task 3, FR-TR-08→Tasks 1–4. AF-01→Tasks 1/4; AF-02→Task 4; AF-03/04→Tasks 2–4; AF-05→Tasks 2/3. All references exist. Interfaces are produced before consumption. No implementation delegation; one fresh final reviewer only.
