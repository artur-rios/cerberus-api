# SDD ledger — plan: docs/superpowers/plans/2026-10-11-uc-33-delete-collection.md

## Preflight

| Task | Implementer model | Review model | Shared interfaces/files | Baseline dependency |
| --- | --- | --- | --- | --- |
| 1 — contracts/command | gpt-6.1-sol high | gpt-6.1-sol high | Defines `CollectionTrash*` and `CollectionAssociationSnapshot` consumed by Tasks 2/3 | UC32 merged tree `36d60041`; Command baseline green |
| 2 — transactional store | gpt-6-astra xhigh | gpt-6.1-sol high | Consumes Task 1 interfaces; owns `CollectionTrashStore` used by Task 3 DI | Task 1 committed and reviewed; Data baseline green |
| 3 — HTTP/docs/full verification | gpt-6-astra xhigh | gpt-6.1-sol high | Consumes Tasks 1/2; modifies shared controller, Startup, README, changelog, OpenAPI | Tasks 1/2 committed and reviewed; Web/native baseline green |
| Final whole-branch review | gpt-6-astra ultra | n/a | Reviews original base through tested Task 3 head | All tasks committed, reviewed and full verification recorded |

Tasks are sequential. No two implementers run concurrently. Task 1 owns the public
interfaces; Task 2 starts only after its review. Task 3 starts only after Task 2
review. The final reviewer is fresh and receives the full base-to-head package.

## Baseline

- Original base: `36d60041beeb3e53c9fa6ba2440d62fdde7046da`
- Original tree: `d29aba52e1337933d01efb4affbc91e3dd573005`
- Fresh inherited verification from UC32 delivery: 7,875 tests, zero failures/skips;
  98.4% line, 92.1% branch, every production assembly above 90% line; Maven 173,
  OSV 153 packages/0 findings, corpus 322×4, native helpers 36, Python helpers 23;
  exact-head required CI all successful.
- Design/spec verification: `python scripts/verify_specs.py` PASS (13 Markdown files,
  107 FRs, 55 UCs, 29 BRs, 56 backlog entries).

## Rulings

1. Revoke active grants in place, increment their revisions once and preserve wrapper
   bytes; restoration never reactivates them. Reason: system requirements explicitly
   say collection deletion revokes grants and restore does not silently revive them.
   Cost if wrong: migrate grant audit/restore representation in UC37/51.
2. Snapshot all linked profile public IDs, including recipient profiles, but require
   restore-time ownership/grant revalidation. Reason: deletion must remove every
   association while preserving enough restricted evidence to reconcile survivors.
   Cost if wrong: UC51 may omit foreign links without changing this delete flow.
3. Bump active directly linked owned profiles/folders/records once; do not mutate
   foreign profile metadata. Reason: same-owner structural changes use existing sync
   revisions, while cross-owner visibility removal belongs to future sync events.
   Cost if wrong: UC48 adds a recipient visibility-removal event stream.
4. Allow trashing damaged owned collection ciphertext unchanged. Reason: deletion
   neither returns nor replaces opaque content and must not strand owner cleanup.
   Cost if wrong: add a validation/migration rule before purge release.

## Task evidence

### Task 1

- Status: implemented, verified, committed and independently approved.
- Task base: `f4b79b3` (`docs: design collection trash flow`).
- Scope: seven Domain/Command contract files and `DeleteCollectionHandlerTests.cs`;
  no persistence store, controller, registration or Task 2 implementation.
- Discovery prerequisite: `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --no-restore --filter FullyQualifiedName~DeleteFolderHandlerTests --list-tests --verbosity minimal`
  exited 0 and discovered 75 established folder-handler cases.
- RED: after writing the collection tests, before production code,
  `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --no-restore --filter FullyQualifiedName~DeleteCollectionHandlerTests --verbosity minimal`
  exited 1 with only four CS0246 errors for absent `DeleteCollectionCommand`,
  `CollectionTrashDetails`, `ICollectionTrashStore` and `DeleteCollectionHandler`.
  Existing referenced production projects built; no unrelated fixture/compiler errors.
- Focused GREEN: the same focused command exited 0, 75 passed, 0 failed, 0 skipped.
  Cases cover strict body requiredness/forgeries/duplicate and alternate numbers,
  invalid authority/revision with no persistence, exact trusted forwarding and literal
  access-handle hash, caller cancellation, safe error/status mapping, null/error-plus-data,
  mismatched IDs/counters/timestamps, safe-integer exhaustion and exact six-field output.
- Whole-project GREEN: `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --no-restore --verbosity minimal`
  exited 0, 1,327 passed, 0 failed, 0 skipped; no compiler warnings.
- Whitespace: `git diff --check` exited 0.
- Environment: sandboxed .NET test discovery exited 1 without test output;
  `dotnet --info` identified denied Windows Service Control Manager access. Approved
  `require_escalated` test executions ran normally, without changing project configuration.
- Task 1 rulings: none. Deferred minors: none. Blockers: none.
- Commit: `543b2eda909ee695c28ae3c728c4d9535a0de279`
  (`feat: define collection trash contract`).
- Independent review: approved `f4b79b3..543b2ed`; Critical 0, Important 0,
  Minor 0. Reviewer confirmed strict JSON/trusted context, validation/forwarding,
  safe result/error mapping, exact contract/snapshot shapes and no Task 2 leakage.
- Review rulings: sorted/distinct snapshot capture, greater-than-prior sequence
  enforcement and internal cancellation mapping remain Task 2 responsibilities.

### Task 2

- Status: implemented and verified; ready for the separate Task 2 review.
- Task base: `5db2213e64661e7afaf59535cdd44ba9ff3639fa`
  (`docs: record collection contract review`).
- Scope: `CollectionTrashStore.cs`, `CollectionTrashStoreTests.cs`, this ledger and
  the Task 2 plan checkboxes. No controller, Startup, schema or Task 3 changes.
- Discovery prerequisite: `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --no-build --no-restore --filter FullyQualifiedName~CollectionUpdateStoreTests --list-tests --verbosity minimal`
  exited 0 and discovered 131 established collection-update cases from the baseline
  assembly. A source-building discovery attempt encountered the new missing store.
- RED before creating the store: `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --no-restore --filter FullyQualifiedName~CollectionTrashStoreTests --verbosity minimal`
  exited 1 with CS0246 for absent `CollectionTrashStore`; production dependencies
  built. Once the store existed, the compiler exposed a mechanical fixture rename
  (`ExecuteUpdateAsync` had also been renamed) and test-only dynamic identifier
  analyzer errors; those fixture errors were corrected before runtime verification.
- Genuine runtime RED: exactly-one-winner failed with both results returning 503.
  Temporary diagnostics traced a `FormatException` to the inventory SQL's literal
  empty JSON object being parsed as an EF format placeholder. Replacing that literal
  with `jsonb_build_object()` produced GREEN: owner wide/selected complete-graph and
  exactly-one-winner smoke cases 3/3. All temporary diagnostic code was removed.
- First complete runtime matrix: 164/167 passed. Three race fixture assertions were
  corrected after source inspection: complete native rotation requires the profile
  replacement/wrapper; a completed profile-association removal does not change the
  collection revision and wide owner trash remains allowed. Both orders of actual
  owner update/create/association/rotation subsequently passed 8/8.
- Final focused GREEN: the focused command above exited 0, **199 passed, 0 failed,
  0 skipped**, no warnings. Native Master and PerProfile owner success, all PC/CF/CR
  snapshots/removals, preserved direct content/access, sorted complete public IDs,
  distinct owned active bumps, inactive/terminal preservation, all active grant
  revocations and retained revoked wrappers, foreign profile/session preservation,
  recipient GET/list disappearance and complete retained protection rotation passed.
- Concurrency evidence: all ten real lock-wait phases expire to 403; exactly one
  same-revision delete wins and lost retry returns 404; graph growth conflicts and
  harmless disappearance captures fresh inventory; current final-guard authority,
  native recipient folder/record editors and all four actual owner writers pass in
  both orders. EXPLAIN ANALYZE reports one materialized target among 100 unrelated
  collections; captured lock parameters contain only the actor and held graph,
  excluding foreign profiles/accounts/protection and unrelated resources.
- Failure evidence: injected failures after collection, every association family,
  every resource bump, grant, operation, entry and queue all roll back full snapshots
  and admit clean retry. Guard misses, deletion-count failures, tampered persisted
  retained fields, sequence exhaustion/regression, actual provider outage, internal
  cancellation and caller cancellation during a real lock wait all passed.
- Platform verification: the unfiltered Windows Data attempt was stopped after
  1,268 passes and 206 failures (0 skips, 1,474 executed). Existing Linux-only
  `FileErasureLedger` failures affected ErasureLedger, RestoreReconciler,
  RecordPermanentDelete, RecordPurge and TypedTerminalErasure cases, followed by
  membership-table DDL timeouts and dependent read/create/association failures.
  Exact failing test names and traces remain in `task-2-data-all.log`. No unrelated
  production/test code was changed. The approved WSL/Docker Linux harness is the
  authoritative whole-project verification; its first `--no-restore` attempt hit
  NETSDK1064 because copied Windows asset paths are not Linux paths, so the retry
  restores in the disposable container with read-only source/package-cache mounts.
- Whole-project GREEN: `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --verbosity minimal`
  in the established `wsl.exe -d Ubuntu` Docker harness (`--network host`,
  `TESTCONTAINERS_RYUK_DISABLED=true`, SDK `mcr.microsoft.com/dotnet/sdk:10.0`,
  read-only source and NuGet cache, working copy `/work`) exited 0: **3,219 passed,
  0 failed, 0 skipped**, duration 7m29s. Restore/build emitted no warnings or errors.
  Full output: `task-2-data-linux.log`.
- Whitespace: `git diff --cached --check` exited 0 after removing two extra EOF
  blank lines. Only the four authorized Task 2 files are staged.
- Commit message: `feat: trash owned collections`; the task reviewer records the
  resulting SHA and review outcome after this commit.
- Task 2 design rulings: none; the implementation follows the four approved design
  rulings, including fresh harmless-disappearance classification. Deferred minors:
  none. Blockers: none. Independent task review remains pending.

### Task 3

- Status: pending

### Final review and delivery

- Status: pending
