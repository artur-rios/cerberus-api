# UC52 Empty Trash Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Permanently empty admitted owned trash with durable typed erasure, safe retries and real backup-restore privacy, preserving active unrelated and foreign content.

**Architecture:** Strict CQRS DELETE command admits a current wide owner inventory and irreversible terminal intent; one coalesced account `trash-erase` job durably records all entries before a fenced atomic physical pass. Typed physical helpers extend existing replay to F/C/P without recursive deletion of unmarked content.

**Tech Stack:** existing .NET10/EF Core/PostgreSQL, native encrypted protocol, file erasure ledger, current retention runner, xUnit/Testcontainers and HTTP host; no dependency or schema migration.

**Spec:** docs/superpowers/specs/2026-10-15-uc-52-empty-trash-design.md and normative requirements UC52/FR-TR-05/BR21/26.

## Global Constraints

- User's authorized unattended batch replaces repeated human gates; independent whole-branch read-only review and all verification gates remain.
- One issue53/feature/uc-52-empty-trash/PR into develop; no next UC source/branch/status before verified merge/bookkeeping.
- Preserve encrypted bytes/keys in surviving content; API never decrypts/logs secrets. Typed resource identity at every terminal lookup.
- Owner→session→job→ordered own profiles→collections→folders/records; no late foreign owner/profile lock; current DB-time authority/claim fences after waits and on mutation.
- Intent before durable ledger before physical removal; no physical changes on unavailable ledger or stale claim. Post-intent failures stay irreversible and retryable.
- Full unfiltered Release suite, fresh production line coverage>=90 with branch coverage reported, exact-head branch-policy/test/dockerSUCCESS and normal merge; development-only review deferral does not authorize release.

## Review Focus

1. Coalesced job target changes during ledger I/O or completion, stale/expired/stolen claims, and concurrent retries must not erase active data or acknowledge undurable targets.
2. Folder replay before child ledger entries, independent trash, malformed historical associations and cross-kind UUID collisions must not erase unmarked/foreign content.
3. Emptying versus restore/re-trash/shared edits/account purge must obey canonical locks and preserve the valid winner without deadlock.
4. Native profile/session references and foreign shared collections must never broaden selected access or revive grants after physical erasure.
5. Actual old-backup replay, failures after physical mutations, cancellation and original trash-job retirement must preserve irreversible intent and release no premature success.

## Flow mapping

| Flow | Implementation and evidence |
| --- | --- |
| Main | All four current owned trash kinds; typed intent+coalesced outbox; complete durable ledger; fenced physical pass; counts-only200 and actual replay. |
| AF01 | Strict required account expectedRevision, unknown/duplicate/numeric-string/body/query/header/media400; empty preauth413. |
| AF02 | Absent/inactive/terminal owner404; foreign trash never in inventory or response. |
| AF03 | Current human identity401; current vault, wide-only403; no native grants broaden deletion authority. |
| AF04 | Stale account revision or changed admitted row state409 before intent; real restore winner remains active; stale worker claims never mutate. |
| AF05 | DB/native authority/ledger/fault errors503; pre-intent rollback, post-intent durable retry; cancellation propagates and physical pass rolls back. |

### Task 1: Typed physical erasure and restore replay

**Files:** Data/Erasure/ResourceErasure.cs (and focused SQL/metadata helper if needed), TerminalErasureStore.cs; new Data.Tests/ResourceErasureTests.cs and backup/fencing tests. Preserve RecordErasure existing behavior; scope operation cleanup and current/active claim preservation explicitly. AccountErasure.cs must recognize its own coalesced trash-erase job.

**Interface:** `ResourceErasure.RemoveAsync(AppDbContext db, string kind, Guid id, RetentionClaim? claim, CancellationToken ct)` for record/folder/collection/profile with current typed terminal guard. Caller owns transaction; workers require durable entries before call; replay runs before traffic. Typed helper never deletes unmarked descendants or related active members. Extend ReapplyAsync to physically remove F/C/P.

- [ ] Write real four-kind replay, cross-kind isolation, related-active/foreign preservation, selected session IDs, parent-FK ordering and claim/fault/cancellation tests first; observe missing F/C/P physical behavior RED.
- [ ] Implement guarded physical erasure, scoped metadata retirement, safe parent detachment and typed replay; verify GREEN including existing record/account erasure regressions.
- [ ] Commit verified helper/replay changes.

### Task 2: Strict empty-trash command/output

**Files:** Domain/Trash/TrashEmptyContracts.cs; Command/Trash/{EmptyTrashCommand,EmptyTrashValidator,EmptyTrashHandler,EmptyTrashOutput,EmptyTrashMessages}.cs; command tests.

**Interface:** `TrashEmptyRequest(Actor,AccessVerifier,ExpectedRevision)`; `ITrashEmptyStore.EmptyAsync` returns VaultResult<TrashEmptyDetails>. Details accountId/purgedAt/counts/totalPurged; count entries exactly four known kinds, safe nonnegative integers, unique, total=sum, UTC microsecond time. Internal actor/access context; required strict expectedRevision safe positive account revision.200trash_emptied,400/401/403/404/409/503 allowlisted failures.

- [ ] RED command/validator/defensive metadata/error/cancellation tests.
- [ ] Implement strict metadata-only contract and handler; GREEN and commit.

### Task 3: Irreversible owner-wide admission and resumable purge

**Files:** Data/Trash/{TrashEmptyStore,TrashErasureInventory}.cs; Data/Erasure/TrashPurgeHandler.cs; Operations/RetentionWorkStore.cs; Data.Tests/TrashEmptyStoreTests.cs and concurrency/fencing tests.

**Interfaces:** Implements Task2. Handler exactKind `trash-erase`, operation key two canonical segments `trash-erase/{accountPublicId}`. No RetentionWorkItem.Payload. Inventory union is bounded to actual owned deleted rows, contains typed IDs/state only and excludes active/foreign content. New typed intent and create/reopen/coalesce job commit atomically under current DB-time wide-session/account revision guard. New targets invalidate stale claims; same-intent retries preserve live claims. Worker captures typed terminal trash, records all ledger entries before physical phase, relocks/fences, requires complete durable set and purges under one transaction. Extend DB-time completion fence for trash-erase; register exact handler at final HTTP task. Empty0 performs no queue/ledger writes. Scope account deletion queue cleanup.

- [ ] RED actual four-kind owner inventory, expired/damaged trash, native foreign sharing, empty/retry/restore-winner/current auth/revision, new intent during ledger, persisted queue, ledger outage, stale/expired claim/statement fencing, after-mutation failure/cancellation and account-purge integration tests.
- [ ] Implement intent/coalesced job/worker without plaintext/native decryption or active unrelated deletion. Reuse existing safe SQL parameters and terminal fences; no warning suppressions. GREEN all flow/concurrency/native/rollback cases and commit.

### Task 4: HTTP contract, docs and verified delivery

**Files:** TrashController.cs, Startup.cs, schema/operation contract metadata; WebApi.Tests/TrashEmptyHttpTests.cs and TrashEmptyContractTests.cs; trash guide/foundation/changelog/README/OpenAPI.

- [ ] Actual route/schema/native four-kind HTTP tests RED before registration; implement strict request/current auth/required metadata/empty413/no-store; GREEN with actual JSON/schema validation.
- [ ] Document wide-only/account expected revision/admission boundary, irreversible503 retries/coalesced claims/counts, backup replay and unchanged active/foreign resources. README53Done3Todo/M066of9.
- [ ] Locked restore,0warning build,direct/transitive/native audits,helpers/spec,native322×4/actualleaseclients,Docker/OpenAPI, frozen source full unfiltered Release suite/fresh coverage and fresh independent whole-branch read-only review. Record rulings/minors.
- [ ] One PR closing53,Testing,exacthead3requiredSUCCESS,normal squashmerge,tree equality/ClosedDone/clean develop/ownedcleanup/hasharchive; automatically continueUC53issue54.
