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
- [ ] Task 2 review
- [ ] Task 3 - HTTP/docs/OpenAPI/full verification
- [ ] Task 3 review
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
