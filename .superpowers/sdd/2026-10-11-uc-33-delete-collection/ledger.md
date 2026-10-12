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

- Status: implemented, verified and committed; separate task review pending.
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
- Commit subject: `feat: define collection trash contract`. The plan's final Task 1
  checkbox remains unchecked until the controller records the separate task review.

### Task 2

- Status: pending

### Task 3

- Status: pending

### Final review and delivery

- Status: pending
