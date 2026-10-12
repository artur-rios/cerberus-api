# SDD ledger - plan: docs/superpowers/plans/2026-10-12-uc-34-set-collection-membership.md

## Preflight

| Task | Implementer model | Review model | Shared interfaces/files | Baseline dependency |
| --- | --- | --- | --- | --- |
| 1 - contracts/command | gpt-6.1-sol high | gpt-6.1-sol high | Defines `CollectionMembership*` used by Tasks 2/3 | UC33 merged tree; Command baseline green |
| 2 - transactional store | gpt-6-astra xhigh | gpt-6.1-sol high | Consumes Task 1; owns `CollectionMembershipStore` | Task 1 committed/reviewed; Data baseline green |
| 3 - HTTP/docs/full verification | gpt-6-astra xhigh | gpt-6.1-sol high | Consumes Tasks 1/2; shared controller, Startup, docs/OpenAPI | Tasks 1/2 committed/reviewed; Web/native baseline green |
| Final whole-branch review | gpt-6-astra ultra | n/a | Reviews original base through tested Task 3 head | All tasks committed/reviewed; full evidence recorded |

Tasks are strictly sequential. No two implementers run concurrently. Each reviewer
receives the task base/head diff and test evidence; the final reviewer is fresh.

## Baseline

- Original base: `1283500c1614521906fd5c842405b230a1f06f0e`
- Original tree: `043164c43dc0b6694e93c9255640d6181dd382a0`
- Inherited UC33 evidence: 8,248 tests, zero failures/skips; 98.4% line and
  92.4% branch coverage; every production assembly above 90% line; Maven 173,
  OSV 153/0, corpus 322 across four runtimes, helpers 36 + 23; exact-head required
  CI successful and merged tree identical.
- Branch: `feature/uc-34-set-collection-membership`
- Project item `PVTI_lAHOAgOUtM4BmFmuzg_Lyug` moved to In Progress.

## Rulings

1. Same-epoch collection envelope is the atomic opaque client key-manifest boundary.
2. Active grant wrappers are validated and preserved, not revision-bumped.
3. Only direct symmetric-difference members receive structural bumps.
4. Selected access may retain/promote already effective members but cannot introduce
   any item outside its current effective scope.

## Task evidence

### Task 1

- Status: implemented, verified, committed and independently approved.
- Base: `0ce71e7`.
- Discovery: escalated `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --no-restore --list-tests --filter FullyQualifiedName~UpdateCollectionHandlerTests --verbosity minimal`, exit 0; existing collection tests listed successfully.
- RED: focused `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --no-restore --filter FullyQualifiedName~SetCollectionMembershipHandlerTests --verbosity minimal`, exit 1 with seven CS0246 errors for missing membership types, before any new production code.
- Task 1: Ruling: preserve the existing `ledger.md` and parent-controlled sequential task lifecycle rather than introducing skill-script `progress.md` bookkeeping - coordinated record and explicit Task 1-only brief govern - cost if wrong: automation expecting the skill's default filename would need adaptation.
- Windows sandbox .NET attempt returned exit 1 without test execution; approved narrowly scoped `require_escalated` .NET execution provides actual discovery/RED evidence.
- GREEN: focused Command test command above passed 110/110, zero failures/skips; complete `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --no-restore --verbosity minimal` passed 1,437/1,437, zero failures/skips.
- Changes: strict five-field Domain/Command input, hashed trusted context, safe-error allowlist, fail-closed null/error-plus-data/mismatched results, safe counters, matching microsecond UTC edit time, distinct typed membership equality and sorted exact six-field output. Store/HTTP/Startup untouched.
- Verification: `git diff --check` passed; exact staged diff inspected before commit.
- Task 1 implementation: complete (base `0ce71e7`; commit
  `feat: define collection membership contract`; tests above).
- Independent review: approved exact range `0ce71e7..3d7c157`; Critical 0,
  Important 0, Minor 0. Reviewer independently reproduced 110 focused and 1,437
  complete Command tests with zero failures/skips, confirmed exact-range whitespace,
  clean scope/worktree and authorized Task 2 to start. Historical RED ordering is
  ledger evidence rather than a retained raw log; no correctness finding resulted.
- Reviewer declined Data authority/grants/locking/transactionality, HTTP integration
  and external-client qualification as correctly belonging to later gates.

### Task 2

- Status: implemented and verified; base `ca6b92b`; independent review pending.
- Test-first RED: focused Data compilation after correcting test-only move request
  argument order and EF analyzer syntax failed solely with 14 CS0246 references
  to absent `CollectionMembershipStore`. Raw evidence: `task-2-red.log` in this
  workspace. Production store did not exist at this point.
- Continuing Task 1's ledger filename/parent-controlled lifecycle ruling.
- Initial focused run: 172/173 passed. The remaining test's synthetic grant insert
  omitted the required `concurrency_stamp`; correcting the fixture produced the
  expected graph-growth conflict without changing production behavior.
- Additional behavioral RED: three tests demonstrated skipped evidence for an
  active grant with a missing recipient, and late mutation of an already-verified
  collection/member. `task-2-projection-red.log`: 3 failed, 13 passed. The store now
  verifies native evidence cardinality and final target/member projections.
- Focused GREEN before final concurrency additions: 193/193, zero failures/skips.
- Focused GREEN before lifecycle clarification (Linux): 199/199, zero failures/skips in 28 seconds, using
  `docker exec -w /work cerberus-uc34-task2-data dotnet test
  tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj
  --no-build --no-restore --filter FullyQualifiedName~CollectionMembershipStoreTests
  --verbosity minimal`. Raw evidence: `task-2-focused-linux.log`.
- Lifecycle behavioral RED: retained requested folders/records and their required
  active ancestors with nonnull purge deadlines were incorrectly accepted in wide
  and selected scope. `task-2-lifecycle-red.log`: 8 failed, 1 passed (hidden private
  member remained nonrevealing 404). Added only the missing active-null-purge
  validation; untouched ancestor counters/timestamps are still not parsed.
- Task 2: Ruling: active-null-purge consistency applies to every requested direct
  member, including retained members, and required active ancestry used for
  admission - parent clarified the required structural-corruption invariant -
  cost if wrong: a contradictory active-but-scheduled-for-purge row is rejected
  with 503 instead of treated as a valid membership route. Hidden admission still
  wins with nonrevealing 404.
- Missing-projection behavioral RED: actual post-write deletion of the target,
  changed folder/record, or an already-verified folder escaped as an unhandled
  `InvalidOperationException`. `task-2-missing-projection-red.log`: 4 failed,
  0 passed. Replaced only the relevant projection reads with `SingleOrDefaultAsync`
  and explicit null-to-503 checks; no broad exception catch was added. Parent
  confirmed this belongs to the existing post-write verification/rollback contract.
- Coverage: wide/selected typed overlap, inherited promotion, exact whole-owner
  graph normalization, independent collection preservation, recipient GET/list
  effects through overlapping grants, malformed native bindings/pins, current
  authority and hidden-before-stale precedence, complete/invalid ancestry, all
  nine lock phases, both orders of actual owner collection/profile/content writers,
  native recipient edits and native protection rotation; grant FK growth in both
  orders, final graph/evidence guards, expiry at the linearization point, bounded
  target EXPLAIN among 100 unrelated collections, all nine actual mutation-stage
  failures, late projection faults, sequence exhaustion/regression after target
  write, clean retry and exactly one same-revision winner.
- Windows full-project attempt is diagnostic, not authoritative: existing durable
  ledger/restore/permanent-erasure tests require Linux. A simultaneous focused
  rebuild also met the Windows testhost's xunit DLL file lock. No production or
  test configuration was weakened; Linux verification runs from a disposable copy.
  Stopped only the verified diagnostic Windows testhost after 12m36s; its partial
  aborted result was 2,725 passed / 259 failed / 0 skipped, 2,984 executed, exit 1
  (`task-2-data.log`). Initial failures are the explicit unsupported-platform
  exception; the run later also reported synthetic-link/persistence failures
  across existing and new tests. Their exact Windows-only cascade was not pursued
  after the supported Linux suite passed. These are not passing evidence.
- Task 2: Ruling: use the established Linux runner for complete Data verification
  rather than treating Windows-only filesystem failures as product regressions -
  FileErasureLedger explicitly requires the approved Linux host, and UC33 used this
  runner - cost if wrong: Windows-specific behavior would need a separate supported
  platform qualification, which this Linux service does not currently promise.
- Full Linux command: `wsl.exe -d Ubuntu -- bash
  /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-34-set-collection-membership/task-2-linux.sh`.
  Script uses SDK 10.0, host Docker socket/network, disabled Ryuk, read-only source
  and NuGet fallback cache, disposable `/work`, excludes old bin/obj/TestResults,
  and restores then runs the unfiltered Data project. Raw log:
  `task-2-data-linux.log`: 3,418/3,418 passed, zero failures/skips in 8m19s.
  That earlier runner then exited 127 because adding optional runner arguments
  while its shell was running shifted Bash's post-Docker file offset. No tests
  failed; the stable-runner final refresh below remains the authoritative gate.
- Lifecycle-refreshed Linux command adds container name `cerberus-uc34-task2-final`
  to that runner, after the lifecycle correction. Raw full log:
  `task-2-data-linux-final.log`: 3,427/3,427 passed, zero failures/skips in 8m04s,
  exit 0. Focused verification from its exact
  built snapshot uses the focused command above with this final container name;
  raw log `task-2-focused-linux-final.log`: 208/208 passed, zero failures/skips in
  29 seconds, exit 0. This includes all nine lifecycle regressions.
- Final post-write-projection refresh uses container `cerberus-uc34-task2-complete`
  with the same stable runner. Full raw log: `task-2-data-linux-complete.log`,
  3,431/3,431 passed, zero failures/skips in 8m06s, exit 0; exact-built-snapshot focused log:
  `task-2-focused-linux-complete.log`: 212/212 passed, zero failures/skips in
  29 seconds, exit 0. This includes all four missing-projection regressions.
- Final snapshot/workspace SHA-256 equality checked: store
  `20f7a2038bdf0164f5af32d928942e4aab50c2fbc9ca37c1a2828980d323324f`, tests
  `b6b8171c0b03d20c6ff001e562147ea9ba55c2df684e050367b17687ff4ed936`.
- Exact production/test/plan/ledger diff inspected; staged and working
  `git diff --check` passed. Task commit: `feat: replace collection memberships`.
- No Task 2 coverage consciously deferred. HTTP/DI/OpenAPI/documentation and fresh
  six-family coverage/native audits belong to Task 3; independent external-client
  decryptability/protocol qualification remains a release gate. The separate Task 2
  review must approve this commit before Task 3 begins.

### Task 3

- Status: pending.

### Final review and delivery

- Status: pending.

