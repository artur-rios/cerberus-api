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
- Task 2: COMPLETE - Corrected atomic PostgreSQL revocation writer accepted after fresh full-range re-review; 130 focused and 3,820 complete Data tests passed on PostgreSQL 18.6.
- Task 3: COMPLETE - strict HTTP revocation, documentation and exhaustive gates accepted after fresh exact-range review; 90 focused / 9,701 full tests passed.
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

## Task 2 blocking review correction evidence

Status: **CORRECTED - REVIEW PENDING**. Fresh controller re-review is required before Task 3. Earlier implementation evidence above is preserved.

Correction base: `11d3d4a4c0f9a929c169800f68ec2f0d9cb62f02`; branch `feature/uc-37-revoke-collection-access`. Correction head is the commit containing this audit entry; its exact SHA is retained in ignored `task-2-correction-report.md` after commit.

- Verified finding: UC-37 blanket-mapped PostgreSQL SQLSTATE `23505` and `2200H` to `revision_conflict`. Revocation changes no unique key and consumes no sequence. Legitimate stale/exhausted conflicts are already explicit, so trigger/storage faults with those states must return `persistence_unavailable`.
- Tests first: add four real PostgreSQL trigger cases for both SQLSTATEs on both grant-revision and recipient-generation atomic update sides. Add recipient profile/link, collection members and another collection/grant as collateral. Every case requires null failure data, exact full fixture rollback, exact snapshot preservation after trigger removal and successful retry to revision 2/generation 2 with revoked state, cleared wrapper, fresh stamp and unchanged collateral.
- Update the artificial exception theory to require `persistence_unavailable` for `23505` and add `2200H` with that same expected safe error.
- Meaningful RED on unchanged baseline production: 130 total, 124 passed, 6 failed, 0 skipped, exit 1. Exactly the four trigger cases and the two artificial mapping cases failed on expected `persistence_unavailable` versus actual `revision_conflict`. No compilation/fixture failure or build warning/error. Production SHA-256 at RED remained `5ab8bcbf73a2ffecea8e5becb54694eb48f7ebc100a69b344b209e1ec42d1c0b`.
- Minimal production fix after observed RED: remove only UC-37's one blanket `PostgresException` conflict catch so both SQLSTATEs use its existing persistence handler. Preserve every explicit legitimate conflict check, authorization statement, rollback guard and adjacent behavior.
- Fresh focused GREEN: 130 passed, 0 failed/skipped, exit 0, on exact final production/test bytes. All four real trigger cases and both artificial cases passed, together with existing stale/max/captured-wait conflict and concurrency cases.
- Fresh complete unfiltered Data GREEN: 3,820 passed, 0 failed/skipped, exit 0, Release, locked restore, warnings as errors. Individual `Passed` line counts agree with both GREEN summaries, and neither GREEN log contains build warnings/errors.
- Scope: exactly the revocation production file, its main test file and this ledger. UC-35/UC-36, Task 1, the concurrency file, schema, HTTP, DI and public documentation are unchanged. No Task 3 work or subagent dispatch. No applicable AGENTS.md was found.

### Exact correction commands and runtime

From `D:\Repositories\cerberus-api`:

```text
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-2-linux.sh cerberus-uc37-task2-correction-red focused
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-2-linux.sh cerberus-uc37-task2-correction-focused-green focused
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-2-linux.sh cerberus-uc37-task2-correction-data-green full
wsl.exe -d Ubuntu -- docker image inspect mcr.microsoft.com/dotnet/sdk:10.0 postgres:18.6-alpine --format '{{.RepoTags}} {{.Id}}'
```

Unchanged runner inner commands:

```text
dotnet restore tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --locked-mode --ignore-failed-sources -p:NuGetAudit=false -p:TreatWarningsAsErrors=true
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --no-restore -c Release -p:TreatWarningsAsErrors=true --filter FullyQualifiedName~CollectionGrantRevocationStoreTests --logger "console;verbosity=detailed"
dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --no-restore -c Release -p:TreatWarningsAsErrors=true --logger "console;verbosity=normal"
```

The first host RED attempt under default sandbox permissions failed before harness/test execution with WSL `E_ACCESSDENIED`, exit 1, and created no runner log. The identical command with approved escalation executed the meaningful RED. There were no correction compile or PostgreSQL fixture failures. PowerShell's startup `InitializeDefaultDrives` message and Git's future LF-to-CRLF advisory are host messages, not test build diagnostics.

The runner uses fresh disposable SDK containers, read-only source/cache mounts and isolated source copies excluding `.git`, `.superpowers`, `bin` and `obj`; host networking/Docker socket support actual Testcontainers PostgreSQL. NuGet audit is disabled, so no online vulnerability audit is claimed.

Fresh image inspection confirms SDK image ID `sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317` (`mcr.microsoft.com/dotnet/sdk:10.0`) and PostgreSQL image ID `sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873` (`postgres:18.6-alpine`). The focused tests query actual PostgreSQL version `18.6` twice. Fresh logs confirm .NET runtime 10.0.12; the unchanged image's SDK 10.0.401 is recorded in earlier evidence.

### Correction SHA-256 hashes

| File | SHA-256 |
|---|---|
| Corrected `CollectionGrantRevocationStore.cs` | `7cd73f4b9d2fb20d5a88e31c3a4c375fca3a6658932f43cbb2ea7a0341b90994` |
| `CollectionGrantRevocationStoreTests.cs` at RED and both GREEN runs | `3f4fee61658b43f2ef656d439e9735cd4388edebb8087532284d4c635af40ba1` |
| Unchanged `CollectionGrantRevocationStoreConcurrencyTests.cs` | `fb5608655f32229087a5c03ca30722a3c01765b40c2afc46e556ae42298037a8` |
| Unchanged `task-2-linux.sh` | `8b969b4840873c170413954b677b40de01fd4e8377bc128e170b4d53c290cab8` |
| `cerberus-uc37-task2-correction-red.log` | `bd4ddcb24b40823c1046b978dad07e63141f3253961eb40c3dd96d1b2a081ea1` |
| `cerberus-uc37-task2-correction-focused-green.log` | `e65a9dd069f6b4a8b3c9f8b8674d217acedb889d842ca37329aca886d9d94977` |
| `cerberus-uc37-task2-correction-data-green.log` | `6e02135b73b294478d25972d14f0fec57cb3bca297d2166bd7784d758da5861c` |

Final focused EXPLAIN JSON hashes: zero unrelated sets `8e6f1abb1959db8d734db9cddb1810c3a1c3f552554b21c56e83d446f4de8c2f`; 24 unrelated sets `fe87c4bf4e52afda69891264d93ba2b4d46f8480348e648ee105dd928a9ce8ce`. These plans are retained in the focused GREEN log; their passing structural row-bound assertions are unchanged.

The TDD, writing-good-tests and verification-before-completion skills governed the correction. Mental mutation check (not an automated mutation engine): restoring either blanket classification fails real trigger and artificial cases; partial rollback fails exact fixture snapshots; missing successful grant/generation mutation fails literal retry assertions; collateral changes fail stable snapshots. Existing legitimate conflict tests remain GREEN. Verification covers the full Data project, not HTTP/native/crypto/security/release qualification; all earlier scope limitations remain in force. Ignored report/progress/logs are excluded from the correction commit.

## Task 2 corrected review acceptance

- Re-reviewed exact full Task 2 range: `ad5dd3878e95b4bf58ddfef7e4d1478917e769a2..1c9921a9a768e31f99b37aece48519846134743f`.
- Fresh reviewer result: zero Critical, zero Important and zero Minor findings requiring action; Task 2 is ready to proceed.
- The prior blocking SQLSTATE finding is resolved: UC-37 has no blanket conflict mapping, explicit legitimate conflicts remain intact, and both `23505` and `2200H` on both atomic update sides return `persistence_unavailable`, roll back exactly and permit a clean retry.
- Independent exact-source focused verification: 130 passed, zero failed/skipped, exit 0, PostgreSQL 18.6, with no build diagnostics.
- Evidence reconciliation: correction RED was 124 passed / 6 intended failures; corrected focused was 130 passed; full Data was 3,820 passed. All current source/runner/log/ledger hashes and both retained EXPLAIN JSON hashes matched; structural plans retained one target, path-grant and recipient row with 0 and 24 unrelated sets.
- Complete re-review reconfirmed malformed owner-wrapper reduction, foreign native `403`/hidden `404`/corrupt `503`, canonical lock order and rechecks, guarded atomic mutation, trigger rollback, exact post-write scope, real blocker evidence and the intentional adjacent UC-36 hidden / UC-37 conflict asymmetry.
- Correctly deferred: HTTP/DI/online authorization integration, public documentation/OpenAPI, repository-wide/native/audit/CI gates, performance benchmarks, arbitrary out-of-graph triggers, unchanged adjacent SQLSTATE mappings, client key rotation/copied-material destruction/UC-48 and independent release approval.

Task 2: complete - implementation `11d3d4a4c0f9a929c169800f68ec2f0d9cb62f02` plus correction `1c9921a9a768e31f99b37aece48519846134743f` accepted after fresh full-range re-review; no remaining findings.

## Task 3 implementation and verification (2026-10-12)

- Base: `eaf52c863a15fc8efdf31189e069b8f2571eef3f`; branch `feature/uc-37-revoke-collection-access`.
- Task 3 is COMPLETE pending fresh review. Task 4 remains TODO under controller ownership. No push, PR, issue/project update, merge or delivery operation occurred.
- Scope is exactly the DELETE action, three scoped DI registrations, the new HTTP integration suite, generated OpenAPI, collection security/foundation documentation, CHANGELOG, README and this ledger. Reviewed Tasks 1-2 and unrelated files are unchanged. No repository-local AGENTS.md was found.
- The TDD skill and writing-good-tests reference governed missing-route RED and independent behavior assertions. Systematic debugging identified fixture assumptions and native harness line-ending failures. Verification-before-completion governed the final source/evidence reconciliation and whitespace checks.

### RED and focused GREEN

| Run name (cerberus-uc37-task3- prefix) | Passed | Failed | Result |
| --- | ---: | ---: | --- |
| http-red | 5 | 83 | Expected absent-route RED, exit 1 |
| http-red-final | 5 | 84 | Final preimplementation absent-route RED, exit 1 |
| http-green-first | 83 | 6 | Test-fixture assumptions exposed, exit 1 |
| http-green | 89 | 0 | Corrected integration GREEN, exit 0 |
| http-final | 90 | 0 | Exact final source, including database-wait cancellation, exit 0 |

All runs had zero skips. Every final RED failure was the absent DELETE route returning 405 (83 enum MethodNotAllowed assertions and one raw status assertion). The five RED passes were existing shared middleware: the oversized-body empty 413 and four rejected-identity cases. None is counted as new failing behavior. Native fixture signing, initial POST grants, compilation and actual PostgreSQL startup all succeeded before the route/DI implementation.

The final RED test source was captured before production edits as task-3-red-tests.cs. The initial RED source is retained as task-3-initial-red-tests.cs, reconstructed from the original creation content; it predates corrected existing move-route probes and the additional maximum input revision rejection. The 89-test GREEN source is retained as task-3-green-89-tests.cs. The current final test source has the final 90-case suite.

The first GREEN's six failures were five selected create probes with empty profile IDs (existing create authorization correctly returned 403 before evaluating a hidden folder) and one corrupt historical-access owner-reduction probe that incorrectly expected 503. Selected probes now supply their exact profile; corrupt access is tested through the relevant visible foreign native-validation path. The reviewed revocation store deliberately preserves historical access while reducing authority. No production correction was needed. The final added cancellation test proves a real request is blocked by an actual PostgreSQL account-lock holder via pg_blocking_pids, then proves caller cancellation removes the database waiter before the holder releases and leaves exact snapshots unchanged.

The HTTP suite verifies both access values; wide/Master/PerProfile owner and recipient handles; pre-revocation lists, details and read-only/read-write member edits; exact three-field success and secrecy; exact revoked row, empty wrapper, changed stamp and generation +1; stale old handles including unrelated data; real fresh account/profile challenge/proof access; absent direct/inherited collection members; denied record/folder create, update and move paths; unchanged unrelated recipient-owned data; retained inert ProfileCollection links; and UC35 regrant at revision 2 using a fresh native revision-3 envelope restoring only the intended linked profile. Owner malformed/empty wrapper and unprotected-recipient reductions succeed. Foreign corruption, hidden targets, lifecycle/session errors, stale/exhausted counters, both atomic trigger faults, missing-table database outage, rollback and clean retry are pinned. Strict canonical path IDs, no query, exactly one header, one-field JSON, duplicate/unknown/case-forged/null/string/unsafe fields, media type/charset/UTF-8/BOM/compression/body limits, no-store and empty 413 are exercised.

Exact focused host invocation (each name above, with focused):
```text
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-3-focused.sh <name> focused
```

Inner commands:
```text
dotnet restore tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ArturRios.Cerberus.WebApi.Tests.csproj --locked-mode --ignore-failed-sources -p:NuGetAudit=false -p:TreatWarningsAsErrors=true
dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ArturRios.Cerberus.WebApi.Tests.csproj --no-restore -c Release -p:TreatWarningsAsErrors=true --filter FullyQualifiedName~CollectionGrantRevocationHttpTests --logger console;verbosity=normal
```

These focused disposable SDK containers use a read-only source mount, isolated source copy and read-only package cache. Auditing was explicitly disabled only for focused cached-package runs; live audit belongs to the full gate below. Initial sandboxed WSL access returned E_ACCESSDENIED before any tests; approved escalation enabled the actual runs.

### Full six-family Release verification

Authoritative run `cerberus-uc37-task3-full` exited 0 with **9,701 passed, zero failed/skipped**. Fresh TRX parsing independently requires total=passed, zero failed and zero notExecuted in exactly six reports.

| Family | Passed |
| --- | ---: |
| Command | 1,753 |
| Query | 606 |
| Shared | 125 |
| Domain | 361 |
| Data | 3,820 |
| WebApi | 3,036 |

Data took 9m17s and WebApi 4m54s. Locked solution and generator restore, Release build with warnings-as-errors (0 warnings/errors), live NuGet direct/transitive audit (zero known findings), 23 repository helper tests, specification validation and generated OpenAPI drift/shape gates passed. Specs validated 13 Markdown files, 7 template structures, links/anchors, 107 FRs/55 UCs, 12 features, 29 BRs, 6 milestones and 56 backlog rows. The independent release gate returned exactly its expected exit 1: "Protocol security and client review is pending." The development runner validates that pending-review result and ends 0; no release-approval claim is made.

ReportGenerator 5.5.11 merged exactly six fresh Cobertura collectors at 2026-10-12T16:05:07Z. Aggregate line coverage is **98.5% (15,829/16,066)** and branch coverage **92.1% (7,106/7,712)**. The exact six-production-assembly inventory and every individual 90% line floor passed; test/foreign assemblies are excluded.

| Production assembly | Covered / coverable lines | Line coverage |
| --- | ---: | ---: |
| Command | 1,298 / 1,298 | 100% |
| Query | 580 / 580 | 100% |
| Shared | 311 / 321 | 96.8% |
| Domain | 519 / 522 | 99.4% |
| Data | 12,351 / 12,530 | 98.5% |
| WebApi | 770 / 815 | 94.4% |

Exact full invocation and key inner commands:
```text
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-3-full.sh cerberus-uc37-task3-full
dotnet restore src/ArturRios.Cerberus.sln --locked-mode -p:RestoreAdditionalProjectFallbackFolders=/nuget -p:TreatWarningsAsErrors=true
dotnet restore tools/OpenApiGen/OpenApiGen.csproj --locked-mode -p:RestoreAdditionalProjectFallbackFolders=/nuget -p:TreatWarningsAsErrors=true
dotnet tool install --tool-path /opt/uc37-report dotnet-reportgenerator-globaltool --version 5.5.11
python3 scripts/vulnerabilities.py
dotnet build src/ArturRios.Cerberus.sln --configuration Release --no-restore -p:TreatWarningsAsErrors=true
python3 -m unittest discover -s scripts -p test_*.py -v
python3 scripts/verify_specs.py
python3 scripts/openapi.py
python3 /evidence/task-3-check.py shape
dotnet test src/ArturRios.Cerberus.sln --configuration Release --no-build --logger trx --collect:XPlat Code Coverage
python3 scripts/coverage.py --report-only
python3 /evidence/task-3-check.py results
python3 /evidence/task-3-check.py reconcile
```

The runner preflights ReportGenerator before tests, archives all six TRX/Cobertura reports immediately after tests and retains the stopped container's exact /work snapshot. Runtime: SDK 10.0.401, .NET/ASP.NET 10.0.12, Linux x64, actual PostgreSQL 18.6-alpine.

Image identities:
- SDK: sha256:e70cdb7f80b0348f5cb85f19a8f670fca061f033d57eed12fa003d58b0e06317.
- PostgreSQL: sha256:77f585114c32fbca283dc835b0596f4e52b51b4c6662d7810b2f4084f60a1873.
- Full runtime cerberus-uc32-verify: sha256:e856488e0619b98ffc029b686b5fa3f7c26e50e23eb67e52dc2859a749aa4f3a.
- Native runtime cerberus-uc33-native: sha256:3a7bd4e59f6f755cb9bcf90c392f6764823c2ad0c4fdfc07d770cb9803ef480b.

### Native/corpus/audit verification and truthful failed attempts

Final native3 run exited 0. Java **173/173**, Python **142/142**, native helpers **36/36**, repository helpers **23/23**, and **322 corpus cases in all four directions** passed without failures/errors/skips. The current verify.py module performed the replay against the committed corpus and retained the complete result, native suites, known-answer self-tests and provenance. Live OSV at 2026-10-12T15:57:59.460822+00:00 queried **153 package coordinates**, zero findings. Dependencies were freshly hash-locked downloaded/installed; runtime is Python 3.14.8, Temurin 25 and Maven 3.10.0. OSV is a version-based snapshot and does not audit runtime/OS correctness.

Exact final host command:
```text
wsl.exe -d Ubuntu -- bash /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-37-revoke-collection-access/task-3-native.sh
```

The retained task-3-native-inner.sh records fresh venv/wheel/Maven ZIP setup, clean Java tests/dependency tree, live audit.py, current verify.py corpus execution, native/repository helper suites and source reconciliation.

Failed attempts remain separately retained:
1. native.log exited 127 because the copied Windows CRLF Maven wrapper's shebang was not executable on Linux. No native test pass is attributed to this attempt.
2. native2.log completed Java tests and live OSV but verify.py returned invalid_protocol. Native diagnostic evidence independently proved Java 173, Python 142 and both six-known-answer self-test sets passed; the rejection was corpus provenance byte hashes, not cryptographic behavior.
3. Diagnostic comparison proved every differing provenance hash matched after CRLF-to-LF conversion alone. Final native3 normalizes exactly the 44 corpus-provenance inputs plus the Maven wrapper in the isolated copy, verifies each expected corpus digest before execution, and records all **45 raw-source/executed SHA-256 pairs**. Repository files and corpus content remain unchanged. Final reconciliation compares raw source exactly and permits only the explicitly proven LF conversion in these executed copies.
4. The evidence-checker's corpus helper was corrected before its first successful invocation to call verify(check=...) and serialize the returned result; supplying both output and check is forbidden by that API. The earlier check failed before reaching this helper.
5. The full-result parser's project key was preflight-corrected before results collection so all six families remain distinct. No full test failure or lost coverage report occurred.

### OpenAPI, docs and closeout

The repository generator ran successfully in isolated mode (`task-3-full.sh cerberus-uc37-openapi generate`). Only the intended DELETE operation and RevokeCollectionAccessCommand, RevokeCollectionAccessOutput and its DataOutput wrapper schemas were added: 273 generated lines. The shape gate removes exactly those additions and requires semantic equality with the entire baseline document; it also pins the eight response statuses, exact path/header parameters, one required nonnullable revision input and exact three output properties.

Collection security docs, foundation status, CHANGELOG and README describe wrapper erasure, account-wide online invalidation, retained inert links and explicit revision-bound regrant. Independent PowerShell and Python tracker counts both find exactly **55 business rows: 38 Done / 17 Todo**, with UC37 Done in feature content only (M04 now 3/6). No external tracker was changed.

The raw full/native source manifests both bind **662** tested source/test/helper/tool/contract files. They match each other, the final worktree and the final 90-test HTTP file exactly. The eight deliverable hashes are separately retained. All **38** evidence/runner/snapshot hashes are in task-3-evidence-manifest.json (excluding the manifest itself, mutable report/progress and this self-referential ledger), SHA-256 **ef7aa2883927d6f423f9e98622829a37b2ae251564d4b0a0656c6f2967fe2b9f**. Fresh worktree and base-range git diff --check passed. Ignored scripts, snapshots, logs, reports and evidence are excluded from the commit.

A mental mutation check maps missing/wrong route, context, status map, strict transport or output to the HTTP matrix; dropped cancellation to the observed database-wait cancellation test; missing wrapper erasure or generation change to exact row/stale-handle checks; unauthorized or collateral writes to unchanged snapshots; removed profile links or widened regrant scope to exact retained-link and isolated-profile assertions.

Limitations remain explicit: copied roots, ciphertext and plaintext cannot be recalled; this endpoint does not rotate/re-encrypt client keys; UC48 durable offline delivery is not implemented here; shared generated metadata cannot express all runtime transport/output requirements; authenticated recipient-key discovery/external decryptability and independent protocol/security/client approval remain release obligations. No unresolved Task 3 production finding is known; fresh Task 3 review remains required.

### Task 3 exact deliverable SHA-256

| File | SHA-256 |
| --- | --- |
| `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/CollectionsController.cs` | `0d466c3effc9973af4985586f51a520420e20056a62ac050b72b41c290642846` |
| `src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs` | `e0f0627d3490523e7a2900789370935ac560d3c967b0403ff5ce02c34112d6f3` |
| `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionGrantRevocationHttpTests.cs` | `3945b74e4771a892b165c72f8eeb3332aa5a3779b08ad0701a70c319f83fec61` |
| `docs/contracts/openapi.json` | `bf62e4251a62fa6a9d9e0e99763cb33799059e230dd31a21a29080045f4d87be` |
| `docs/security/collection-api.md` | `ce7ae7ae202daf5a5309093cd63336d73e7b3976e60f4f53bd1c4140ca585537` |
| `docs/operations/foundation-status.md` | `9d8c3a089299ad987b4cbca7da58211aae8fec2d75fc01225daa0a084268075a` |
| `CHANGELOG.md` | `57c80cf2921fa7935b9d9950157a80876ffe718baf213f055644da545636c8f5` |
| `README.md` | `3df80fae920a1ca35105216dc3a8318399e31b8763f2fc64273c643eb26122fd` |

### Task 3 primary evidence SHA-256

| File | SHA-256 |
| --- | --- |
| `cerberus-uc37-task3-http-red-final.log` | `c861b172b04564403bdc9b0fdfe65410ddb504d6883fe681e1aa1441e5b65077` |
| `cerberus-uc37-task3-http-final.log` | `bcc0c3bc01daa32eb06ef27f6dfee1b65738ae7a72d3308437f8138adc021f27` |
| `cerberus-uc37-task3-full.log` | `3abff0641caaa1b354a118bc60bf0ef969fc131a5965b89d644c9a128bb25148` |
| `cerberus-uc37-task3-full-evidence.json` | `9444fcbc14cdac905694562fa466e13f57b772845fb71d31afc58f30277f8752` |
| `cerberus-uc37-task3-full-coverage-summary.json` | `674d4c9100acbb5de877a724d2aad680dae3093b59f237cf4e2285038f3fcf02` |
| `cerberus-uc37-task3-full-source-sha.json` | `a06f6bd818490a1ee00c8f9d7f61bc0dee99e33c96ce127e216f3de2e6d36d83` |
| `cerberus-uc37-task3-full-test-results.tar.gz` | `873184c2979d94d6aeb2c4676158dd54860d8ac6334cace3fc05e21e7d8658f5` |
| `native3.log` | `f4be39d1a4ca49c18ac16da2af093b8b3651fa7f2d1cde6aa83b37ea40d82090` |
| `native3-source-sha.json` | `a06f6bd818490a1ee00c8f9d7f61bc0dee99e33c96ce127e216f3de2e6d36d83` |
| `native-summary.json` | `665c074183a92c3e48ccc31205d8b9c3a7c1f3cb4e1c40a18ea47c8b14c4ffd7` |
| `native-corpus-result.json` | `89e62bc5eaab08c9796aec812fbcc55bfa15cd18db141b29345a071c783245c3` |
| `native-audit.json` | `72e7cc61663a5a4f549438ae23395c33456041c62cb9fc0f1faeb3a90dface96` |
| `native-runtime-normalization.json` | `4441597718aca6b8a0a23b3603d98c9619c127f05af956e891ecf957afae763c` |
| `task-3-check.py` | `d9d0e7c10a2e85d1b8aa2bdbb9c38bef600508fc9bce5f5e442d1c279f389f5e` |

## Task 3 review acceptance

- Reviewed exact range: `eaf52c863a15fc8efdf31189e069b8f2571eef3f..6ef9fa149505356f6f37ed7a799158311587effc`.
- Fresh reviewer result: zero Critical, zero Important and zero actionable Minor findings; Task 3 is ready for whole-branch review.
- Independent exact-source HTTP verification: 90 passed, zero failed/skipped, exit 0, against PostgreSQL 18.6 in 27.3 seconds.
- Retained full evidence independently reconciled to Command 1,753; Query 606; Shared 125; Domain 361; Data 3,820; WebApi 3,036; total 9,701 passed with zero failed/skipped.
- Raw coverage independently reconciled to 15,829/16,066 lines (98.5%) and 7,106/7,712 branches (92.1%); each production assembly remains above the 90% line floor.
- All eight deliverable hashes, both 662-file source manifests and all 38 evidence hashes matched. Native normalization/provenance pairs reconciled; retained Java 173, Python 142, helpers 36, repository helpers 23, corpus 322 in all four directions, NuGet zero known findings and live OSV 153/0 evidence are consistent.
- The reviewer reconfirmed strict transport, exact status/output secrecy, malformed-wrapper reduction, foreign classification, stale/fresh online behavior, direct/inherited denial, unrelated recipient data, inert links, revision-bound regrant, narrow generated contract and accurate 38 Done / 17 Todo documentation.
- Correctly deferred: actual client rotation/copied-material destruction/UC-48, external-client decryptability and independent protocol/security approval, production load/deployment/OS qualification, and Task 4 whole-branch/exact-head delivery.

Task 3: complete - implementation commit `6ef9fa149505356f6f37ed7a799158311587effc` accepted after fresh exact-range review; no blocking findings.
