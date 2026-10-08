# UC-04 Update Account Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans inline under existing batch authorization, test-driven-development and one fresh final whole-branch review.

**Goal:** Deliver issue #5: owner-only encrypted account replacement with atomic expected-revision conflict handling.

**Architecture:** Domain owns update contracts. Data rechecks current actor/account/access at a conditional SQL UPDATE and increments the revision atomically. Command validates the strict envelope/header/body and dispatches through CommandMediator; no identity link or key/session material changes.

**Tech Stack:** Pinned .NET10, first-party mediator/output, FluentValidation, EF Core/PostgreSQL, xUnit/Moq/Testcontainers.

**Spec:** `docs/superpowers/specs/2026-10-08-uc-04-update-account-design.md`; UC04/FR-AC-09, BR01 and shared permission/concurrency/envelope rules.

## Global Constraints

- Signed/current scoped Heimdall identity and active non-erased own account required.
- Current account-wide vault access; no profile-only, stale/revoked/expired/foreign session.
- Input contains only expectedRevision>0 and the supported encrypted details envelope; no caller identity/owner fields or plaintext.
- Only details and increasing revision change; identity link/state/policy/generation/sessions remain intact.
- Invalid400, missing401, denied403, hidden404, conflict409, dependencies503; all no-store and no content on failure.
- Full unfiltered suite zero failures/relevant skips, >=90% production line coverage and report branch coverage. Required CI green, own PR into develop under batch authorization.
- Actual independent protocol/client approval remains deferred for development and pending for release.

## Review Focus

- Two requests with the same expected revision cannot both commit or overwrite the winner.
- A state/session/tombstone change between initial validation and mutation must prevent the write.
- Invalid/unknown/duplicate JSON and raw empty header values must not become a valid update.
- Dependency failure and cancellation must not return a fabricated success or alter identity linkage.
- Revision exhaustion must fail without wraparound.
- Access time can pass after request capture/preflight; conditional writes must use database-evaluated current statement time for issuance/expiry.

### Task 1: Atomically replace authorized account details

**Files:** Domain `Accounts/AccountUpdateContracts.cs`; Data `Accounts/AccountUpdateStore.cs`; Data tests `AccountUpdateStoreTests.cs`.

**Interfaces:** `AccountUpdateRequest(Guid IdentityId,string Verifier,long ExpectedRevision,byte[] DetailsEnvelope,DateTimeOffset Now)`; `AccountUpdateResult(Guid? Id=null,long Revision=0,string? Error=null)`; `IAccountUpdateStore.UpdateAsync(AccountUpdateRequest,CancellationToken)`.

- [x] Write real PostgreSQL tests for valid/no-expiry writes, unchanged identity/policy/session fields, stale revision/exhaustion, all account/session denials, unavailable provider/cancellation, concurrent same-revision writers, and intercepted close/revoke/tombstone changes before UPDATE. Run: missing contract/store RED.
- [x] Implement metadata-only initial authorization and conditional atomic update with all current predicates and exact revision. Do not retrieve the previous envelope or change identity. Run full Data suite GREEN. Commit.

### Task 2: Deliver protected update command and HTTP contract

**Files:** Command `Accounts/UpdateAccountCommand.cs`, validator/handler/output/status map; WebApi AccountController/Startup; Command handler tests; WebApi AccountUpdateHttpTests; OpenAPI/README/operational docs.

**Interfaces:** strict `UpdateAccountCommand:BaseCommand` body `ExpectedRevision` + `Details`; server-only actor/access injected through `SetAccess(Guid,string?)`; `ICommandHandlerAsync<UpdateAccountCommand,UpdateAccountOutput>`; output only public Id/new Revision.

- [x] Add unit tests for invalid metadata/revision/identity/access, exact hashed verifier and serialized envelope, success/error mapping; observe RED, implement validator/handler using TimeProvider, run Command suite GREEN.
- [x] Add real-host HTTP tests covering main plus every AF, strict JSON/header/query input, cross-account/profile/expired/revoked access, missing/inactive/erased accounts, competing/stale revisions, identity outage and actual database failure; observe absent route RED. Implement protected PUT/trusted actor/header injection/DI/statuses; run WebApi suite GREEN.
- [x] Generate/inspect OpenAPI; mark only UC04 done and milestone4/9. Run unfiltered coverage.py, helper/spec/contract/diff checks with >=90% line coverage, report branch. Commit verified changes and ledger.
- [x] One fresh final code review; one correction pass with reproduced RED→GREEN if needed. Push/open PR closing #5, wait all required CI green, merge/close/projectDone/verify remote branch deletion/sync/archive ledger under existing authorization.
