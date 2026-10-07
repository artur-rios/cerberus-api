# Cerberus Backlog Implementation Plan

> **For agentic workers:** Use `superpowers:executing-plans` for inline execution after review. Read the specifications fresh for each issue and prepare its detailed test-first plan before implementation.

**Goal:** Deliver the foundation and all 55 specified use cases, covering every main and alternative flow.

**Architecture:** Follow the approved Domain, Command, Query, Shared, Data and WebApi layers. PostgreSQL stores ciphertext and structural metadata; Heimdall supplies identity. Clients retain vault encryption keys. Each issue has its own branch and pull request into `develop`.

**Tech Stack:** Resolve and pin mutually compatible stable versions under the Technology Stack Document. Use the approved first-party libraries, EF Core/PostgreSQL, FluentValidation, Serilog, xUnit, Moq, Bogus and PostgreSQL Testcontainers.

**Spec:** [System Requirements](../../requirements/System%20Requirements%20Document.md), [Operations & Infrastructure](../../requirements/Operations%20%26%20Infrastructure%20Document.md), [Use Case Specification](../../requirements/Use%20Case%20Specification%20Document.md), [Technology Stack](../../requirements/Technology%20Stack%20Document.md) and [Testing Specification](../../requirements/Testing%20Specification%20Document.md).

**Status:** Batch authorized by the user's “Yes, go ahead”. Foundation tasks are implemented, tested and reviewed in PR #57; required CI gates its merge. All 55 business use cases remain Todo. Beta setting design inputs are approved; independent protocol review is still pending and blocks dependent implementation.

## Global Constraints

- One issue, one branch and one pull request per unit of work.
- Foundation branch: `feature/project-foundation`; use-case branches: `feature/uc-##-use-case-name`.
- Base and pull-request target: `develop`.
- Status lifecycle: `Todo → In Progress → Testing → Done`.
- Public identifiers are GUIDs; internal database IDs never cross the API boundary.
- PostgreSQL is the sole persistence engine; Heimdall is the sole external runtime application service.
- Persist only opaque encrypted content and wrapped keys; never decrypt vault content on the server.
- Required merged line coverage is at least 90%; report branch coverage.
- Run the complete application suite for each issue, plus applicable contract, formatting and infrastructure checks.
- Require successful `branch-policy`, `test` and `docker` CI before merging; never bypass rulesets.
- Encryption/recovery implementation requires the approved security and client-interoperability protocol review under NFR-11.
- Production configuration requires explicit operator retention values, limits and protected ledger storage.
- Production deployment is a separately authorized action.

## Review Focus

1. A restored database must not resurrect terminally deleted identifiers: replay an external durable erasure ledger while traffic remains disabled.
2. An unavailable or incompatible Heimdall response must not grant access: reject unknown identity/scope/challenge responses and fail closed.
3. Failed production configuration must not expose credentials through diagnostics: reject invalid settings using stable names and redacted messages.
4. A stopped or crashed retention worker must not extend a restoration deadline: request-time enforcement remains authoritative.
5. Duplicate or concurrent operations must preserve the winning state: test actual PostgreSQL constraints and transactions.

## Evidence gathered on 2026-10-07

- All issues #1 through #56 are open; there are no existing application projects or open pull requests.
- `develop` matches `origin/develop` at `f264bb637a2637717979ec00b9d765af21634c84`; the initial working tree was clean.
- GitHub authentication has repository administration permission. The active `develop` ruleset requires the three CI checks above and zero approving reviews.
- Installed .NET SDK: 10.0.112. Docker daemon is available: 29.8.2. Docker access required sandbox escalation.
- `python3 scripts/verify_specs.py` passed: 107 functional requirements covered by 55 use cases, workflow parity, six milestones and 56 backlog entries.
- `python3 -m unittest discover -s scripts -p 'test_*.py' --verbose` passed all nine tests.
- The local Heimdall reference uses the approved layered layout, `WebApiStartup`, mediator dispatch and PostgreSQL provider. Inspected identity routes include `/api/auth/login`, `/api/auth/2fa/verify` and scoped person creation. Cerberus must inspect complete request/response and authorization contracts before implementing adapters.
- No application build, functional suite or application coverage result exists yet.

## Execution order

Follow the documented milestone dependencies, then specification order within each milestone. Issue #1 explicitly blocks every use case.

| Milestone | Issues in execution order | Use cases |
| --- | --- | --- |
| M-01 Foundation | #1 | IR-01 through IR-14 |
| M-02 Account identity and vault protection | #2–#6, #39–#42 | UC-01–UC-05, UC-38–UC-41 |
| M-03 Organized vault | #10–#35 | UC-09–UC-34 |
| M-04 Controlled sharing and software secrets | #36–#38, #43–#45 | UC-35–UC-37, UC-42–UC-44 |
| M-05 Offline synchronization | #46–#50 | UC-45–UC-49 |
| M-06 Recoverable deletion and beta operations | #7–#9, #51–#56 | UC-06–UC-08, UC-50–UC-55 |

## Decisions and authorization required

- Received authorization covering design/implementation stage transitions, pushing and opening pull requests, merging verified pull requests into `develop`, closing issues, updating project/README status and deleting merged feature branches for issues #1–#56.
- The cryptographic suite and binary contracts remain deliberately unselected. Before encrypted-contract implementation, prepare a protocol design covering algorithms, KDF parameters, nonce uniqueness, authenticated metadata, recipient public-key verification, proof domain separation, key rotation and client test vectors; obtain the required review. An automated test run does not constitute independent security or client review.
- Operations §3 requires operator-chosen backup, log and sync retention, worker interval, storage paths and request/page limits before foundation approval. Obtain those choices or explicit authority to propose them for review. Keep production secrets out of the plan and source control.
- The foundation may supply the review gate while its record is pending (IR-09); do not begin encryption/recovery implementation until actual review evidence is approved (NFR-11). Do not mark the batch complete while those decisions remain unresolved.

## Foundation tasks — issue #1

### Task 1: Establish the solution and reproducible dependency pins — IR-01, IR-02, IR-07

**Files:** `src/ArturRios.Cerberus.sln`; six production project directories from Operations §2.3; six mirrored test project directories; `Directory.Build.props`; `Directory.Packages.props`; `global.json`; `.config/dotnet-tools.json`.

- [x] Resolve stable versions against package manifests and primary release documentation; inspect family library APIs and compatibility before choosing pins.
- [x] Create the approved project graph, nullable/implicit-usings settings and test categories. Keep public package references explicit where their types are used.
- [x] Verify restore/build and test discovery; introduce behavior tests with the tasks below rather than getter/setter or empty placeholder tests.

### Task 2: Validate configuration and safe HTTP/logging behavior — IR-03, IR-06, IR-14

**Files:** `src/Presentation/ArturRios.Cerberus.WebApi/Configuration/CerberusOptions.cs`; `Configuration/CerberusOptionsValidator.cs`; `Startup.cs`; `Program.cs`; `Middleware/NoStoreMiddleware.cs`; nonsecret example settings; mirrored WebApi tests.

**Interface:** `CerberusOptionsValidator.Validate(string? name, CerberusOptions options)` returns the configuration validation result; bound options carry required operator values without production defaults.

- [x] Write tests for missing/invalid retention, unsupported provider, nonpositive or overflowing durations, missing ledger location, invalid limits and environment precedence. Assert that failures reveal setting names and never values.
- [x] Confirm the tests fail for the missing validation behavior, then implement typed configuration with environment secrets taking precedence.
- [x] Write functional tests for no-store headers, finite request limits and redacted error/log behavior; implement one structured logging pipeline without payload logging.
- [x] Run the affected tests and commit the verified task.

### Task 3: Add PostgreSQL persistence and migration primitives — IR-04, IR-07, IR-10

**Files:** `src/Infrastructure/ArturRios.Cerberus.Data/AppDbContext.cs`; `Configuration/`; `EntityMaps/`; `Migrations/`; repository contracts in Domain; `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/` fixtures and constraint tests.

- [x] Write real PostgreSQL tests for singular snake_case mapping, migration application, uniqueness, optimistic concurrency and transaction rollback. Add only foundation infrastructure entities here; business entities arrive with their owning issues.
- [x] Observe failing behavior tests, then implement family provider/context integration and async repository abstractions.
- [x] Provide an explicit migration command and configuration validation; do not migrate implicitly on each HTTP request.
- [x] Rerun real-database tests and commit.

### Task 4: Bootstrap scoped Heimdall contracts and token validation — IR-05

**Files:** `src/Application/ArturRios.Cerberus.Shared/Identity/IHeimdallClient.cs`; `Identity/HeimdallClient.cs`; `Identity/HeimdallContracts.cs`; WebApi security registration; controlled HTTP fixtures under Shared.Tests and WebApi.Tests.

- [x] Inspect Heimdall login, challenge completion, scoped registration and validation contracts from the reference implementation. Record the inspected revision and payload shapes.
- [x] Write fixtures rejecting invalid issuer/audience/signature/expiry/scope, unsupported upstream responses, rejected credentials, timeouts and missing service privilege. Assert no privilege escalation or credential logging.
- [x] Observe expected failures; implement typed adapter contracts and token validation using the inspected first-party libraries. Do not implement UC-01/UC-02 endpoint behavior under the foundation issue.
- [x] Run all adapter/security fixtures and commit.

### Task 5: Add durable erasure, restore and worker primitives — IR-11, IR-12, IR-13

**Files:** Domain operational contracts; Data ledger implementation and work claiming; WebApi hosted-worker registration; restore tooling; `docs/operations/restore.md`; Data.Tests and WebApi.Tests operational fixtures.

**Interfaces:** `IErasureLedger.RecordAsync(ErasureEntry entry, CancellationToken cancellationToken)` records a durable deletion; `IErasureLedger.ReadAsync(CancellationToken cancellationToken)` streams entries for replay. `IRestoreReconciler.ReconcileAsync(CancellationToken cancellationToken)` must succeed before restored traffic is enabled.

- [x] Write tests for duplicate erasure records, interrupted writes, inaccessible ledger storage and replay after a database rollback. Assert deleted IDs remain inaccessible and failures keep traffic disabled.
- [x] Implement durable ledger records outside the restorable database using the operator-approved storage and documented access control.
- [x] Write concurrent PostgreSQL work-claim tests, retry/crash-recovery tests with a deterministic clock and restore authorization-reconciliation tests; implement idempotent infrastructure primitives.
- [x] Exercise the isolated restore procedure against disposable data and commit. Defer domain deletion/expiry behavior to its specified use cases.

### Task 6: Enforce protocol review and delivery verification — IR-08, IR-09, IR-10

**Files:** versioned protocol decision/review record; `.github/workflows/tests.yml`; `Dockerfile`; `.dockerignore`; OpenAPI generator under `tools/`; `scripts/openapi.py`; coverage tooling tests; installation/testing sections of README and formal Testing/Technology documents.

- [x] Test that an absent or unapproved protocol record blocks dependent implementation/release validation. Record pending review honestly; do not substitute a fabricated approval marker.
- [x] Test merged coverage aggregation without test assemblies or stale input; retain the 90% floor and fail when no valid report exists.
- [x] Add generated OpenAPI drift checks and a Docker artifact compatible with the approved host; retain all required CI checks.
- [x] Run restore/build, the unfiltered application suite, merged coverage, vulnerability scan, specification/helper tests, OpenAPI checks and Docker build. Validate README commands against a clean checkout.
- [x] Review IR-01–IR-14 individually. Beta design inputs approved; IR-09 gate implemented with pending real NFR-11 review, which blocks dependent encryption/recovery work rather than foundation acceptance. Whole-branch review findings reproduced/fixed and full suite green; required CI gates the merge.

## Per-use-case execution loop

- [ ] Reload that use case's main flow, every AF, traced requirements, authorization matrix, technology/testing rules and applicable operations requirements.
- [ ] Produce its concrete design, file/interface map and test-first plan. Map every flow to behavioral assertions and verify references exist.
- [ ] Branch from current `develop`; move only its issue to In Progress.
- [ ] Implement tests and behavior at the correct layers, preserving ciphertext, ownership, selected-profile, sharing and lifecycle rules.
- [ ] Confirm every main/alternative flow; advance to Testing under the recorded authorization.
- [ ] Run `dotnet test src/ArturRios.Cerberus.sln` unfiltered, enforce merged coverage and run the applicable contract/tooling checks. Read fresh output.
- [ ] Update that issue's README row and milestone progress; push and open its own PR with the actual closing issue reference.
- [ ] Wait for successful CI, merge without bypass, delete the merged feature branch, close the issue and set project status Done under explicit authorization.
- [ ] Check the complete Definition of Done, sync `develop`, confirm no leftover changes and report the PR/issue/test evidence before beginning the next issue.

Stop the batch on a required test failure after three fix attempts, failed CI, an unresolved merge conflict, missing/dangling requirements, ambiguous specifications, unavailable mandatory review or an unsatisfied repository rule. Preserve already-merged work and the failing branch/PR. Routine successful issue transitions do not need repeated approval once explicitly authorized for the whole batch.
