# UC-01 Register Account Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans to implement this plan inline. The existing batch authorization replaces per-stage human approvals with verifications.

**Goal:** Implement issue #2, including every registration failure and reconciliation flow.

**Architecture:** Domain owns encrypted envelope validation and registration-store contracts; Command validates and dispatches registration; Shared establishes a proved scoped Heimdall identity; Data performs durable PostgreSQL reconciliation; WebApi exposes the command through the mediator.

**Tech Stack:** Existing pinned .NET, EF Core/PostgreSQL, ArturRios.Mediator/Output, FluentValidation, xUnit/Moq and Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-08-uc-01-register-account-design.md` and UC-01 in the Use Case Specification; FR-AC-01–04 all exist.

## Global Constraints

- Preserve client-held keys, ciphertext, public GUID boundaries and no-store responses.
- One use case, branch, issue and PR into develop; required CI gates every merge.
- Full unfiltered suite and at least 90% merged production line coverage; report branch coverage.
- Do not persist, log or return identity credentials, usable vault keys or internal IDs.
- Independent review is explicitly deferred for development only; keep actual approval pending.

## Review Focus

- A lost upstream response must reconcile using actual ownership proof, not email.
- Concurrent requests and changed operation input must preserve the winning account.
- Unknown fields and noncanonical envelope encodings must fail before side effects.
- Identity outages and pending MFA must never fall through to account creation.
- A completed retry must not disclose account context without current identity proof.

### Task 1: Validate encrypted registration input

**Files:** Domain `Accounts/EncryptedEnvelope.cs`; Command `Accounts/RegisterAccountCommand.cs`, `RegisterAccountValidator.cs`; mirrored Domain/Command tests.

**Interfaces:** `EncryptedEnvelope.IsValid()`; `RegisterAccountCommand : BaseCommand` with account/operation GUIDs, identity registration input and encrypted details; `RegisterAccountValidator : AbstractValidator<RegisterAccountCommand>`.

- [ ] Write literal valid/invalid envelope and registration validation tests, including unknown JSON members, canonical encoding, exact nonce/tag lengths, invalid identifiers and credential shape.
- [ ] Run the new tests; expected missing behavior fails before implementation.
- [ ] Implement strict metadata validation without decrypting content and validator rules matching Heimdall's name/email/password contract.
- [ ] Run Domain and Command tests; expected all pass. Commit the verified task.

### Task 2: Establish proved scoped identity

**Files:** Shared `Identity/HeimdallClient.cs`, `HeimdallContracts.cs`; Domain `Accounts/RegistrationContracts.cs`; Shared fixture tests.

**Interfaces:** `IHeimdallClient.EstablishRegistrationIdentityAsync(HeimdallRegistration registration, string? proofToken, CancellationToken cancellationToken)` returns `RegistrationIdentity`; `IRegistrationStore.RegisterAsync(RegistrationRequest request, Func<CancellationToken, Task<RegistrationIdentity>> establishIdentity, CancellationToken cancellationToken)` returns `RegistrationResult`.

- [ ] Add HTTP fixtures for new creation, proved existing login, explicit proof, MFA, duplicate email, malformed/denied/unavailable response and lost-create-response retry. Observe expected failures.
- [ ] Implement typed outcomes; only rejected login permits constrained creation. Revalidate all existing identity proofs and preserve original adapter contracts.
- [ ] Run the complete Shared suite; expected all pass. Commit.

### Task 3: Persist and reconcile registrations

**Files:** Domain `Accounts/Account.cs`, `RegistrationOperation.cs`; Data `Accounts/RegistrationStore.cs`, `AppDbContext.cs`, generated migration; Data registration tests; Command `Accounts/RegisterAccountHandler.cs` and handler tests.

**Interfaces:** Implement `IRegistrationStore` using real PostgreSQL; `RegisterAccountHandler : ICommandHandlerAsync<RegisterAccountCommand, RegisterAccountOutput>` returns `DataOutput<RegisterAccountOutput?>`.

- [ ] Add PostgreSQL tests for preserved ciphertext, unique identity/account/operation constraints, changed-input conflicts, concurrent attempts, terminal identifiers, and failure after upstream creation followed by retry. Observe failures.
- [ ] Implement a durable pending-operation transaction followed by serialized identity establishment and atomic account binding. Key input fingerprints with the configured server secret; store no credentials.
- [ ] Add handler tests for input denial, status mapping and dependency unavailability; observe failures, then implement handler and output.
- [ ] Run Data and Command suites; expected all pass. Commit migration and implementation.

### Task 4: Deliver HTTP registration

**Files:** WebApi `Controllers/AccountController.cs`, `Startup.cs`; WebApi registration functional tests with controlled HTTP Heimdall and real PostgreSQL; `docs/contracts/openapi.json`; README UC-01 row.

**Interfaces:** Anonymous `POST /api/accounts` passes optional bearer proof through the command mediator; stable DataOutput statuses 200/201/400/401/403/404/409/503.

- [ ] Write real-host success and every AF test, including no-store, credentials redaction, persisted ciphertext and pending-operation retry. Observe failures, then implement route/DI/status map.
- [ ] Generate and inspect OpenAPI with `python3 scripts/openapi.py --write`; mark only UC-01 done in branch README.
- [ ] Run `dotnet test src/ArturRios.Cerberus.sln --configuration Release --logger trx` unfiltered, `python3 scripts/coverage.py`, helper/spec/OpenAPI checks and applicable dependency/harness checks. Expected zero failures/skips and coverage at least 90%.
- [ ] Perform one fresh whole-branch code review; fix important findings with observed red/green tests. Push/open PR closing #2, require green CI, merge, close issue, delete feature branch and sync base under batch authorization.
