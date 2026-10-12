# UC-36 SDD ledger

## Authorization and binding

- User authorization: continue the previously authorized unattended sequential
  use-case batch through design, TDD implementation, task review, final review,
  exact-head CI, normal squash merge, closure and branch cleanup.
- Issue: #37, UC-36 Change collection access.
- Base: `e9f58936eab3723ad82dfe06f44c7667585bb284`.
- Branch: `feature/uc-36-change-collection-access`.
- Project item: `PVTI_lAHOAgOUtM4BmFmuzg_Ly0U`, In Progress.

## Design rulings

1. PUT path identifies collection and grant; body is exact three required fields:
   positive safe `expectedRevision`, changed `access`, and next-revision
   `keyEnvelope`.
2. The replacement envelope is required because native authority binds the grant
   revision, which advances exactly once.
3. Only an exact active nonterminal grant changes. Revoked/hidden/wrong-collection
   grants are 404; stale, same-access, exhausted and concurrent states are 409.
4. Owner and recipient accounts are locked together in public-UUID order before
   session, selected profile, collection and exact grant.
5. The grant CAS and recipient account revocation-generation increment are one
   atomic authorization write. Existing recipient handles become stale; after a
   fresh unlock, read-write permits writes and read-only denies them.
6. Only grant access/envelope/revision/stamp and recipient generation mutate.
   Account revision/policy/protection, collection/member graphs and sessions remain
   otherwise exact.
7. Success returns the same exact six nonsecret grant/projection fields as UC-35;
   no recipient identity, generation, profile, envelope, pin or key is returned.

## Task status

- [x] Task 1 - Domain and application contract
- [x] Task 1 review
- [x] Task 2 - PostgreSQL permission/generation writer
- [x] Task 2 review
- [x] Task 3 - HTTP/docs/OpenAPI/full verification
- [x] Task 3 review
- [ ] Whole-branch review
- [ ] Exact-head delivery and closeout

## Review findings and evidence

Record task commits, behavioral red/green evidence, focused/full counts, review
findings, corrections, exact source hashes, CI head/base and merge audit here as
the work advances.

### Task 1 implementation evidence (2026-10-12)

- Implementer base: `1568ede` (design commit), on the dedicated UC-36 branch.
- Added immutable `CollectionGrantChangeInput`, request, details and store
  interface plus change command, validator, handler, output and safe messages.
  The exact public body is `expectedRevision`, `access`, `keyEnvelope`; actor,
  collection, grant and vault access are trusted context. The positive expected
  revision is strictly below the protocol maximum and the envelope must bind the
  path grant at the next revision. Same-access conflict, native pin/signature
  agreement and database authority remain the store's responsibility in Task 2.
- Handler retains authentication/access precedence, hashes the handle, forwards
  cancellation, allowlists errors, rejects contradictory/null/corrupt store
  outcomes and returns the exact six sorted nonsecret fields with
  `collection_access_changed` / 200. Duplicate/zero roots are rejected rather
  than silently repaired. No store, registration, route, schema, dependency or
  operational/OpenAPI change is included.
- Behavioral RED was executable before production additions:
  `cerberus-uc36-task1-contract-red` restored and compiled the existing production
  assemblies, then ran two command/domain serialized-boundary cases. Both failed
  `Assert.NotNull` for the absent new types: **2 failed, 0 passed, 0 skipped**,
  exit 1. This records missing-contract behavior, not a compile-only RED.
- The complete typed behavior suite was then added before implementation.
  `cerberus-uc36-task1-types-red` exited 1 with six `CS0246` missing-type errors
  (new command, details, store and handler); no executable tests ran in this
  supplementary RED. No unrelated compiler failure was counted as a test failure.
- Focused GREEN `cerberus-uc36-task1-focused-green`: **121 passed, 0 failed,
  0 skipped**, exit 0. Complete unfiltered Command family
  `cerberus-uc36-task1-command-green`: **1,678 passed, 0 failed, 0 skipped**,
  exit 0. Both compiled Release with warnings as errors and locked restore.
- Exact Windows invocations (all through escalated WSL Ubuntu):

  ```text
  wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-36-change-collection-access/task-1-linux.sh cerberus-uc36-task1-contract-red focused
  wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-36-change-collection-access/task-1-linux.sh cerberus-uc36-task1-types-red focused
  wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-36-change-collection-access/task-1-linux.sh cerberus-uc36-task1-focused-green focused
  wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-36-change-collection-access/task-1-linux.sh cerberus-uc36-task1-command-green all
  ```

- The ignored runner uses `cerberus-uc32-verify:latest` (.NET SDK 10.0.401), a
  read-only source/package mount and an isolated `/work` copy excluding Git,
  scratch and build products. It runs:

  ```text
  dotnet restore tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --locked-mode -p:RestoreAdditionalProjectFallbackFolders=/nuget
  dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~ChangeCollectionAccess --logger "trx;LogFileName=cerberus-uc36-task1-focused-green.trx" --results-directory /evidence
  dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=cerberus-uc36-task1-command-green.trx" --results-directory /evidence
  ```

- Restore/test logs and TRX reports are ignored siblings of this ledger with the
  run names above. Runner SHA-256:
  `566a2487ef53a0f42dd84a6625304d15a1b559538027a62eefbe44011d9a6310`.
- Self-review: checked all Task 1 requirements against the design, adjacent UC-35
  conventions and native envelope structure. No unresolved implementation concern
  found. `git diff --check` passed. Task 1 independent review remains unchecked.
- Limitations: Windows Docker socket access was unavailable; the established
  WSL/Docker harness succeeded. Full six-family coverage, native interoperability,
  database authority/atomicity and HTTP integration are Task 2/3 gates and were not
  claimed by these Command tests. The native fixture proves envelope shape with
  real test signing material, not external-client decryptability.

Exact tested source SHA-256 values (paths relative to the repository):

| File | SHA-256 |
| --- | --- |
| `src/Application/ArturRios.Cerberus.Command/Collections/ChangeCollectionAccessCommand.cs` | `db679daa098db61ac5bba2628066cfc4f2310a5233956887c0304e331b96e979` |
| `src/Application/ArturRios.Cerberus.Command/Collections/ChangeCollectionAccessHandler.cs` | `ed8cc468396ffe65d999bdd717a546d2acc7f09aa5c2636e68c99f6b184de3dc` |
| `src/Application/ArturRios.Cerberus.Command/Collections/ChangeCollectionAccessOutput.cs` | `25833fe425dfec2c7274fcbc997a576a87973a002d0e9665b25fdb70364448ff` |
| `src/Application/ArturRios.Cerberus.Command/Collections/ChangeCollectionAccessValidator.cs` | `1fa4d31a7104690d970cf5f3c6611decc468a9d74db7c558fffa77209a07c403` |
| `src/Application/ArturRios.Cerberus.Command/Collections/CollectionGrantChangeMessages.cs` | `cfdc30d117bdc67bf1b9981eadef4f55c0573c365aae2b9daa1771bb8d3f2811` |
| `src/Domain/ArturRios.Cerberus.Domain/Collections/CollectionGrantChangeContracts.cs` | `8d6efe65be0054ce67660a806248cfcc6f78eca25412b5f9d9f480f11b0d1808` |
| `tests/Application/ArturRios.Cerberus.Command.Tests/ChangeCollectionAccessContractTests.cs` | `6fb40313f025f8e77f0780cc931f2c3447dda4647d12eb7323049ecae4598d51` |
| `tests/Application/ArturRios.Cerberus.Command.Tests/ChangeCollectionAccessHandlerTests.cs` | `f1bb0762232fd58d1d3d534d075e056014836d4ddc39834475c99cb74b1ddff8` |

### Task 1 independent review (2026-10-12)

- Reviewer inspected exact range
  `1568ede..b193ae78b3ca939938c40c5dbb963a74d9886675` against the
  binding design, plan, UC-35 patterns and retained evidence.
- Result: accepted with **0 Critical, 0 Important and 0 Minor** findings.
  The reviewer confirmed the exact three-field input, trusted path/session
  context, revision/envelope agreement, authentication precedence,
  cancellation forwarding, safe status mapping, fail-closed store result
  handling and exact six-field output. Task scope remained limited to the
  contract/application layer and tests.
- Fresh independent focused run: **121 passed, 0 failed, 0 skipped**, exit 0.
  The reviewer also reconciled the executable RED (2 expected failures), typed
  RED (six expected missing-type compiler errors), complete Command GREEN
  (**1,678 passed**) and every recorded source/runner hash.
- The first review wrapper attempt ran all 121 tests successfully but ended with
  exit 127 from a trailing Windows carriage return. A normalized read-only
  rerun completed with exit 0; no repository or retained evidence file changed.
- Exact-range whitespace check passed and the reviewed head/working tree were
  unchanged. PostgreSQL authority, native validation, atomic generation update
  and HTTP integration remain Task 2/3 gates.

### Task 2 implementation evidence (2026-10-12)

- Implementer base: `9804a3a268fd60cec55467c26eae2cd78a92d768`. Scope is
  `CollectionGrantChangeStore`, two focused Data test files, and this ledger.
  No registration, route, migration, dependency or documentation change is included.
- The writer classifies the target before resolving the exact active path grant.
  Visible foreign denial validates current native evidence; hidden/revoked/terminal
  and wrong-collection targets remain 404. Wide and exact-selected owner access
  use the canonical dual-account `FOR UPDATE` statement before session, selected
  profile (`FOR NO KEY UPDATE`), collection and exact grant locks. Authority is
  rechecked after waits. Current protections, native collection/grant binding and
  safe counters precede replacement verification at the next revision.
- One guarded data-modifying CTE performs the grant CAS and dependent recipient
  generation increment. Post-write checks preserve every captured field except
  the separately checked exact generation increment, validate direct roots and
  compare all saved grant fields and submitted envelope bytes before commit.
  Natural owner-session expiry after authorization does not reverse the decision.
- Real PostgreSQL tests cover both directions, Master/PerProfile selection,
  hidden/foreign/revoked/cross-collection/terminal state, stale/no-op/exhausted
  state, stored/caller native failures, direct ancestry, exact mutation scope,
  selected-recipient invalidation, rollback, cancellation and safe failures.
  `pg_blocking_pids` proves same-grant and reciprocal races, both-order recipient
  unlock/protection/lifecycle writers, and account/session/profile/collection/grant
  waits. A lower-account blocker plus `NOWAIT` on higher account, collection and
  grant proves no premature locks. Captured lock SQL/values and
  `EXPLAIN (ANALYZE, FORMAT JSON)` prove target, grant and recipient CTEs each
  return exactly one row despite unrelated inventory.
- Reciprocal changes using original owner handles produce one success and one
  `vault_access_denied`; a fresh unlock lets the waiting actor retry. Unlock-first
  creates a handle that the change invalidates; change-first rejects the old
  challenge with `revision_conflict`, creates no session and leaves it unconsumed.
- Executable preimplementation RED `cerberus-uc36-task2-complete-red`:
  **122 failed, 0 passed, 0 skipped**, exit 1, from the absent store type after
  real PostgreSQL setup. The interface/reflection bootstrap made these executable
  failures; final tests call the concrete store. An earlier partial RED had 103
  expected failures. Two earlier attempts only encountered a missing test import
  and EF SQL analyzer warning; neither was counted as RED.
- Counter RED `cerberus-uc36-task2-counters-red`: **6 failed, 122 passed**, exit 1.
  Unsafe owner/recipient account counters were accepted; current snapshots now
  check account revision, policy and generation ranges.
- Atomic fault RED `cerberus-uc36-task2-generation-fault-red`: **1 failed,
  129 passed**, exit 1; `cerberus-uc36-task2-grant-fault-red`: **1 failed,
  131 passed**, exit 1. Scoped PostgreSQL triggers raise or suppress each update.
  Suppressed writes initially returned conflict; the writer now identifies its
  partial CAS or unchanged authorization snapshot as persistence failure and
  rolls back. Triggers are removed in `finally`; clean retries succeed.
- Final focused GREEN `cerberus-uc36-task2-focused-final-green`: **132 passed,
  0 failed, 0 skipped**, exit 0. The initial implementation run had 120 passes
  and two test-fixture failures from regenerating a randomized assertion envelope;
  the tests now retain the submitted envelope. Intermediate 128/130-case focused
  suites also passed before the final atomic fault additions.
- Complete unfiltered Data family `cerberus-uc36-task2-data-green`: **3,690
  passed, 0 failed, 0 skipped**, exit 0, in 8 minutes 30 seconds. All runs use
  Release, warnings as errors and locked restore. Staged whitespace check passed.
- Exact invocation pattern (escalated WSL Ubuntu):

  ```text
  wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-36-change-collection-access/task-2-linux.sh <run-name> focused
  wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-36-change-collection-access/task-2-linux.sh cerberus-uc36-task2-data-green all
  ```

  `<run-name>` is each exact RED/GREEN name recorded above. The ignored runner
  uses `cerberus-uc32-verify:latest` (.NET SDK 10.0.401), the Docker socket and
  host network, read-only source/package mounts and an isolated `/work` copy:

  ```text
  dotnet restore tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --locked-mode -p:RestoreAdditionalProjectFallbackFolders=/nuget
  dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj -c Release --no-restore --filter FullyQualifiedName~CollectionGrantChangeStoreTests --logger "trx;LogFileName=<run-name>.trx" --results-directory /evidence
  dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj -c Release --no-restore --logger "trx;LogFileName=cerberus-uc36-task2-data-green.trx" --results-directory /evidence
  ```

- Runner, restore/test logs and TRX reports are ignored siblings of this ledger.
  Test-driven-development guided the executable RED cycles;
  verification-before-completion requires focused and full Data gates before
  commit. Task 2 independent review remains unchecked.
- Self-review covered all five binding priorities, actual SQL, adjacent UC-35 and
  recipient writers, native signatures, exact mutation snapshots, trigger faults
  and blocking evidence. Read-only self-review assistance confirmed lock and
  generation consequences and prompted the atomic fault proof. No remaining
  source concern was identified.
- Limitations: HTTP integration, six-family coverage, native interoperability and
  external-client decryptability remain Task 3/release gates. No push or PR.

Exact tested SHA-256 values (repository-relative paths; evidence files are
ignored ledger siblings):

| File | SHA-256 |
| --- | --- |
| `src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionGrantChangeStore.cs` | `21103e16ccc817010bc3ca01c57d8a5325f002b5f4da020bc5120cdc5ed34ae8` |
| `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionGrantChangeStoreTests.cs` | `a3c6a36987f76f91cabdd30250b11b5bca1f5718c760884b157af9cc2fba221c` |
| `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionGrantChangeStoreConcurrencyTests.cs` | `0c28f162d8b8cb5d656b300eb3c9d43f1dbac505e66f359e8a0ade36b5b36877` |
| `task-2-linux.sh` | `defa577358386c08fd3b93fca367b8743f92b5b3ae7ccac9bf1ac810bae82f49` |
| `cerberus-uc36-task2-complete-red.trx` | `a1b7ed98e9861797a4e7fd1412f10fd9ae0636d4079f72528683601f01aae7c2` |
| `cerberus-uc36-task2-counters-red.trx` | `9349605b48f4ca3bbcd049ce29a9fe93000bf9d9c642ef0699f8ec70c0883cfa` |
| `cerberus-uc36-task2-generation-fault-red.trx` | `9d464bbc240d9ddf6585d158e32d78d825e25a17c0ee4afc8181c02fc2296751` |
| `cerberus-uc36-task2-grant-fault-red.trx` | `7e4d80d3ad8f88f7e733fd836939852d1f6b4666ed19da3ef02dc55c4dfb1ca9` |
| `cerberus-uc36-task2-focused-final-green.trx` | `6846c139f12dc9bf9333eba2ae982deec34650064bbfd46cbc76aa3d00840d6e` |
| `cerberus-uc36-task2-data-green.trx` | `1f2e5d8ffda056b6a2ece186987e5856cdf12050864903a75e860dd15579e8c5` |

### Task 2 independent review (2026-10-12)

- Reviewer inspected exact range
  `9804a3a..801dd8fedd4ff5a1e31cb51a5cd72017565fb699` against the
  binding design, plan, UC-35 grant writer and adjacent account/session,
  lifecycle and protection writers.
- Result: accepted with **0 Critical, 0 Important and 0 Minor** findings. The
  review confirmed target-first authority classification, 403/404 native
  validation semantics, canonical strong dual-account locking before all later
  locks, current native binding, exact error taxonomy, one-statement grant CAS
  plus recipient-generation increment, exact mutation scope, rollback and
  post-write grant/generation/direct-root verification.
- Fresh independent read-only-source PostgreSQL run: **132 passed, 0 failed,
  0 skipped**, exit 0. The reviewer reconciled the retained complete Data result
  (**3,690 passed**) and every recorded source, runner and TRX hash.
- Actual `pg_blocking_pids` evidence, both writer acquisition orders, trigger
  suppression faults and bounded `EXPLAIN ANALYZE` assertions were checked and
  accepted as behaviorally meaningful.
- One initial independent wrapper attempt failed before restore/tests because a
  Windows/WSL quoting error supplied an empty evidence mount. A normalized
  base64-transported wrapper succeeded. Its temporary output directory was
  ephemeral, so the fresh command output—not a new retained TRX hash—is the
  review evidence. The repository and retained evidence remained unchanged.
- Exact reviewed head and range whitespace checks were clean. HTTP integration,
  all-family coverage and native interoperability remain Task 3 gates.

### Task 3 implementation evidence (2026-10-12)

- Implementer base: `ac3c4ec`. Added the strict PUT
  `/api/collections/{id}/grants/{grantId}` action and scoped validator, handler
  and store registrations. The route uses the shared bounded strict-body pipeline,
  both canonical path UUIDs, no query, one access header, current identity and the
  exact command status map. No schema or dependency changed.
- Executable preimplementation HTTP RED
  `cerberus-uc36-task3-http-red`: **109 failed, 1 passed, 0 skipped**, exit 1.
  The absent endpoint returned 404/empty bodies instead of the expected success
  or error contract; the existing outer oversized-body guard was the sole pass.
  All setup, native signing, initial POST grants and compilation succeeded.
- First post-route attempt `cerberus-uc36-task3-http-green`: **105 passed,
  5 failed**, exit 1. All five were fixture member-write probes using revision 1
  after collection creation had legitimately advanced the directly linked record.
  The probe now reads current persisted revision and checks the persisted envelope,
  revision and sequence after success, or exact unchanged record state after any
  denied write. No production fix was needed.
- Corrected 110-case suite `cerberus-uc36-task3-http-final-green` passed
  **110/110**, exit 0. Read-only self-review then strengthened multiple sorted
  direct roots, selected-recipient downgrades, and persisted write/no-write
  assertions. Final `cerberus-uc36-task3-http-complete-green` passed **114/114**,
  zero failures/skips, exit 0, in 23 seconds.
- Real native HTTP flows cover both access directions for wide, Master and
  PerProfile owners; unchanged collection/member/account/protection/session/profile
  state except the exact grant and recipient generation; stale recipient wide
  and selected handles; real challenge/proof account and profile unlocks; retained
  direct/inherited reads; permitted read-write edits and unchanged read-only
  denials; exact selected isolation; revoked PUT refusal followed by POST regrant
  and current PUT; relevant-native foreign denials; native binding/counter/state
  errors; both canonical route IDs; exact three-field JSON; required/unknown/
  duplicate/case-forged fields; strict media/UTF-8/BOM/compression/header/query
  rejection; current identity errors; no-store and empty 413. Actual grant and
  generation trigger faults and missing-table outage verify 503 rollback and
  successful clean retry.
- The application OpenAPI generator ran in an isolated work copy:
  `task-3-full.sh cerberus-uc36-task3-openapi generate`, exit 0. Applied its
  generated diff with `apply_patch`; only the new PUT operation and three
  command/output/wrapper schemas are added. Shared media-type/output-requiredness/
  wrapper/empty-413 limitations remain documented.
- Updated collection API, foundation status, changelog and README intended merged
  feature progress. Two independent tracker counts found exactly 55 business UCs:
  **37 Done / 18 Todo**, with UC36 Done and M04 2/6. The foundation row is excluded.
  No external issue/project operation occurred.
- Exact focused commands (escalated WSL Ubuntu):

  ```text
  wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-36-change-collection-access/task-3-linux.sh cerberus-uc36-task3-http-red focused
  wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-36-change-collection-access/task-3-linux.sh cerberus-uc36-task3-http-green focused
  wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-36-change-collection-access/task-3-linux.sh cerberus-uc36-task3-http-final-green focused
  wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-36-change-collection-access/task-3-linux.sh cerberus-uc36-task3-http-complete-green focused
  ```

  Each uses `cerberus-uc32-verify:latest`, read-only checkout/NuGet fallback
  mounts, a fresh isolated `/work`, actual PostgreSQL Docker fixtures, locked
  Web project restore and Release tests filtered to
  `FullyQualifiedName~CollectionGrantChangeHttpTests`. Logs and TRX files are
  ignored ledger siblings.

- Native verification completed with exit 0 in a fresh disposable
  `cerberus-uc33-native:latest` environment (CPython 3.14.8, Temurin 25.0.4.1+1,
  Maven 3.10.0, Linux x86_64), from 09:32:31Z to 09:33:20Z. Java **173/173**,
  Python **142/142**, native helpers **36/36**, repository helpers **23/23**:
  zero failures/errors/skips. Current corpus replay passed **322 cases in all four
  directions** and reran both complete native suites. Live OSV at
  `2026-10-12T09:33:03.384698+00:00` queried **153 package coordinates**, zero
  findings. All dependencies were freshly hash-locked downloaded/installed; no
  preserved cache fallback was used.
- Native command:

  ```text
  wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-36-change-collection-access/native-run.sh cerberus-uc36-native
  ```

  The ignored `native-in-container.sh` records exact fresh virtualenv, wheel,
  Maven ZIP/hash/cache setup followed by:

  ```text
  tools/protocol-harness/java/mvnw -B -Dmaven.repo.local="$HARNESS_MAVEN_CACHE" -f tools/protocol-harness/java/pom.xml clean test dependency:tree
  PYTHONPATH=tools/protocol-harness/python "$HARNESS_PYTHON" -m unittest discover -s tools/protocol-harness/python/tests -t tools/protocol-harness/python -v
  "$HARNESS_PYTHON" tools/protocol-harness/audit.py --maven-cache "$HARNESS_MAVEN_CACHE" --wheels "$CERBERUS_PYTHON_WHEELS" --maven-distribution "$CERBERUS_MAVEN_DISTRIBUTION"
  "$HARNESS_PYTHON" /evidence/native-corpus.py
  python -m unittest discover -s tools/protocol-harness/tests -v
  python -m unittest discover -s scripts -p 'test_*.py' -v
  python scripts/verify_protocol.py
  python /evidence/native-summary.py
  ```

  `native-corpus.py` invokes the current `verify.py` module's
  `verify(java, python, check=Path('/work/docs/security/interoperability-vectors.json'))`
  and retains the full result. Raw logs, JUnit reports, source/evidence manifests
  and result are ignored ledger siblings. The stopped `cerberus-uc36-native`
  container preserves the tested snapshot and new cache.
- Native source binding: all **100** source/helper/corpus file hashes remained
  unchanged after execution. Exactly 44 provenance files plus the Maven wrapper
  required CRLF-to-LF normalization only in the disposable copy; each normalized
  provenance hash equaled the committed corpus and Git bytes. Wrapper Git blob
  `bd8896bf2217b46faa0291585e01ac1a3441a958`, qualified SHA-256
  `cae96cef89ebea3531221f4ae17c23cf8edf67d00eae8306d4186ae1bbed4d02`.
  Repository protocol bytes stayed unchanged. This run had no formatter or
  evidence-summary failure; UC35's historical failure was not relabeled or altered.
- Native limitations: OSV is a dated package-version observation, not runtime/OS
  qualification or client decryptability. Independent release approval remains
  exactly the expected exit 1:
  `BLOCKED: Protocol security and client review is pending.`

Exact Task 3 SHA-256 values (repository-relative production/test paths;
runner/evidence files are ignored ledger siblings):

| File | SHA-256 |
| --- | --- |
| `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/CollectionsController.cs` | `f093a8544aaa58a237238492e067f7c4602fb51e49138bf3b2be3b88970bf87c` |
| `src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs` | `0571b33510b3a98568e9d891ff198b9a017b52f4933063b04f741e200c3a5ac7` |
| `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionGrantChangeHttpTests.cs` | `6544eaa4a092a15c19bbace4cbb6cc1882224aaa5cdb4770fe79ea5bd64d2880` |
| `docs/contracts/openapi.json` | `5af127ebb77f22c72c043e715231584cf8aca41b5c7fe93cd95ba14c36433fdd` |
| `task-3-linux.sh` | `1b562084d0b788744f25ccdd13124c562c522254519fb639dc86fa121cdaf4d6` |
| `task-3-full-first-attempt.sh` | `7b8bec52c699073f4194a93cc42f0f5aa9864b0e6167c6e24774cfcf29ad39cb` |
| `task-3-full-inner-first-attempt.sh` | `5157cf225698f4ce20d8607cd7ca4f6fbf5b92668cf60df2ba9986bd4d28828b` |
| `task-3-check.py` | `dcc5a00b0eb395fca20150ad3eca68f0a8a687e61f617082d1947f0304a0250f` |
| `cerberus-uc36-task3-http-red.trx` | `2dfd83245978bc7b36d15a214ac5b6e6a2889745bcfd915501b9648d5065c1cc` |
| `cerberus-uc36-task3-http-complete-green.trx` | `3aacc701b026e7814002a75ea603b92a2af7ff134a51f0e3ea3fcaade3c8b297` |
| `native-summary.json` | `c77f0f39d560e861d8f3bf320b6f6af2b395c06f4ba3de75cf1b1affe6320e18` |
| `native-source-manifest.json` | `d85c6915f9e6e95e7dd56f24d37268008732cfe535120effd7c87d70b7e9614c` |
| `native-corpus-result.json` | `b0b8c0ae61ea0ef10ba749ebba509c45dad88d9ce0895e6b4df3217718ea6964` |
| `native-audit.log` | `8f4ec4c6f741904bcf477fade926934120266f2b40ddee6ec7d943097c2ec370` |

### Full verification runner correction

- First complete matrix `cerberus-uc36-task3-full` ran all six unfiltered
  Release families successfully: **9,406 passed, zero failed/skipped**
  (Command 1,678; Query 606; Shared 125; Domain 361; Data 3,690; WebApi 2,946).
  Data took 8m33s and WebApi 3m51s. Locked restore, live direct/transitive NuGet
  audit, Release build (zero warnings/errors), 23 repository helpers,
  specifications and OpenAPI drift/shape checks also passed.
- The runner subsequently exited **1** at `scripts/coverage.py --report-only`
  with `FileNotFoundError: reportgenerator`. Read-only image inspection
  confirmed no global tool was installed. The original runner removed the
  container before its later archive step, so that attempt's six collector files
  were not retained. Its raw log and 646-file source manifest remain intact;
  no merged coverage success is attributed to that attempt.
- The original ignored runners are preserved as
  `task-3-full-first-attempt.sh` and
  `task-3-full-inner-first-attempt.sh`, with their hashes above. The corrected
  runner installs pinned ReportGenerator **5.5.11** before tests, verifies it
  starts, archives all TRX/Cobertura evidence immediately after the tests, and
  retains the isolated container. Reporter installation and preflight succeeded.
  Product code and tests were unchanged. A new complete fresh coverage run is
  `task-3-full.sh cerberus-uc36-task3-full2`.

### Final complete verification and Task 3 closeout

- Authoritative complete run `cerberus-uc36-task3-full2` exited **0** with
  **9,406 passed, zero failed/skipped**: Command **1,678**, Query **606**,
  Shared **125**, Domain **361**, Data **3,690** (8m34s), WebApi **2,946**
  (3m55s). All six fresh TRX/Cobertura reports are retained in
  `cerberus-uc36-task3-full2-test-results.tar.gz`; the stopped same-name
  container preserves the tested `/work` snapshot.
- ReportGenerator 5.5.11 merged those six collectors at
  `2026-10-12T09:54:31Z`: line **98.5% (15,624/15,857)** and branch
  **92.3% (6,893/7,466)**. Exact six-production-assembly inventory and every
  individual 90% line floor passed: Command **1,243/1,243 (100%)**;
  Query **580/580 (100%)**; Shared **311/321 (96.8%)**;
  Domain **513/516 (99.4%)**; Data **12,221/12,396 (98.5%)**;
  WebApi **756/801 (94.3%)**. Percentages are the report's displayed values;
  exact line counts are retained. Test/foreign/native-harness assemblies are
  excluded from production coverage.
- Locked solution and OpenAPI-generator restore, Release build (zero warnings/
  errors), live direct/transitive NuGet audit (zero known vulnerabilities),
  **23** repository helper tests, specs and complete generated OpenAPI drift
  passed. Specs validated 13 Markdown files, 7 template structures, links/anchors,
  107 FRs/55 UCs, 12 features, 29 BRs, 6 milestones and 56 backlog entries.
  The shape gate proved exact PUT 200/400/401/403/404/409/413/503 statuses,
  three required input fields with nonnullable revision, exact six output
  properties, exact path/header parameters and unchanged prior operations/models.
- Exact full invocation and key inner commands:

  ```text
  wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-36-change-collection-access/task-3-full.sh cerberus-uc36-task3-full2
  dotnet restore src/ArturRios.Cerberus.sln --locked-mode -p:RestoreAdditionalProjectFallbackFolders=/nuget
  dotnet restore tools/OpenApiGen/OpenApiGen.csproj --locked-mode -p:RestoreAdditionalProjectFallbackFolders=/nuget
  dotnet tool install --tool-path /opt/uc36-report dotnet-reportgenerator-globaltool --version 5.5.11
  python3 scripts/vulnerabilities.py
  dotnet build src/ArturRios.Cerberus.sln --configuration Release --no-restore
  python3 -m unittest discover -s scripts -p "test_*.py" -v
  python3 scripts/verify_specs.py
  python3 scripts/openapi.py
  python3 /evidence/task-3-check.py shape
  dotnet test src/ArturRios.Cerberus.sln --configuration Release --no-build --logger trx --collect:"XPlat Code Coverage"
  python3 scripts/coverage.py --report-only
  python3 /evidence/task-3-check.py results
  ```

  Runtime: .NET SDK 10.0.401, .NET/ASP.NET 10.0.12, Debian 12 Linux x64,
  real PostgreSQL 18.6-alpine fixtures. The runner uses read-only source/package
  mounts and an isolated work copy. Independent release approval again returned
  the exactly expected pending-review exit 1; the development runner validated
  that result explicitly and ended 0.
- All **646** tested source/test/helper/tool/OpenAPI hashes matched after the
  suite and again against the current worktree. The first and second complete
  source manifests are identical. Native source and all **20** retained native
  evidence/runner hashes were independently reconciled. No schema, dependency,
  protocol source or tracked stale checkpoint changed.
- Self-review covered route/DI, actual HTTP behavior, generated contract and
  operational documentation. Working and original-base whitespace checks passed.
  Task 3 implementation is complete; commit subject:
  `feat: expose collection permission changes`. Task 3 review, whole-branch
  review and exact-head delivery remain unchecked under parent control. No push,
  PR, issue/project change, merge or closure occurred.
- Remaining limitations are the explicitly documented shared generated metadata,
  independent protocol/security/client approval, external authenticated recipient
  key discovery/decryptability and UC37/UC48 follow-on behavior. Native/.NET tests
  prove server/protocol behavior, not external-client or production qualification.

Final complete-run SHA-256 values:

| File | SHA-256 |
| --- | --- |
| `cerberus-uc36-task3-full2.log` | `09c4e52062d4744d31da1b75fd732d1b9aee0dcc474d348a33918ded0ecc36c9` |
| `cerberus-uc36-task3-full2-evidence.json` | `a440d9f4fb68caf962e1fc9834f55434c23df14cfeaae12bc38162f674245717` |
| `cerberus-uc36-task3-full2-coverage-summary.json` | `fc1268a343b6fed08a9b7640267c0d3e7a73fa64efbf61a494291664330a1246` |
| `cerberus-uc36-task3-full2-source-sha.json` | `e563f028736ccf92e84a4038c4d650a08362b6b675109b2c5278c80516fcf42a` |
| `cerberus-uc36-task3-full2-test-results.tar.gz` | `01c0a9888b0f006510cc258929d24116ac4d1e4abeba68305d7b53efe7232f2b` |
| `task-3-full.sh` | `c6ae28cb2c4c98cdaacac6d4c2651b8aed83a52af73be8551dec2b19661d0945` |
| `task-3-full-inner.sh` | `c88e671ba7b5bd22e8792f7b5e6a7e4494da3f9d5f7bc64b64c031a82e0ad131` |

### Task 3 independent review (2026-10-12)

- Reviewer inspected exact range
  `ac3c4ec..f09e384a23c3d5c4ba5999ab328990c6f7074b7f` against the
  complete design, plan and ledger, including route, DI, handler/store/native
  behavior, HTTP tests, generated OpenAPI and operational documentation.
- Result: accepted with **0 Critical, 0 Important and 0 Minor** findings. Strict
  transport, canonical route context, authority precedence, status/output
  contracts, cancellation, no-store behavior, atomic rollback, both permission
  directions, selected isolation and regrant behavior all matched the design.
- Fresh independent read-only-source HTTP run: **114 passed, 0 failed,
  0 skipped**, exit 0; temporary output only and no repository changes.
- The reviewer independently reconciled the meaningful RED (109 failures and one
  existing oversize pass), all **9,406** full-suite passes, raw coverage
  (**15,624/15,857 lines**, **6,893/7,466 branches**), every assembly's 90%
  line floor, native Java 173/Python 142/helpers 36/repository helpers 23/corpus
  322 by four directions, and OSV 153 coordinates with zero findings.
- All 646 full-run source hashes, 100 native source hashes, 20 native evidence
  hashes and 39 ledger hash entries matched. Full1's reporting-tool failure and
  corrected Full2 success were truthfully disclosed.
- Generated OpenAPI added only the intended PUT and three schemas; shared
  metadata limitations remained documented. README independently counted
  **37 Done / 18 Todo**. Exact reviewed head, working tree and whitespace checks
  were clean. Independent protocol/security/client approval remains external.
