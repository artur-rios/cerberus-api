# SDD ledger - plan: docs/superpowers/plans/2026-10-12-uc-37-revoke-collection-access.md

## Authorization and binding

- User authorization: approved the UC-37 design, written implementation plan and execution.
- Branch: `feature/uc-37-revoke-collection-access`.
- Design: `docs/superpowers/specs/2026-10-12-uc-37-revoke-collection-access-design.md` at `d9f3bfa7885280172dcbbf4e2680a124003cda39`.
- Plan: `docs/superpowers/plans/2026-10-12-uc-37-revoke-collection-access.md` at `327168a46910988f47dbe87c6c10c439e855f439`.
- Delivery target: issue #38 and its existing project item only.

## Dependency preflight

| Producer | Consumer | Boundary | Result |
|---|---|---|---|
| Task 1 | Task 2 | Revocation request/store/result domain contract | Exact signature, identifiers and revision semantics agree. |
| Task 1 | Task 3 | Command, validator, handler, output and status map | Controller inputs and exact response contract agree. |
| Task 2 | Task 3 | Atomic PostgreSQL store through the Task 1 interface | DI and transport consume stable store outcomes without a parallel persistence path. |
| Tasks 1-3 | Task 4 | Commits, evidence and this ledger | Whole-branch review and delivery consume the accumulated exact branch. |

Each task is internally consistent with the design. Shared-ledger writes are sequential because the workflow permits only one implementer at a time.

## Workflow ruling

The ignored `progress.md` is the short-lived SDD recovery map; this force-added `ledger.md` is the durable repository audit record named by the plan. The only downside of a mistaken distinction would be duplicated status prose, not changed implementation behavior.

## Task status

- Task 1: COMPLETE - Domain and application revocation contract; 75 focused and 1,753 complete Command tests passed.
- Task 2: COMPLETE - Atomic PostgreSQL revocation writer; 125 focused and 3,815 complete Data tests passed.
- Task 3: TODO - HTTP route, integration, documentation and exhaustive verification.
- Task 4: TODO - Whole-branch review and exact-head delivery.

## Task 1 implementation evidence

- Base SHA: `327168a46910988f47dbe87c6c10c439e855f439`.
- Scope: the six named domain/application production files and two named Command test files, plus this ledger. No persistence, HTTP or DI implementation was added. Ignored probe, runner, logs, report and progress remain local evidence.
- Executable missing-contract RED completed before typed tests or production code: exactly two `Assert.NotNull` behavior failures resolving absent input/command types, zero passes, no compiler/fixture failures, exit 1.
- Typed RED completed before production code: ten `CS0246` diagnostics, all for absent revocation types, no unrelated diagnostics, exit 1; tests did not execute.
- Focused GREEN: 75 passed, zero failed/skipped, exit 0.
- Complete unfiltered Command GREEN: 1,753 passed, zero failed/skipped, exit 0.
- Fresh complete unfiltered Command verification immediately before commit: 1,753 passed, zero failed/skipped, exit 0. All GREEN logs contain no build warnings or errors; independent enumeration of `Passed` lines agrees with summary counts.

Exact host commands, run from `D:\Repositories\cerberus-api`:

```text
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-1-linux.sh cerberus-uc37-task1-contract-red focused
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-1-linux.sh cerberus-uc37-task1-typed-red focused
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-1-linux.sh cerberus-uc37-task1-focused-green focused
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-1-linux.sh cerberus-uc37-task1-command-green full
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-1-linux.sh cerberus-uc37-task1-final-green full
```

Each runner uses a fresh `--rm --network none` Docker container, a read-only source mount, a read-only existing Windows NuGet cache, and a source copy excluding `.git`, `.superpowers`, `bin` and `obj`. The probe is copied into that isolated Command test project only for the first RED command. It runs these exact inner commands (focused adds the shown filter; full omits it):

```text
dotnet restore tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --locked-mode --ignore-failed-sources -p:NuGetAudit=false -p:TreatWarningsAsErrors=true
dotnet test tests/Application/ArturRios.Cerberus.Command.Tests/ArturRios.Cerberus.Command.Tests.csproj --no-restore -c Release -p:TreatWarningsAsErrors=true --filter FullyQualifiedName~RevokeCollectionAccess --logger console;verbosity=normal
```

Runtime: cached `mcr.microsoft.com/dotnet/sdk:10.0` image ID `sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317`, SDK 10.0.401, .NET runtime 10.0.12. Existing `cerberus-uc32-verify` and `cerberus-uc33-native` images contain Python/Java but no .NET SDK. WSL access required approved sandbox escalation. Restore stayed locked and offline; NuGet vulnerability auditing was explicitly disabled because the container has no network. This task verifies the complete Command project, not the repository-wide database/HTTP/native suites deferred to later tasks.

Mutation check from `writing-good-tests.md` was completed mentally, not by an automated mutation engine:

| Realistic break | Tests that catch it |
|---|---|
| Wrong actor, path IDs, verifier, input revision or caller token | `GivenValidRevocation_WhenHandling_ThenForwardTrustedContextAndReturnExactThreeFields` uses independent literal values, including the SHA-256 verifier. |
| Wrong authority/header/hash branch or reordered validation | `GivenMissingAuthorityOrInvalidHandle_WhenHandling_ThenRejectBeforeValidation` uses a strict validator and rejecting external-store fixture. |
| Missing store invocation or missing output/message assignment | Valid revocation cases require captured request, successful three-field payload, advanced revision and exact success message. |
| Empty/default success or corrupt/contradictory store result | `GivenMalformedStoreSuccess_WhenHandling_Then503WithoutPayload` and `GivenStoreErrorWithData_WhenHandling_ThenFailClosed`. |
| Missing zero/empty/unsafe/unauthorized/malformed validation | Contract JSON rejection cases, revision boundary cases and `GivenInvalidPathOrRevision_WhenHandling_ThenRejectBeforeStore`. |
| Leaked internal error or wrong status | `GivenStoreError_WhenHandling_ThenExposeOnlyAllowlistedFailures` and authority/success cases exercise all eight statuses through real returned messages/errors. |
| Dropped cancellation | Both cancellation tests require the exact caller token; pre-cancelled malformed authority must throw before any output. |

SHA-256 of exact verification source/runner bytes (all hashes lowercase):

| File | SHA-256 |
|---|---|
| `task-1-linux.sh` (ignored) | `da08c1a68e4b0ca1faa010a30670c6076ce8eacf2f2e05ce7a6a4b6f58b2870a` |
| `task-1-contract-probe.cs` (ignored) | `be4622fb1dac41a7269634104d880640acee6b8c082e0c3dd2539bef9774e767` |
| `src/Domain/ArturRios.Cerberus.Domain/Collections/CollectionGrantRevocationContracts.cs` | `0d8b6db531d33b421d4973963116511bc5c0505716c5724ed91dc65839f56572` |
| `src/Application/ArturRios.Cerberus.Command/Collections/RevokeCollectionAccessCommand.cs` | `1d09d9bf970e7a9ca5cd985b3d0b9d8bfe0ec9aaf5837e1e8154894597bc9a34` |
| `src/Application/ArturRios.Cerberus.Command/Collections/RevokeCollectionAccessHandler.cs` | `8fc8d4478c1be4e879e9c430db3a99e32194ae50aa653538697ec66a67a86f99` |
| `src/Application/ArturRios.Cerberus.Command/Collections/RevokeCollectionAccessOutput.cs` | `4cf7f4e47849df0a3be7e325ac44eecd1c15c3738c142330c1c98d2e5615fe62` |
| `src/Application/ArturRios.Cerberus.Command/Collections/RevokeCollectionAccessValidator.cs` | `74373c4ca9793952dabbc53397ab1ed6a30c5ab0c393b8a98ddf6de145174eda` |
| `src/Application/ArturRios.Cerberus.Command/Collections/CollectionGrantRevocationMessages.cs` | `d463b9e94953750a576f0591c73e132609db4ae019a72de3eceb57a2028bbe97` |
| `tests/Application/ArturRios.Cerberus.Command.Tests/RevokeCollectionAccessContractTests.cs` | `3307cee6bc36c003fd5e73aa34dcafef83bad0a237422dc622aefe824272a4f2` |
| `tests/Application/ArturRios.Cerberus.Command.Tests/RevokeCollectionAccessHandlerTests.cs` | `ee306ae350c313ef96eb9d6ef0a282ecbe10b3bfa5abe5018a44c3773a44e23f` |

Ignored evidence logs in this ledger's directory:

| Log | SHA-256 |
|---|---|
| `cerberus-uc37-task1-contract-red.log` | `b10def0d197356dcd1a097342b0b06721c4292f956fd070a2496964b085d2712` |
| `cerberus-uc37-task1-typed-red.log` | `45dbe151310f7a973f1598913b918d4117fd657e3aeae2c38b7dc528c6f89ee4` |
| `cerberus-uc37-task1-focused-green.log` | `1d3c2357754a2c579097556869f5083a4d2214605af16e26142c5326d202eec0` |
| `cerberus-uc37-task1-command-green.log` | `6e70cd1872269ca2a8508a37c0ec88aa78b3af9413883b4f90d627287a7363aa` |
| `cerberus-uc37-task1-final-green.log` | `8484469be916721567b80ceda1ad923ddf5803631e4549b7a6cb03c392176ae9` |

Task review is dispatched by the controller after this implementer's handoff; no Task 2 work was begun.

## Task 1 review acceptance

- Reviewed exact range: `327168a46910988f47dbe87c6c10c439e855f439..81fef2fe7f0aff59d7d43d1f7d5dc6ff25b0e867`.
- Fresh reviewer result: zero Critical, zero Important and one accepted Minor finding; Task 1 is ready to proceed.
- Independent focused verification: 75 passed, zero failed/skipped, exit 0, using a disposable offline Docker container with Release, locked restore and warnings as errors.
- Evidence reconciliation: all 15 recorded source/runner/probe/log hashes matched; RED counts were 2 executable behavior failures and 10 typed `CS0246` diagnostics; retained GREEN counts were 75, 1,753 and 1,753 with no failures/skips or build warnings/errors.
- Accepted Minor: the success fixture in `RevokeCollectionAccessHandlerTests` captures the last store request/token but does not assert exactly one store invocation. The current handler invokes the state-changing store once and the reviewer found no production defect. This nonblocking mutation-strengthening opportunity is retained for later test maintenance rather than expanding Task 1 after acceptance.
- Declined as correctly out of Task 1: database authority/classification/locking/atomicity/races, HTTP/DI/transport and online revocation behavior, documentation/OpenAPI, repository-wide/native/audit gates, client key rotation/copied-material destruction/UC-48 delivery and independent release approval.

Task 1: complete - implementation commit `81fef2fe7f0aff59d7d43d1f7d5dc6ff25b0e867` accepted after fresh exact-range review; no blocking findings.

## Task 2 implementation evidence

- Base SHA: `ad5dd3878e95b4bf58ddfef7e4d1478917e769a2`.
- Scope: the new revocation store, its two named PostgreSQL test files, and this ledger. UC-35/UC-36, Task 1, HTTP, DI, public documentation and Task 3 remain untouched. No repository-local AGENTS.md was present.
- The TDD skill and `writing-good-tests.md` governed executable RED, independently derived assertions and the mutation checklist. The verification-before-completion skill governed fresh final checks.
- Mandatory missing-store RED: a real PostgreSQL fixture migrated and created read-only/read-write active grants; both tests failed solely at `Assert.NotNull` when resolving the absent store type. 2 failed, 0 passed; no compiler or database setup failure.
- Expanded missing-store REDs: 71 failed/0 passed and 87 failed/0 passed before production implementation. The final tests use the typed production interface; the initial reflection probe remains in ignored `task-2-red-tests.cs`.
- First GREEN: 87 passed. Stronger collateral-fault RED: 112 passed, 3 failed because session/profile/sibling-grant changes were not rejected. The production collateral snapshot guard then passed 115/115. The adjacent-store matrix passed 124/124, and adding both unrelated-inventory sizes produced 125/125.
- Final implementation verification: focused 125 passed, 0 failed/skipped, exit 0; full unfiltered Data **3,815** passed, 0 failed/skipped, exit 0. No build warnings/errors in final GREEN logs. Enumeration of individual `Passed` lines agrees with each summary.
- Owner reduction accepts arbitrary old wrapper bytes, including malformed/empty bytes, and an existing active recipient without a protection row. It verifies the exact active relationship, safe grant/account counters and current owner authority. Foreign administration denial still requires the full current native collection/grant/identity/pin binding.
- Initially inactive or hidden grants return `not_found`; loss of the captured active grant or a changed revision during an actual wait returns `revision_conflict`. Account lifecycle and invalid-session precedence remain nonrevealing.
- One guarded authorization CTE performs grant state/revision/wrapper/stamp mutation and the dependent recipient-generation update. No second authorization write or broader revision/session/profile mutation is used.
- Post-write checks compare the exact revoked row, historical access, new revision/stamp, empty wrapper, generation +1 and absence of an active pair. Stable authority/member/link JSON and separate relevant session/profile/sibling-grant JSON detect collateral changes. Whole-fixture test snapshots also preserve unrelated account inventory.

### Exact commands and environment

All commands ran from `D:\Repositories\cerberus-api` through the ignored runner:

```text
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-2-linux.sh <name> <mode>
```

| Exact name argument | Mode | Result |
|---|---|---|
| cerberus-uc37-task2-store-red | focused | 2 expected absent-store failures |
| cerberus-uc37-task2-matrix-red | focused | 71 expected absent-store failures |
| cerberus-uc37-task2-reduction-red | focused | Test harness compile failure: xUnit1012 null passed to nonnullable expected error |
| cerberus-uc37-task2-reduction-red-fixed | focused | 87 expected absent-store failures after nullable annotation repair |
| cerberus-uc37-task2-first-green | focused | 87 passed |
| cerberus-uc37-task2-race-stability-red | focused | Test harness compile failures: CS0246 ProtectionRevoke and CS1061 wrong protection-writer method |
| cerberus-uc37-task2-race-stability-red-fixed | focused | Test harness CS0246 remained; no tests executed |
| cerberus-uc37-task2-race-stability-red-compiled | focused | 112 passed, 3 real collateral-write failures |
| cerberus-uc37-task2-stability-green | focused | 115 passed |
| cerberus-uc37-task2-adjacent-races | focused | 124 passed |
| cerberus-uc37-task2-focused-green | focused | 125 passed |
| cerberus-uc37-task2-data-green | full | 3,815 passed before EOF-only cleanup |
| cerberus-uc37-task2-final-focused | focused | 125 passed before EOF-only cleanup |
| cerberus-uc37-task2-final-clean | focused | 125 passed on exact final source |

Inner commands:

```text
dotnet restore tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --locked-mode --ignore-failed-sources -p:NuGetAudit=false -p:TreatWarningsAsErrors=true
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --no-restore -c Release -p:TreatWarningsAsErrors=true --filter FullyQualifiedName~CollectionGrantRevocationStoreTests --logger console;verbosity=detailed
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --no-restore -c Release -p:TreatWarningsAsErrors=true --logger console;verbosity=normal
```

Earlier focused commands through `adjacent-races` used `console;verbosity=normal`; the last focused runs use detailed output to retain EXPLAIN JSON. Each disposable SDK container uses a read-only source mount, a source copy excluding .git/.superpowers/bin/obj, and the read-only existing NuGet cache. It has host networking and the Docker socket so Testcontainers can create and dispose actual PostgreSQL containers. Restore is locked and uses cached packages, with vulnerability auditing disabled for this task verification; no online vulnerability-audit claim is made.

Runtime: SDK image `mcr.microsoft.com/dotnet/sdk:10.0`, image ID `sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317`, SDK 10.0.401, .NET runtime 10.0.12. PostgreSQL image `postgres:18.6-alpine`, image ID `sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873`. Both final EXPLAIN cases independently query and print `server_version=18.6`. WSL Docker access used approved sandbox escalation.

### Real concurrency and rollback matrix

Every controlled waiter below is proven blocked by its actual backend PID via `holder=ANY(pg_blocking_pids(waiter))` before the holder is released. Cancellation deadlines bound waits; no deadlock occurred.

| Scenario | Acquisition orders and asserted outcome |
|---|---|
| Same-grant revoke | First revoke wins; captured waiter conflicts; grant revision 2, revoked/empty, recipient generation 2. |
| Reciprocal revoke | First revoke wins; reciprocal actor's old session is stale and returns vault_access_denied. Fresh unlock permits its separate revoke. Same canonical account pair prevents a reciprocal lock cycle. |
| UC-36 permission change | Both orders. Permission change first: revoke conflicts. Revoke first: UC-36 returns not_found for the now inactive grant. Exactly one grant mutation and one generation increment. Controller explicitly retained this intentional asymmetric hidden/conflict classification; UC-36 is unchanged. |
| UC-35 stale regrant | Both orders, stale expected revision 1. Revocation is the sole winner; regrant conflicts; grant revision 2/generation 2 remain intact. |
| UC-35 successful regrant overtakes captured revoke | Delayed revoke captures active revision 1; a separate revoke commits revision 2; real UC-35 regrant holds the pair while the delayed revoke blocks, then commits revision 3. Delayed revoke conflicts, preserving active revision 3/new wrapper and generation 2. |
| Recipient unlock/session issuance | Both orders. Unlock first: its issued session becomes stale when revoke commits. Revoke first: the prior unlock challenge conflicts; fresh unlock cannot see the revoked collection. |
| Recipient lifecycle | Both orders, real account-locking PostgreSQL writer. Lifecycle first hides recipient and prevents revocation. Revoke first commits one grant/generation transition before lifecycle changes. |
| Recipient protection | Both orders, real VaultProtectionChangeStore. Protection first changes captured authority and revoke conflicts; revoke first makes the protection request's old handle stale. |
| Owner profile association | Both orders, real ProfileAssociationStore. Removal first hides exact selected collection; revoke first succeeds and later association removal remains valid. |
| Recipient profile association | Both orders. Removal first changes captured authority and revoke conflicts; revoke first makes recipient association handle stale. Profile revision/link and grant/generation outcomes are exact. |
| Per-lock authority loss | Account, session, profile, collection and grant waits; lifecycle, protection pins, policy, selected link, trash, revoked state, revision and database-time expiry are rechecked. Expiry is tested at all five lock stages. |
| Canonical lower-account wait/cancellation | Holding the lower public UUID blocks the pair lock. NOWAIT proves no higher-account, owner-session, selected-profile, collection or grant lock was acquired early. Caller cancellation leaves the entire fixture unchanged. |

Four real PostgreSQL BEFORE UPDATE trigger cases cover raise/suppress on the grant revision side and on the recipient generation side. Every case returns persistence_unavailable, preserves the exact pre-call fixture, and succeeds after trigger removal. In particular, suppressed recipient UPDATE after the grant CAS rolls the grant back. Additional real post-write SQL fault probes mutate grant/generation/account/collection/membership/pins/session/profile/link/sibling state; all fail verification and roll back. Caller cancellation after authorization rolls back both sides; natural session expiry after authorization still commits, while expiry before authorization denies.

### Query-bound evidence

`GivenUnrelatedInventory_WhenRevoking_ThenCaptureOnlyExactTargetAndLockCanonicalPairBeforeSessionProfileCollectionGrant` executes the captured real authority query through `EXPLAIN (ANALYZE, FORMAT JSON)` with zero and 24 unrelated collection/record/folder/grant sets. In both cases, actual rows are exactly 1 for `CTE target`, `CTE grant_rows` and `CTE recipient`; the five observed locks are account pair, session, selected profile, collection and path grant. Success tests require exactly one authorization write and at most twelve reads. Plans are retained verbatim in the final-clean log.

| Final focused plan | SHA-256 of exact JSON |
|---|---|
| 0 unrelated sets | f4df6c7140358ee8abba53d75cc19a33023ae2d92b003a4930e6e0ae03803274 |
| 24 unrelated sets | 66ece00ee06c13735201ec7281ae04626040f4a15c60c0cbd3fd6da74ac46326 |

The production snapshots are bounded to the requested collection/path/recipient and their relevant members, ancestry, linked profiles, sessions and sibling grants. This is structural query evidence, not a production latency benchmark or a bound on the number of relevant members/sessions.

### Writing-good-tests mutation check

The skill's mental mutation check was completed (not an automated mutation-engine run).

| Realistic mutation | Independent behavioral detection |
|---|---|
| Wrong state/access/revision/generation argument | Both initial access cases assert literal revoked state, unchanged historical access, revision 2, generation 2, empty wrapper and exact trusted IDs; last-safe counters reach literal maximum. |
| Wrong authority branch or selected-scope widening | Wide/selected Master/PerProfile fixtures, foreign hidden/valid/corrupt cases and actual link/session/expiry waits assert exact outcomes and unchanged snapshots. |
| Missing generation increment, retained wrapper, partial commit | Success row checks, old/fresh recipient access checks, real trigger suppression/raise tests and post-write rollback fixtures. |
| Empty/default return | Main transition cases require nonnull details and exact three domain result fields. |
| Missing malformed/zero/unsafe validation | Foreign native binding mutations, unsafe stored counters, malformed owner wrapper reduction, pre-database request failures and stale/exhausted revisions. |
| Missing wait recheck, canonical pair order or cancellation | pg_blocking_pids race matrix, lower-account NOWAIT proof, cancellation and exact winner snapshots. |
| Missing collateral comparison | Session/profile/sibling tests were observed failing before the added collateral guard, then passing with full rollback. |

### Verification hashes

| File | Final SHA-256 | Full-run SHA-256 before EOF cleanup |
|---|---|---|
| `src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionGrantRevocationStore.cs` | `5ab8bcbf73a2ffecea8e5becb54694eb48f7ebc100a69b344b209e1ec42d1c0b` | `c62c7c92e70dad46f4c1b8a0e8432d6de27e4e06ce5e3c3c17704adb1b1152e0` |
| `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionGrantRevocationStoreTests.cs` | `94f0a0eae9a6e2a1631d60c1224973327671e093a036ef470fa525f2c2f3e160` | `2bd504d95df8156b5db9baebdb3ff3431725323f6330d874dad7fc2a370f5871` |
| `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionGrantRevocationStoreConcurrencyTests.cs` | `fb5608655f32229087a5c03ca30722a3c01765b40c2afc46e556ae42298037a8` | `37e0f881db09374fa9ad5f42f318513bbf021b0ef3e3f519e9a94474ea33d8c6` |

The staged whitespace check found two extra LF bytes at each new file's EOF. Only those bytes were removed after the full Data source copy; re-appending two LF bytes to each final file reproduces every full-run hash above exactly. The final-clean run reverified all 125 focused cases on the exact final bytes. Staged `git diff --check` then passed. Git's LF-to-CRLF working-tree advisory was harmless; final source hashes are of the LF files tested and staged.

| Ignored supporting file | SHA-256 |
|---|---|
| `task-2-linux.sh` | `8b969b4840873c170413954b677b40de01fd4e8377bc128e170b4d53c290cab8` |
| `task-2-red-tests.cs` (preserved initial probe, normalized terminal newline) | `01cd412253225c3cf4b001d4837a02d014b37270c1ab94b14de966709e755229` |

| Ignored evidence log | SHA-256 |
|---|---|
| `cerberus-uc37-task2-adjacent-races.log` | `23d70408355ae7dfff2f82ee166a9615fb123242467a6cd9fee750de663bbd9d` |
| `cerberus-uc37-task2-data-green.log` | `2c537729730581b2b798255a2372d5c8953ca0872ef36e0aff019af23c793ca7` |
| `cerberus-uc37-task2-final-clean.log` | `ba08de7d78bd65cfd2a8ac1d5a37778341efd2660491b505fa6cda49dae08815` |
| `cerberus-uc37-task2-final-focused.log` | `437c76899f21c756164cfa9f4625d70732220bfacd8612b0b73693323253a06d` |
| `cerberus-uc37-task2-first-green.log` | `a077991367af50dbf5d0d03c2e654ff8c1c9cfee518d910850089d7a1a43b31b` |
| `cerberus-uc37-task2-focused-green.log` | `0a58e5e58a97bfb10caacaf5ca8a9f3dca6ff54f2114917ac996bef3a836f503` |
| `cerberus-uc37-task2-matrix-red.log` | `f636cb7ec69efa926f3a112040dc0c341859ff670970677ea8f0573bac2d0bb0` |
| `cerberus-uc37-task2-race-stability-red-compiled.log` | `7bb12f054719ab24a98cb724c435a928dbc8da6ad3e58a1280fa875ad53fd06f` |
| `cerberus-uc37-task2-race-stability-red-fixed.log` | `e29dad18f261dc7227aefbf9f5d8769e2514e063b700089c0ccef1896a65ceb5` |
| `cerberus-uc37-task2-race-stability-red.log` | `d3533c385b3f2a01b884b375e3ab06e470c0ba91a32dbb9848f9e0d70ac91d38` |
| `cerberus-uc37-task2-reduction-red-fixed.log` | `23cba0bda34526475f7f391551a95c03216bbcfb00c309561e68ab37eb836a34` |
| `cerberus-uc37-task2-reduction-red.log` | `7982c015a4c80792ae4d0529e968a9504b883fa9c85fcc5607dd4125b80856d0` |
| `cerberus-uc37-task2-stability-green.log` | `9f63e29b6baf410459eaf45c33b9bf25e543e0e4a6f80ceb77b7688f0ecb2130` |
| `cerberus-uc37-task2-store-red.log` | `0b73c8a3df1f2266e4be7e1d29494938eda467b7ccc2c0c77d298b188ab73ca8` |

Task 2 implementation is complete. Fresh task review is the controller's next step; Task 3 has not begun. This task verifies the complete Data project, not the repository-wide HTTP/native/audit/coverage gates reserved for later tasks.
