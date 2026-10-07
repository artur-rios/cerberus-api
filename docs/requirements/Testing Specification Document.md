# Testing Specification Document — Cerberus API

## 1. Purpose

Define how each use case is verified before completion, following the approved Heimdall
standards. A use case without the required tests is incomplete. Tool versions belong in
[Technology Stack](Technology%20Stack%20Document.md); timing and approval gates belong in
[Development Workflow](Development%20Workflow%20Document.md).

---

## 2. Testing philosophy

1. Test behavior and observable invariants rather than mirroring implementation.
2. Unit-test handlers, validators and domain behavior in isolation.
3. Functional-test HTTP contracts and persistence through the real database engine.
4. Use controlled identity dependencies and deterministic clocks for failure/race scenarios.
5. Apply the same per-use-case flow coverage and authorization checks consistently.

Merged line coverage must be at least 90%; report branch coverage without a numerical gate.
Coverage never substitutes for correct security, concurrency and alternative-flow assertions.

---

## 3. What to test for each use case

| Artifact | Kind | Location |
| --- | --- | --- |
| Command/query handler and validators | Unit | Mirrored Command.Tests / Query.Tests |
| Domain behavior and permission decisions | Unit | Domain.Tests / Shared.Tests |
| API endpoints and middleware | Functional | WebApi.Tests with database containers |
| Persistence constraints and transaction races | Functional | Data.Tests or WebApi.Tests using real database |
| Heimdall HTTP adapter | Functional contract fixture | WebApi.Tests / Shared.Tests as appropriate |
| Retention worker and restore reconciliation | Unit plus functional persistence | Data.Tests / WebApi.Tests |

Plain property holders earn no getter/setter tests. Client cryptographic implementations are
outside this repository, but approved test vectors must validate API envelope/proof contracts;
absence of client code is not permission to omit the cryptographic design review.

---

## 4. Test project layout

```text
tests/
  Domain/ArturRios.Cerberus.Domain.Tests/
  Application/ArturRios.Cerberus.Command.Tests/
  Application/ArturRios.Cerberus.Query.Tests/
  Application/ArturRios.Cerberus.Shared.Tests/
  Infrastructure/ArturRios.Cerberus.Data.Tests/
  Presentation/ArturRios.Cerberus.WebApi.Tests/
```

One test project per production project, named by appending `.Tests`; mirror namespaces and
production class names with a `Tests` suffix. Add all projects to the solution under `src/`.
Reference the production project, make tests nonpackable, and keep package versions consistent.

---

## 5. Naming & structure

Use `GivenSomeCondition_WhenSomeAction_ThenSomeOutput`, for example:

```text
GivenConsumedRecoveryCredential_WhenRecoveringAgain_ThenRejectWithoutChangingKeys
```

Use Given/When/Then sections inside the test:

```csharp
// Given: arrange an account, current recovery generation and deterministic dependencies.
// When: invoke the recovery handler or send the API request.
// Then: assert the result, persisted generation and absence of unauthorized mutations.
```

Use the family UnitFact/UnitTheory and FunctionalFact/FunctionalTheory attributes and their
category traits. Use generated input where it helps; use explicit boundaries when testing
deadlines, timestamps or identifier conflicts.

---

## 6. Unit testing standard

### 6.1 Scope of a unit test

Exercise one behavior without reaching a live database, network, filesystem service or real
clock. Replace repositories, Heimdall adapters, validators and time providers as appropriate.

### 6.2 Test doubles

Prefer ArturRios.Util.Test repository fakes and host helpers; use Moq for other collaborators
and Bogus for test data. Do not introduce a second mocking library. Fake cryptographic
contracts only at explicit interfaces; do not use a toy cipher as proof of real E2E security.

### 6.3 Coverage per unit

Cover success, input boundaries, hidden/not-found targets, ownership/profile/grant denials,
read-only versus read/write, consumed/superseded recovery, expired authorization, and state
transitions applicable to the unit. Verify the winning state after conflicts and denial; a
status-only assertion is insufficient when unauthorized mutation would be harmful.

---

## 7. Functional testing standard

### 7.1 Scope

Drive each API endpoint through the family functional host and real PostgreSQL containers.
Assert response envelopes/statuses, authorized ciphertext visibility and persisted state.
Do not substitute an in-memory database for constraints, transaction or concurrency behavior.

### 7.2 External dependencies

Use controlled Heimdall HTTP fixtures with representative identity, scope, challenge and
revocation contracts from the inspected reference implementation. Never use production users,
credentials or live email. Contract fixtures must detect incompatible responses, not simply
return a successful mock for every request. Include upstream timeout/unavailable behavior.

### 7.3 Coverage per entry point

Every main flow and applicable AF must have a behavioral assertion. Include guessing another
account's GUID, selected-profile escape, read-only writes, shared-folder descendant changes,
recipient removal, software grant limits, generic login rejection and no-store headers.

Mandatory race/boundary scenarios:

| Concern | Evidence |
| --- | --- |
| Recovery | Concurrent use permits one commit; lost-response retry cannot consume again; refresh rejects old generation. |
| Deletion | Restore just before/at/after deadline, cascade versus independently trashed items, empty trash, account cancellation and ledger replay. |
| Sharing | Read-only/read-write matrix; owner-only deletion/membership; no unrelated profiles; regrant needed after restored collection. |
| Offline | Initial 24-hour policy, arbitrary supported positive duration, disabled renewal, rejected renewal, policy delivery on reconnection. |
| Synchronization | Older/newer/equal timestamps, persisted tie ordering, duplicate operation IDs, stale access and terminal tombstone rejection. |
| Storage/logging | No plaintext content/usable keys, redacted credentials/proofs, encrypted names and structural metadata boundary. |
| Operations | Readiness versus liveness, operator-only details, fail-closed unset retention/ledger configuration and safe restore replay. |

---

## 8. Per-use-case workflow

1. Read the UC main flow, all AF rows, traced requirements and permission matrix.
2. Map each behavioral assertion to unit, functional or contract coverage.
3. Grow meaningful tests with implementation; finalize coverage in the Testing stage.
4. Run required categories and coverage collection, fix failures, and rerun affected checks.
5. Preserve fresh evidence in the PR; use the Development Workflow's approval gates.

The foundation must establish a reproducible coverage merge and gate without counting test
assemblies as production coverage. Do not invent a successful crypto review, a benchmark
result or a passing suite in a documentation-only repository.

---

## 9. Running the suites

From the repository root after scaffolding:

```bash
dotnet test src/ArturRios.Cerberus.sln
```

| Suite | Command |
| --- | --- |
| Unit | `dotnet test src/ArturRios.Cerberus.sln --filter "Category=Unit"` |
| Functional | `dotnet test src/ArturRios.Cerberus.sln --filter "Category=Functional"` |
| Coverage input | `dotnet test src/ArturRios.Cerberus.sln --collect:"XPlat Code Coverage"` |

Run `python3 scripts/coverage.py` for a fresh unfiltered collection, merged HTML/JSON report
and 90% production line-coverage gate. Install `dotnet-reportgenerator-globaltool` first.
The script removes old TestResults, excludes test/foreign assemblies and fails on missing,
empty or invalid summaries. Branch coverage is reported without a numerical threshold.
Docker must be available for functional tests. CI runs categories separately plus the full
unfiltered suite, then `python3 scripts/coverage.py --report-only` on that checkout's fresh
inputs. Run `python3 scripts/openapi.py` to check contract drift and the Python helper suite
to test these gates. No benchmark, independent crypto review or client compatibility result
is implied by the foundation fixtures.
