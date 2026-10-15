# UC08 Permanent account deletion implementation plan

> **For agentic workers:** Use superpowers:executing-plans task by task, with a fresh read-only whole-change reviewer. Existing unattended batch authorization replaces routine human gates; no implementation subagents.

**Goal:** implement issue9/UC08 main and AF01–07, FR-AC-15–18 and BR25/26/29, preserving Heimdall identity and foreign owned data.

**Architecture:** Extend existing two-phase typed erasure infrastructure. Atomically authorize an Active owner's fresh identity and account-wide session, reduce access using savepoint-aware existing closure behavior, and commit inaccessible typed erasure intent plus immediate account-erase work. Flush durable external ledger entries before fenced physical account purge; replay account ledger entries physically before the restore traffic gate opens.

**Tech Stack:** existing .NET10/C#/EF Core/Npgsql/PostgreSQL, custom Heimdall identity middleware, ArturRios mediator/output/test helpers and Linux fsync ledger. No new service or package.

## Constraints and decisions

- Branch from clean synchronized develop49f75050b79f961990c7196709e626079f187326; source tree6705830d5a8b7bb578b49af3ee231c7f26c1a0af, UC07 full11340 passed/98.8%line. One UC/issue/branch/PR; exact branch-policy/test/docker SUCCESS and normal merge.
- DELETE `/api/accounts/me/permanent` accepts exactly positive safe integer `expectedRevision`. Fresh current human actor and single signed iat less than60 seconds old, no future value. No caller owner ID. Match Active account precondition; account-wide current vault session is required, consistent with own-account management. Selected-profile-only authority cannot delete the entire account.
- Canonical connected account roots, affected collections and current session precede child/job locks. Guard session admission with current database time and policy/generation/expiry, then hold authority locks throughout the transaction. The nested closure CAS separately rechecks fresh identity before reduction. Session expiry after its successful guard and identity expiry after its successful closure CAS may complete; identity expiry between those guards rejects and rolls back.
- Use NativeMutationTransaction savepoints for AccountClosureStore when called through TransactionContextFactory; standalone closure behavior remains unchanged. Outer failure rolls back closure, grants, counters, session reduction, terminal intent and jobs.
- Commit typed account/resource/grant terminal IDs and `account-erase/<UUID>` immediate work before external ledger IO. The account marker immediately prevents closure cancellation, normal login, unlock/read/write/reuse. Complete the recoverable closure's due work inside the same transaction. No physical removal before all relevant external ledger entries are durable.
- AccountPurgeHandler fences its persisted claim with database time after waits and at every mutation, supports retry after ledger or storage failure, and never calls Heimdall deletion. Return minimal accountId/deletedAt/state (completed200 or queued202); safe503 on unavailable dependencies, propagating caller cancellation. A retry after completed erasure404; a committed pending intent is retried by the worker.
- Purge owned active/trashed profiles, records, folders, collections, protection/recovery, sessions/challenges, software provisioning, synchronization metadata, registration metadata and owned trash/resource work. Cascading FK deletion removes incoming/outgoing grants and profile links to erased collections. Preserve all other owners' content/protection/sibling grants. Keep minimal typed terminal IDs and current completed erasure work.
- Account ledger replay runs only with traffic closed, marks and removes restored owned trees, revokes affected recipients' current generation when needed, removes incoming/outgoing grants and own registration metadata, and reserves terminal IDs. Validate safe metadata and exact scoped collateral before commit; bad replay leaves the gate closed.
- Future UC53 supplies expiration scheduling/expired-closure admission and uses common account erasure; this unit implements explicit immediate erasure only. Existing SyncOperation IDs are removed only with account erasure, never by arbitrary sync retention.

## Review focus

- Old backup contains shared owned content and incoming foreign grants: replay erases only the deleted account and keeps foreign content.
- Ledger outage/cancellation after durable database intent: account remains inaccessible, retries are possible, no incomplete physical purge.
- Claim theft/expiry after lock waits or directly before a DELETE: no mutation or acknowledgement by stale workers.
- Closure savepoint factoring: outer rollback cannot leak a committed close or grant reduction.
- Same UUID in other typed kinds, new account with retained Heimdall identity, corrupt counters and terminal metadata: no resurrection or cross-kind deletion.

## Task 1: strict contracts and command

Files: new Domain/Accounts/AccountDeletionContracts.cs; Command/Accounts/DeleteAccountCommand.cs, AccountDeletionOutput.cs, DeleteAccountHandler.cs; mirrored Domain/Command tests.

- [x] Write missing-contract RED tests for safe revision, strict JSON, fresh identity, account-wide opaque access binding, output integrity and safe error allowlist; observe expected failure.
- [x] Implement AccountDeletionRequest(Actor,AuthenticationIssuedAt,AccessVerifier,ExpectedRevision), AccountDeletionDetails(AccountId,DeletedAt,State), result/store interface and mediator handler. Test no data on failure, exact result state/status, dependency/caller cancellation.
- [x] Run focused Domain/Command checks GREEN and record evidence.

## Task 2: atomic irreversible intent and fenced physical/restore erasure

Files: new Data/Accounts/AccountDeletionStore.cs, Data/Erasure/AccountErasure.cs, AccountPurgeHandler.cs; modify AccountClosureStore.cs savepoint boundary, TerminalErasureStore.cs account dispatch and RetentionWorkStore.cs account erasure claim completion; mirrored PostgreSQL tests.

- [x] Write PostgreSQL RED tests for actual complete account-owned data removal, retained foreign sharing/Heimdall identity, inaccessible intent on ledger failure, atomic outer rollback and UC06 regressions.
- [x] Implement guarded admission and typed intent using existing closure reductions/savepoints; common fenced account cleanup and ledger-first handler; register worker kind. Preserve unrelated snapshots and safe exact authority.
- [x] Add actual races/locks/expiration/theft, rollback fault injection and idempotent retry; test no collateral mutation and typed ID reservations.
- [x] Restore an actual pre-erasure pg_dump and replay preserved ledger before opening traffic; assert ciphertext/resources/grants/own metadata absent, foreign data retained, replay idempotent and terminal identifiers unavailable.
- [x] Run all focused PostgreSQL tests and affected existing closure/record-erasure regressions GREEN.

## Task 3: actual HTTP contract and documentation

Files: AccountController.cs strict DELETE, Startup.cs registrations, ContractRegistration.cs strict body/output schema; new AccountDeletionHttpTests/ContractTests; docs/security/account-deletion-api.md, restore runbook, foundation-status, changelog and generated OpenAPI.

- [x] Missing endpoint HTTP RED then real hosted GREEN with actual valid owner session and current Heimdall fixture; no identity-delete calls.
- [x] Cover AF01 strict body/media/query/header/UTF-8/limit400/413; AF02 hidden/absent404; AF03 invalid/current/fresh auth401, selected/missing/stale scope403; AF04 revision/state409; AF05 ledger/identity/DB503+retained safe intent; AF06 incoming sharing preserved; AF07 owned sharing purged and recipients denied.
- [x] Validate actual successful/queued response JSON against committed OpenAPI and required fields; run docs/spec/helper checks and fresh independent whole-change development review. This is not independent security/client release approval.

## Task 4: full verification and delivery

- [x] Locked restore, zero-warning Release build, audits, helper/native corpus/leases/OpenAPI/Docker; clear old coverage then full unfiltered Release, read all output/exit/TRX, require fresh >=90%production line coverage and report branch coverage. No test/source changes during full run.
- [ ] Update README tracking after local proof, commit/review exact head, own PR closes9 and projectTesting; exact-head branch-policy/test/docker SUCCESS.
- [ ] Normal merge, issueClosed/projectDone, source/merged-tree equality, delivered branch cleanup, hash-verified evidence archive and clean synchronized develop. Continue newly unblocked UC43 if its Heimdall contract is verified, then UC50–55.

Local proof: full unfiltered Release 11,463 passed / 0 failed / 0 skipped; fresh production line coverage 98.8%, branch coverage 89.6%. Full attempt 1 exposed shared corrupt-fixture backup contamination; a dedicated PostgreSQL restore scenario resolved it without changing product behavior or assertions. The second full run passed.
