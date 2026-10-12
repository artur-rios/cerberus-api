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
- Task 2: TODO - Atomic PostgreSQL revocation writer.
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
