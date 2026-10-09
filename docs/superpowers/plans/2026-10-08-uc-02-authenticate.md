# UC-02 Authenticate Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline under existing batch authorization. Follow test-driven-development for each task and one fresh whole-branch review at the end.

**Goal:** Deliver issue #3, scoped identity login/MFA and permitted account context without vault access.

**Architecture:** Shared types distinguish provider outcomes and revalidate identity. Domain/Data read only current account context by trusted identity. Command handlers validate and dispatch; WebApi exposes two explicit routes.

**Tech Stack:** Pinned .NET 10, FluentValidation, ArturRios.Mediator/Output, EF Core/PostgreSQL, xUnit/Moq/Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-08-uc-02-authenticate-design.md`; UC-02 and FR-AC-05–07.

## Global Constraints

- Preserve server-owned scope and current online identity validation.
- Identity login never unlocks vault content or creates an access handle.
- Strict visible input; no credentials, tokens or bodies in logs; no-store responses.
- One branch/issue/PR into develop; all required CI green before merge.
- Fresh full unfiltered suite, zero relevant skips, >=90% production line coverage; report branch coverage.
- Actual independent protocol review remains pending under the owner’s development deferral.

## Review Focus

- A signed result for the wrong scope or a now-deleted identity must yield no bearer/context.
- A completed challenge must undergo the same authorization checks as direct login.
- A pending challenge must never trigger account lookup or become completed authentication.
- Existing inactive/tombstoned accounts must be denied, while unbound identities reveal no vault rights.
- Dependency/database faults and caller cancellation must not collapse into successful or misleading authentication.

### Task 1: Typed scoped identity authentication

**Files:** Shared `Identity/HeimdallContracts.cs`, `HeimdallClient.cs`; Shared `AuthenticationIdentityTests.cs`.

**Interfaces:** `HeimdallAuthentication(HeimdallLogin? Login = null, Guid? IdentityId = null, string? Error = null)`; `IHeimdallClient.AuthenticateAsync(string email, string password, CancellationToken)` and `VerifyChallengeAsync(string challenge, string? code, string? recoveryCode, CancellationToken)`.

- [x] Write adapter fixtures for completed login/challenge, MFA, 401/403, malformed/unavailable, foreign-scope/invalid token, current denial/outage and caller cancellation. Run targeted tests: expected missing interface or behavior fails.
- [x] Add typed adapter methods preserving existing nullable methods. Structural parse precedes signed/current identity validation; pending challenge carries no identity GUID. Run full Shared suite: expected green. Commit.

### Task 2: Read permitted account context

**Files:** Domain `Accounts/AuthenticationContracts.cs`; Data `Accounts/AccountAuthenticationStore.cs`; Data `AccountAuthenticationStoreTests.cs`.

**Interfaces:** `AuthenticationAccount(Guid Id, long Revision)`; `AccountAuthenticationResult(AuthenticationAccount? Account = null, string? Error = null)`; `IAccountAuthenticationStore.FindAsync(Guid identityId, CancellationToken)`.

- [x] Write real PostgreSQL tests for active, absent, closure-pending, erased and unavailable database outcomes plus cancellation. Run: expected missing types/behavior fails.
- [x] Implement no-tracking account/tombstone lookup and typed provider-failure result; no writes or credentials. Run full Data suite: expected green. Commit.

### Task 3: Validated mediator/HTTP delivery

**Files:** Command `Authentication/` login/challenge commands, validators, handlers/output/status map; WebApi `Controllers/AuthController.cs`, `Startup.cs`; Command handler/validator tests; WebApi `AuthenticationHttpTests.cs` and controlled identity fixture; generated OpenAPI/README.

**Interfaces:** `LoginCommand : BaseCommand` email/password; `VerifyChallengeCommand : BaseCommand` challengeToken/code/recoveryCode; both handlers return `DataOutput<AuthenticationOutput?>` with `HeimdallLogin Identity` and nullable `AuthenticationAccount Account`; adapter outcome selects store lookup only for completed identity.

- [x] Write validator/handler tests covering invalid input, pending MFA no store calls, all stable errors, unbound/active/inactive accounts and both authentication modes. Run: expected missing behavior fails; implement and run full Command suite green.
- [x] Add real-host tests for both routes and every AF, including malformed/duplicate bodies before calls, generic credential denial, inactive/erased accounts, controlled upstream failures and database failure. Run: expected routes absent; implement mediator dispatch/DI/status mapping. Run WebApi suite: expected green.
- [x] Generate OpenAPI, inspect both routes and public schemas; mark UC-02 done in branch README. Run fresh unfiltered coverage.py, helper/spec/OpenAPI/format checks: expected zero failures/skips and coverage>=90%.
- [ ] One fresh whole-branch review; reproduce/fix important issues, record all rulings/declines/minors. Push/open PR closing #3; green CI, merge, close issue/set Done/delete branch/sync under batch authorization.
