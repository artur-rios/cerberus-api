# UC-03 Get Account Implementation Plan

> **For agentic workers:** Use superpowers:executing-plans inline under existing batch authorization, test-driven-development, and one fresh final whole-branch review.

**Goal:** Deliver issue #4, owner-only encrypted account read with current vault access.

**Architecture:** Domain owns canonical opaque-handle validation and session/store contracts. Data stores verifier metadata and projects only currently authorized ciphertext. Query validates/returns public envelope metadata; WebApi injects trusted actor and dispatches QueryMediator.

**Tech Stack:** Pinned .NET 10, first-party mediator/output, EF Core/PostgreSQL, SHA-256 opaque-handle verifier, xUnit/Moq/Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-08-uc-03-get-account-design.md`; UC-03 and FR-AC-08.

## Global Constraints

- Current scoped Heimdall identity and active non-erased owner account are required.
- Account-wide vault access required; no session issuance or unlock in this route.
- Opaque content only; no internal IDs, identity profile, credentials or client keys in outputs/logs.
- Canonical 32-byte base64url access header; persist verifier only; exclusive expiry and current policy/generation.
- No-store success/failure; no writes on reads/denials.
- One branch/issue/PR to develop; green required CI; fresh full suite zero relevant skips and >=90% production line coverage, report branch coverage.
- Actual independent protocol approval remains pending under development deferral.

## Review Focus

- Another account's handle must not authorize the current identity's account.
- Profile-only access must not become account-wide access.
- Expiry equality, future issuance, null-expiry policy and stale revisions must fail closed.
- Concurrent account closure/erasure must prevent ciphertext disclosure in the read's current database snapshot.
- Corrupt stored metadata and dependency faults must return no ciphertext or keys.

### Task 1: Persist and verify account-wide access

**Files:** Domain `Access/OpaqueAccessHandle.cs`, `Access/VaultAccessSession.cs`, `Accounts/AccountReadContracts.cs`; Data `Accounts/AccountReadStore.cs`, `AppDbContext.cs`, generated migration; Domain handle tests and Data account-read tests.

**Interfaces:** `OpaqueAccessHandle.TryHash(string? token, out string verifier)`; `AccountSnapshot(Guid Id,long Revision,AccountState State,byte[] DetailsEnvelope)`; `AccountReadResult(AccountSnapshot? Account=null,string? Error=null)`; `IAccountReadStore.ReadAsync(Guid identityId,string verifier,DateTimeOffset now,CancellationToken)`.

- [x] Add canonical handle and real PostgreSQL tests for valid/no-expiry sessions, expiry equality/future issuance, profile scope, stale policy/generation, revocation, cross-account use, hidden account, corruption boundary and provider failure/cancellation. Run: expected missing types/behavior fails.
- [x] Implement verifier-only session schema with account FK/cascade and unique verifier, current authorization predicates and conditional ciphertext projection. Generate migration. Run Domain/Data tests: expected green. Commit.

### Task 2: Deliver protected account query

**Files:** Query `Accounts/GetAccountQuery.cs`, `GetAccountHandler.cs`, output/status map; WebApi `AccountController.cs`, `Startup.cs`; Query tests and WebApi account-read HTTP tests; OpenAPI/README/operational docs.

**Interfaces:** server-created `GetAccountQuery(Guid identityId,string? vaultAccess):BaseQuery`; `IQueryHandlerAsync<GetAccountQuery,AccountOutput>`; public output ID/revision/state/EncryptedEnvelope details.

- [x] Add unit tests for missing/invalid handles/actor, store outcomes, public projection and corrupt metadata; observe RED, implement using TimeProvider and Domain store, run Query suite GREEN.
- [x] Add real-host tests for main/every AF, missing/current denied identity, header/query/body validation, isolation, expired/revoked/profile session and actual database outage; observe absent route RED. Implement trusted-actor/header extraction, QueryMediator/DI/status mapping; run WebApi suite GREEN.
- [x] Generate/inspect OpenAPI, mark only UC-03 done and update milestone progress. Run fresh unfiltered coverage.py/helper/spec/contract/format checks: expect zero failures/skips and >=90% line coverage.
- [x] One fresh final review; reproduce/fix Important findings once, ledger rulings/minors/declines. Open own PR closing #4; required CI green, merge/close/projectDone/delete feature branch/sync under existing authorization.
