# UC51 Restore Trash Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Restore one authorized deletion operation before its original deadline, preserving independent trash, terminal erasures, current permissions and revoked capabilities.

**Architecture:** Add a strict CQRS command behind `POST /api/trash/{id}/restore`; `id` is the public deletion-operation ID returned by UC50 and the only body field is required `expectedRevision` of its root. Under current owner/session locks, capture only that operation's typed members and retained associations, revalidate current links, then atomically restore surviving owned members and retire that operation/its queue item. Return typed restored metadata and omission counts, with no ciphertext, keys or hidden association identifiers.

**Tech Stack:** Existing .NET10, PostgreSQL, native envelope/signature contracts, ArturRios mediator/output, FluentValidation, xUnit/Moq/Testcontainers; no new dependency or migration.

**Spec:** Use Case Specification UC51 main and AF01–07; System Requirements FR-TR-02/03/04, common statuses, authorization matrix and deletion strategy; BR10/19/20/22/23; Testing Specification; current trash writers and UC50 retained scope.

## Global Constraints

- One issue52/branch/PR from synchronized `develop` aa54d3ea2e9e027b5d9dac334071f6ba10acb662. Existing unattended authorization covers design, transitions, normal verified develop merge, closure and owned cleanup; release qualification remains separate.
- Required full unfiltered Release suite, fresh >=90% production line coverage with reported branch coverage, locked restore/build/audits/native/actual HTTP/OpenAPI/Docker, independent whole-change review and all exact-head CI checks before merge.
- Current active owner and vault selection are mandatory; human identity is checked before database work. Hidden operations404 precede revision/deadline/native disclosure. Visible expired own operations410 even when retained rows remain; typed terminal root404.
- A folder root admits only its operation's retained cascade. Missing/terminal members are never revived; independently prior trash and its original deadline/job remain exact. Wrong active member state409; required retained/native corruption503; every unsuccessful transaction leaves state exact apart from permitted global sequence gaps.
- Preserve native ciphertext, encrypted wrappers, client edited timestamps and ownership. Advance each restored row's revision/sequence once, and distinct affected active structural parents once where the corresponding trash writer did. Never reactivate sessions or collection/software grants. Profile restoration is account-wide only.
- Selected root authority matches UC50: retained original selected-profile/active owned selected-collection links, or current complete owned former-parent scope. Restored direct profile/collection links are limited to current selected scope; outside-operation parents must be currently visible. Omit unavailable/out-of-scope links without exposing their IDs. Authorized folder/collection roots admit their exact original owned unit, not caller-chosen new membership.
- One guarded root mutation rechecks database time/session/ownership/operation dates/typed terminal state after all waits; admission is exclusive before the original deadline. Natural expiry after that guard may finish under held locks. Expiry/purge workers must use the same owner→job order and row/state guards.
- Lock own account UPDATE, own session UPDATE, selected operation's job UPDATE, operation/entries UPDATE, ordered owned profiles, then owned collections/resources. For R/F/C operations, owned collections NO KEY UPDATE serialize shared writers before folder/record locks. Profile restoration uses collection SHARE (including current foreign collection links), never strong own-collection locks followed by foreign SHARE, avoiding reciprocal profile-restore deadlocks. Never acquire foreign account/profile roots late.
- Strict shared retained-snapshot decoding rejects duplicate/unknown fields, duplicate IDs and noncanonical GUIDs. Extend UC50's parser through this shared behavior; prove actually foreign selected collections cannot authorize owned trash.
- Only the chosen operation/entries/job are consumed; selected sessions keep their ProfileId and revocation state. Lost-success retry404; stale root revision409. No ledger record is undone or removed.

## Review Focus

1. Restore versus original deadline, session expiry and permanent purge while blocked on every lock phase; one valid winner and rollback of all actual mutations.
2. Folder cascade versus prior independent trash, missing/terminal members, missing parents and restored topology cycles.
3. Selected-profile escape through retained foreign collections or reattaching currently hidden parents/profiles/collections; current native grants on profile restore.
4. Reciprocal profile restores with foreign collection links and alternate-route recipient edits; no foreign root lock inversion.
5. Re-trash after restore, queue-claim invalidation, native bytes/keys/client timestamps preserved and revoked grants/sessions left exact.

## Flow mapping

| Flow | Implementation/evidence |
| --- | --- |
| Main | Four root kinds; root expected revision; exact retained cascade/current associations; atomic metadata output; domain/command/data/HTTP tests. |
| AF01 | Strict canonical operation ID, required safe revision, unknown/duplicate/numeric-string/body/header/query/media rejection400. |
| AF02 | Current owner/retained selected scope; foreign/hidden/terminal root404 before other state; missing/terminal members omitted without resurrection. |
| AF03 | Current human bearer + vault handle; invalid identity401, invalid scope403; no mutation or secret output. |
| AF04 | Root CAS, member-state and sequence guards409; concurrent restore/re-trash/edit wins preserved. |
| AF05 | Required persistence/identity/native corruption503; injected post-mutation faults and cancellation roll back all writes. |
| AF06 | Revalidate former parent/P/C/R/F links, current native foreign collection grants and complete ancestry; report only authorized resource ID + association-kind counts for omissions. |
| AF07 | Original database-time exclusive deadline410, including lock waits; no dependence on physical purge timing. |

### Task 1: Strict restore command and retained snapshot contract

**Files:** Domain/Trash/TrashRestoreContracts.cs and TrashAssociations.cs; Command/Trash/{RestoreTrashCommand,RestoreTrashValidator,RestoreTrashHandler,RestoreTrashOutput,RestoreTrashMessages}.cs; mirrored Domain.Tests/TrashAssociationsTests.cs and Command.Tests/RestoreTrashHandlerTests.cs.

**Interfaces:** `ITrashRestoreStore.RestoreAsync(TrashRestoreRequest(Actor, AccessVerifier, OperationId, ExpectedRevision), CancellationToken)` returns `VaultResult<TrashRestoreDetails>`. Details contain OperationId, RootKind, RootResourceId, RestoredAt, Items and Omissions. Each item is `(Kind, ResourceId, Revision, ServerSequence, EditedAt)`; each omission `(ResourceKind, ResourceId, AssociationKind, Count)` names only an authorized restored/root resource and a positive count. `TrashAssociations.TryRead(kind, bytes, out data)` provides typed Parent/Profile/Collection/Record/Folder ID arrays for all four persisted snapshot shapes.

- [ ] Write strict decoder and command/validator/result integrity/cancellation tests first; observe missing-feature/behavior RED.
- [ ] Implement safe command, native-independent metadata contract, allowlisted errors and decoder; validate all identities, root expected+1, typed uniqueness/order, safe counters/times and omission integrity; verify GREEN.
- [ ] Commit the verified command and snapshot behavior.

### Task 2: Atomic current-scope restoration

**Files:** Data/Trash/{TrashRestoreStore,TrashRestoreInventory,TrashRestoreAssociations}.cs; adapt TrashListStore.cs to shared decoder; mirrored Data.Tests/TrashRestoreStoreTests.cs and concurrency/failure test files when needed.

**Interfaces:** Implements Task1 store. Reuse current native folder reads through TransactionContextFactory for complete owned former-parent scope and current active visibility; use current native profile-collection resolver/binding rules for foreign links. Root guard and per-row CAS use current owner/session/time/typed terminal evidence; postconditions compare native bytes/client times/IDs and metadata. Retire only the chosen operation/job after all writes.

- [ ] Write real PostgreSQL tests for actual R/F/C/Profile deletes/restores; cascade/prior trash, missing links, terminal resources/typed UUID collision, current selected scope and actually foreign selected collection evidence, native profile/foreign-grant corruption, current auth/state, re-trash/retry and revoked capabilities.
- [ ] Observe missing-store RED; implement typed inventory, authority and link planning with omission counts, canonical locking, guarded root admission, member/parent mutations and operation/job retirement; verify GREEN.
- [ ] Add actual lock-wait deadline/session tests, concurrent winners/reciprocal profile restores/shared edits, post-mutation fault and cancellation rollback tests. Record every code correction with RED→GREEN evidence.
- [ ] Commit verified persistence and shared listing regression coverage.

### Task 3: Actual HTTP contract and verified delivery

**Files:** TrashController.cs; Startup.cs; contract schema metadata; WebApi.Tests/TrashRestoreHttpTests.cs and TrashRestoreContractTests.cs; trash API guide, foundation status, changelog, README and generated OpenAPI.

**Interfaces:** Canonical operation route; strict JSON `{ "expectedRevision": n }`; current bearer/single vault handle; no query override or authority headers. Success200 `trash_restored` metadata output;400/401/403/404/409/410/503 stable failures and empty preauthentication413, all no-store.

- [ ] Add real host tests before route registration, observe route/schema RED; implement all transport/authorization flows and exact output/OpenAPI requiredness; verify actual JSON against committed schema.
- [ ] Document operation ID/root-unit semantics, original deadline, current-scope omissions, lock/admission boundary, preserved bytes/revocations, re-trash and deferred purge worker responsibility.
- [ ] Complete locked restore, zero-warning build, current dependency/native audits, helpers/spec, native corpus + actual lease clients, OpenAPI, Docker, full unfiltered Release suite/fresh coverage and frozen source hashes. Obtain independent whole-change review.
- [ ] Update README52Done/4Todo/M065of9; create one PR closing52, set Testing and wait for exact-head required checks. Normal squash merge, prove identical tree, Closed/Done, synchronized clean develop/owned cleanup and hash archive; continue UC52.
