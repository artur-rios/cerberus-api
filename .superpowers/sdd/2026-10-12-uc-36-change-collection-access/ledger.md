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
- [ ] Task 1 review
- [ ] Task 2 - PostgreSQL permission/generation writer
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
