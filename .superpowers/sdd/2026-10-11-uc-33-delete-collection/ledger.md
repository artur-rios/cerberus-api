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

- Status: implemented, verified, committed and independently approved.
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
- Commit: `765d476c568f85fab641c997d31069349048cda0`
  (`feat: trash owned collections`).
- Independent review: approved `5db2213..765d476`; Critical 0, Important 0,
  Minor 0. Reviewer confirmed all five review-focus statements, current authority
  precedence, graph/lock safety, grant revocation, retained content and staged
  rollback evidence.
- Task 2 design rulings: none; the implementation follows the four approved design
  rulings, including fresh harmless-disappearance classification. Deferred minors:
  none. Blockers: none.

### Task 3

- Status: implemented, verified and committed; independent task review pending.
- Task base: `3e85238` (`docs: record collection trash review`).
- Scope: collection DELETE action, explicit store/validator/handler registration,
  `CollectionTrashHttpTests.cs`, collection API/security and foundation operations
  status, changelog, README UC33/M03 status, generated OpenAPI and task bookkeeping.
  No Domain, Command, persistence, schema or dependency changes.
- TDD prerequisite: all native collection creation, profile proof/open and recipient
  GET fixtures ran through the real HTTP host before the absent DELETE operation.
  Two initial xUnit2031 test-only analyzer errors were corrected before counting RED.
- Genuine RED before route/registration:
  `dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ArturRios.Cerberus.WebApi.Tests.csproj --filter FullyQualifiedName~CollectionTrashHttpTests --verbosity minimal`
  in the established disposable Linux Docker harness exited 1: **92 discovered,
  87 failed, 5 passed, 0 skipped**. Native owner successes expected 200 and received
  405 MethodNotAllowed; validation/foreign/hidden cases likewise observed the absent
  route. Six current-authority wait cases could not observe a lock because no
  endpoint executed. Shared middleware admitted its existing passing cases.
  Full output: `task-3-red.log`.
- Focused GREEN after only controller/DI wiring: the same command exited 0:
  **92 passed, 0 failed, 0 skipped**, 12 seconds, no warnings.
  Full output: `task-3-green.log`.
- HTTP evidence: native wide/Master/PerProfile owner success; exact six response
  fields, safe counters, UTC microsecond 720-hour deadline and no-store; retained
  collection bytes/time/epoch/owner, active record/folder content and direct access;
  complete typed membership snapshots/removal, owned structural bumps, active grant
  revocation and unchanged wrappers/recipient profiles/sessions; recipient GET/list
  disappearance. Native RO/RW recipients in all access modes return 403 before stale
  revisions, hidden targets return 404 before corrupt evidence, and relevant
  malformed native binding/pins return 503.
- HTTP failures: strict forged/missing/duplicate/case/numeric bodies, canonical
  paths, headers/query/UTF-8/BOM/compression/charset/oversize and current identity/
  account/session state preserve complete snapshots. Real account lock waits
  reclassify policy/revocation/selection/link/lifecycle/terminal changes. Concurrent
  same-revision delete has one winner and a lost-success retry returns 404 without
  extending retention. Required metadata corruption, actual missing-table outage
  and queue-insert failure return 503; the queue fault rolls back the entire graph,
  and outage/queue clean retries succeed.
- Supporting gates: `dotnet restore src/ArturRios.Cerberus.sln --locked-mode`,
  `python3 scripts/vulnerabilities.py`, `python3 scripts/openapi.py` all exited 0.
  The dependency scan reports no known direct/transitive vulnerabilities and
  OpenAPI reports current. The generated route/models retain documented shared
  requiredness, wrapper and empty-413 schema limitations.
- Native Linux runtime: CPython **3.14.8**, Temurin **25.0.4.1+1**, .NET SDK
  **10.0.401**. Fresh hash-locked wheels/Maven distribution and cache; Maven
  **173 tests, 0 failures/errors/skips**; live OSV audit **153 packages, 0 findings**;
  committed corpus **322 cases × 4 directions**; native helper suite **36 passed**;
  Python helper suite **23 passed**. All supporting logs use `task-3-*` names.
- Native environment diagnosis: first corpus replay returned invalid_protocol after
  a successful audit. Comparing committed provenance identified **44 Windows CRLF
  mismatches**, all matching the expected SHA-256 after LF normalization. Normalizing
  only the disposable Linux copy's protocol source/fixtures produced a successful
  fresh replay; repository protocol files/evidence were not modified.
- Specification validation passed: 13 Markdown files, 107 FRs, 55 UCs, 29 BRs,
  6 milestones and 56 backlog entries. Independent protocol/client review remains
  explicitly pending: `verify_protocol.py` exited 1 with its expected BLOCKED
  message; it is not counted as a green release gate.
- Fresh full unfiltered GREEN:
  `dotnet test src/ArturRios.Cerberus.sln --collect:"XPlat Code Coverage" --logger trx --verbosity minimal`
  in the established WSL/Docker Linux harness exited 0: **8,241 passed, 0 failed,
  0 skipped**. Six fresh TRX/collector families: Domain **361**, Shared **125**,
  Query **606**, Command **1,327**, Data **3,219** (7m26s), WebApi **2,603** (2m53s).
  No compiler warnings or errors. Source and package cache were mounted read-only;
  a disposable `/work` copy excluded prior bin/obj/TestResults, restored locked
  packages and used the host Docker socket with Testcontainers Ryuk disabled.
- Merged fresh ReportGenerator coverage: **98.4% line (15,488/15,728)** and
  **92.4% branch (6,138/6,642)**. All six production assemblies exceed the
  required 90% line threshold; test/foreign assemblies excluded. Exact evidence:
  `task-3-full-tests.log`, `coverage/Summary.json`,
  `task-3-coverage-summary.log` and `task-3-reportgenerator.log`.

  | Production assembly | Line coverage | Branch coverage |
  | --- | --- | --- |
  | Command | 100% (1,090/1,090) | 98.0% (1,037/1,058) |
  | Data | 98.5% (12,113/12,296) | 89.2% (2,795/3,130) |
  | Domain | 99.3% (496/499) | 96.5% (670/694) |
  | Query | 100% (623/623) | 98.8% (714/722) |
  | Shared | 97.2% (355/365) | 88.0% (317/360) |
  | WebApi | 94.8% (811/855) | 89.2% (605/678) |

- Whitespace: `git diff 36d60041beeb3e53c9fa6ba2440d62fdde7046da --check`
  exited 0; staged whitespace is checked immediately before the Task 3 commit.
- Commit subject: `feat: expose collection trash endpoint`. The separate task
  review and final whole-branch review remain the coordinator's next steps.
- Task 3 design rulings: none. Deferred minors: none. Implementation blockers: none.

### Final review and delivery

- Status: pending
