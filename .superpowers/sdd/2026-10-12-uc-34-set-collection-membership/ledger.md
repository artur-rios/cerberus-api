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

- Status: implemented, verified, committed and independently approved; base
  `ca6b92b`; task commit `8121dc6`.
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
  decryptability/protocol qualification remains a release gate.
- Independent review: approved exact range `ca6b92b..8121dc6`; Critical 0,
  Important 0, Minor 0. Reviewer independently rebuilt and reran 212 focused Linux
  tests, confirmed the 3,431-test full log, exact snapshot hashes, whitespace and a
  clean worktree. All five review focuses and AF-01 through AF-06 were approved.
- Reviewer declined HTTP/DI/OpenAPI/transport, fresh six-family/audit/delivery gates,
  external-client qualification, UC-48 offline events and the explicit pre-UC35
  recipient-account FK audit as correctly belonging to later work.

### Task 3

- Status: implemented, fully verified, committed and independently approved.
- Base: `49546bb`. Read complete design, plan, preceding ledger and Tasks 1/2
  production contracts/store. Kept the assigned existing feature checkout and
  parent-controlled task lifecycle/`ledger.md` established by Task 1.
- Test-first RED: `wsl.exe -d Ubuntu -- bash
  /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-34-set-collection-membership/task-3-linux.sh
  cerberus-uc34-task3-red`, before route/DI existed, exit 1:
  116 failed / 1 passed / 0 skipped of 117 in 1m10s (`task-3-red.log`).
  Missing-route responses caused expected-status failures; the six lock-wait
  probes timed out because no route entered the transaction. One existing identity
  rejection already passed. All prerequisites compiled and actual endpoint creation
  succeeded. No Task 1/2 production change was required.
- Minimal production changes: exact `PUT /api/collections/{id}/members` route,
  trusted context and response metadata, `VaultProofBody`, explicit store/validator/
  handler registrations. The shared corrected exact JSON media-type rule is reused.
- Focused GREEN: same Linux runner with `cerberus-uc34-task3-green`, exit 0,
  117/117, zero failures/skips in 15s (`task-3-focused.log`).
- Complete Web GREEN: same runner with `cerberus-uc34-task3-web all`, exit 0,
  2,727/2,727, zero failures/skips in 3m12s (`task-3-web.log`).
- Tests exercise actual profile/access/record/folder/collection HTTP prerequisites,
  owner wide/Master/PerProfile success, exact six fields/sorted IDs, same-epoch
  envelope replacement, GET detail, recipient GET/list additions/removals, future
  dynamic descendants, unchanged grant wrappers/revisions, selected private scope
  and record-container denial, inherited promotion/clear, native RO/RW denial,
  hidden/native corruption precedence, strict route/header/query/media/body rules,
  no-store and empty 413, current identity/session/lifecycle and waiting authority
  changes, same-revision winner/lost retry, outage and post-link-write rollback with
  clean retry. Account/protection/access and native grant rows are fixture setup;
  no grant-creation HTTP endpoint exists before UC35.
- Documentation updated only after focused/full Web passed: security/operations
  contract, CHANGELOG, README UC34 Done/M03 26/26, foundation status, generated
  OpenAPI. Release approval and external-client decryptability are not claimed.
- Required Linux/native scripts and raw logs are retained in this plan workspace:
  `task-3-full.sh`, `task-3-native.sh`, `task-3-native-python.sh` and
  `task-3-linux.sh`. Main runners copy the read-only checkout to a disposable
  `/work`, exclude old bin/obj/TestResults, and use task-specific containers/caches;
  the explicit Python count run uses the identical preserved native snapshot.
- Full prerequisite commands in `task-3-full.sh`: `dotnet restore
  src/ArturRios.Cerberus.sln --locked-mode`, `python3 scripts/vulnerabilities.py`,
  `python3 scripts/openapi.py --write`, `python3 scripts/openapi.py`,
  `python3 -m unittest discover -s scripts -p "test_*.py" -v`, and
  `python3 scripts/verify_specs.py`. All passed; 23 Python helpers, no known direct
  or transitive NuGet vulnerabilities, no OpenAPI drift. Corresponding raw logs:
  `task-3-restore.log`, `task-3-nuget-audit.log`, `task-3-openapi-write.log`,
  `task-3-openapi-drift.log`, `task-3-python-helpers.log`, `task-3-specs.log`.
  Live `/source` specs were rechecked after README/status edits and passed.
- Native command: `wsl.exe -d Ubuntu -- bash
  /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-34-set-collection-membership/task-3-native.sh`,
  exit 0. Fresh hash-locked wheels/venv, checked Maven 3.10.0 distribution and fresh
  Maven cache; `mvnw -B -Dmaven.repo.local="$HARNESS_MAVEN_CACHE" -f
  tools/protocol-harness/java/pom.xml clean test dependency:tree`: 173 tests,
  zero failures/errors/skips (`task-3-maven.log`). The audit command
  `"$HARNESS_PYTHON" tools/protocol-harness/audit.py --maven-cache
  "$HARNESS_MAVEN_CACHE" --wheels "$CERBERUS_PYTHON_WHEELS"
  --maven-distribution "$CERBERUS_MAVEN_DISTRIBUTION"` passed 153 coordinates,
  zero findings at 2026-10-12T05:03:22Z (`task-3-native-audit.log`).
- `"$HARNESS_PYTHON" tools/protocol-harness/verify.py --check
  docs/security/interoperability-vectors.json`: 322 cases, four directions,
  both complete native suites (`task-3-native-corpus.log`).
  `python3 -m unittest discover -s tools/protocol-harness/tests -v`: 36/36
  (`task-3-native-helpers.log`). An explicit native Python suite run from the
  identical preserved native snapshot recorded 142/142, zero failures/errors/skips
  (`task-3-native-python.log`, `task-3-native-python.sh`). Its first diagnostic
  one-line shell attempt lost a variable at the Windows/Bash boundary and ran no
  tests; the explicit script corrected command transport, not product code.
- `python3 scripts/verify_protocol.py`: exit 1, exactly `BLOCKED: Protocol
  security and client review is pending.` (`task-3-protocol-gate.log`). This
  expected independent release gate is not represented as development approval.
- Runtime evidence (`task-3-runtime.log`, `task-3-native-runtime.log`): Linux x64
  Debian 12, .NET SDK 10.0.401, .NET/ASP.NET 10.0.12, PostgreSQL 18.6-alpine,
  CPython 3.14.8, Temurin 25.0.4.1+1, Maven 3.10.0, ReportGenerator 5.5.11.
- Task 3: Ruling: normalize the 44 Windows CRLF protocol provenance files only
  in the disposable native copy and only when the resulting SHA-256 exactly
  matches committed corpus provenance - follows the established UC33 harness
  and parent instruction without modifying repository protocol bytes - cost if
  wrong: a separate platform that intentionally qualifies CRLF would need its
  own provenance; this run qualifies the committed LF protocol corpus.
- Task 3: Ruling: remove one trailing blank line from the earlier design, plan
  and ledger - the required original-base whitespace gate exposed these inherited
  formatting errors - cost if wrong: no runtime or design behavior changes, only
  final-file whitespace differs from the earlier review snapshot.
- Generated OpenAPI was copied from the actual generator and matched SHA-256
  `9efe97184897a61d6b9b2598e377f8aa7a6acb57957fe7d352085ad1f7dd9409`.
  Parsed route metadata has exactly 200/400/401/403/404/409/413/503; the command
  schema has exactly five required properties and output exactly six properties.
  Existing broader advertised JSON types, nullability/requiredness/wrapper and
  empty-413 schema limitations remain documented, not silently claimed fixed.
- Snapshot/worktree byte equality: controller SHA-256
  `4b80a2cee22146161e02545dc8fbbee221139a8466272d39a30fb456068d8ad2`, Startup
  `51a71ab9c1e2c9b74159ccb7f811d2931e98183ad8e923e924e607dd4232102c`, HTTP tests
  `36ed8e70da538e85658f2c59cf29cc01a0968a93dba917ab56f9823ff03dda4d`.
- Authoritative full command: `wsl.exe -d Ubuntu -- bash
  /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-34-set-collection-membership/task-3-full.sh`,
  exit 0. It runs unfiltered `python3 scripts/coverage.py`, which invokes
  `dotnet test /work/src/ArturRios.Cerberus.sln --collect:XPlat Code Coverage`.
  Fresh six-family total: 8,687/8,687, zero failures/skips; Domain 361,
  Shared 125, Query 606, Command 1,437, Data 3,431 (7m51s), Web 2,727 (3m07s).
  Six new Cobertura collectors merged at 2026-10-12T05:12:12Z:
  line 98.4% (15,758/16,002), branch 92.6% (6,414/6,926).
  Every production assembly exceeds 90% line: Command 1,162/1,162 (100%),
  Data 12,282/12,468 (98.51%), Domain 511/514 (99.42%), Query 623/623 (100%),
  Shared 355/365 (97.26%), WebApi 825/870 (94.83%). Per-assembly threshold
  and exact six-assembly inventory independently checked from
  `task-3-coverage-summary.json`; complete output is `task-3-coverage.log`.
- Exact production/test/docs/OpenAPI/plan/ledger diff inspected. Working and
  original-base `git diff 1283500 --check` pass after the whitespace correction.
  Task 3 implementation: complete (base `49546bb`; commit
  `feat: expose collection membership replacement`; evidence above).
- No Task 3 behavior was consciously deferred; no Task 1/2 runtime change was
  needed. Publishing/PR/CI/merge and the pre-UC35 audit remain parent-controlled
  later steps. Nothing was pushed and no project item was moved to Testing.
- Independent Task 3 review: approved exact range `49546bb..c41f5cc`; Critical 0,
  Important 0, Minor 0; no correction pass required. Reviewer independently rebuilt
  and ran 117 focused Linux HTTP tests, confirmed the preserved full container exit,
  source/test/OpenAPI hashes, 8,687-test log, coverage, native/dependency/spec/OpenAPI
  evidence, whitespace and clean worktree. Final whole-branch review may start.
- Reviewer treats README UC34 Done/M03 26/26 as intended post-delivery branch state;
  remote issue/project/PR/CI/merge/branch cleanup remain pending. Generated OpenAPI
  limitations, external-client qualification and the pre-UC35 audit remain explicit.

### Final review and delivery

- Status: pending.
