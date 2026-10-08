# UC-05 Update Identity Details Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans inline under existing batch authorization, test-driven-development and one fresh final whole-branch review.

**Goal:** Deliver issue #6, self-bound Heimdall name/email update with current account-wide vault access, preserving all Cerberus vault state.

**Architecture:** Domain owns result/authorized-callback contracts; Shared implements the inspected Heimdall PUT; Data locks account/session and authorizes at fresh database statement time before invoking the callback. Command validates permitted fields and WebApi injects signed actor/token/raw access header.

**Tech Stack:** Pinned .NET10, first-party mediator/output/JWT/test tooling, FluentValidation, EF Core/PostgreSQL, xUnit/Moq/Testcontainers and controlled Heimdall HTTP fixtures.

**Spec:** `docs/superpowers/specs/2026-10-08-uc-05-update-identity-details-design.md`; UC05/FR-AC-10, BR02/BR03 and shared current authorization/no-store rules.

## Global Constraints

- Only authenticated current scoped actor's own identity; no target/owner/role/scope/password fields accepted.
- Required name<=200 and valid email follow actual Heimdall rules; encrypted account details/profile data remain intact.
- Current active non-erased account and account-wide session; lock account then session, validate expiry at fresh database statement time after locks.
- Forward original actor token to `/api/persons/{actor-guid}`; never use registration service identity or forward arbitrary provider controls.
- Curate only id/name/email/emailVerified; no internal IDs, credentials, provider error bodies or vault content in outputs/logs.
- Invalid400, missing401, denied403, hidden404, provider conflict409, required dependencies503, all no-store.
- No local vault writes/migration; remote response loss may follow an upstream commit, never claim rollback; safe name/email replacement retry.
- Full suite zero failures/relevant skips,>=90% production line coverage and report branch; required CI green and authorized merge into develop.
- Independent protocol/client approval remains deferred for development, pending for release.

## Review Focus

- Forged actor/role/scope or service credential substitution cannot change another identity.
- Callback is never invoked for missing/denied/expired account access, including time spent waiting for locks.
- Account/session lifecycle writes cannot interleave the authorized external callback; lock order is consistent.
- Provider success must name the same scoped role3 person and required identity fields; incompatible/malformed results fail closed.
- A lost provider response cannot mutate Cerberus vault state or fabricate a successful rollback.

### Task 1: Implement typed self-bound Heimdall update adapter

**Files:** Domain `Identity/IdentityUpdateContracts.cs`; Shared Identity contracts/client (split update parser into partial file if needed); Shared `IdentityUpdateClientTests.cs` plus existing adapter fixtures.

**Interfaces:** `IdentityDetails(Guid Id,string Name,string Email,bool EmailVerified)`; `IdentityUpdateResult(IdentityDetails? Identity=null,string? Error=null)`; `IIdentityUpdateStore.UpdateAsync(Guid identityId,string verifier,Func<CancellationToken,Task<IdentityUpdateResult>> update,CancellationToken)`; `IHeimdallClient.UpdateIdentityAsync(string token,Guid identityId,string name,string email,CancellationToken)`.

- [ ] Write adapter contract tests for exact own route/name-email-only body/actor bearer, invalid or mismatched token, correct curated response,400/401/403/404/409/503, malformed/missing/mismatched identity/scope/role/required metadata, duplicate/numeric JSON, timeout and caller cancellation. Observe absent-method RED.
- [ ] Add Domain contracts and typed adapter. Validate original actor token, strict bounded response parsing, required own public identity/scope/role; map stable errors without provider bodies. Run full Shared suite GREEN. Commit.

### Task 2: Hold current account permission across external update

**Files:** Data `Identity/IdentityUpdateStore.cs`; Data `IdentityUpdateStoreTests.cs`.

**Interfaces:** Consume Task1 authorized-callback store contract/result; no encrypted account bytes are retrieved; no local state writes.

- [ ] Write PostgreSQL tests for valid/no-expiry callbacks, all account/session denials without callback, lock contention and access changed before guard, expired-after-wait boundary, callback provider errors/response loss, database outage/cancellation, unchanged vault state and account/session lock blocking. Observe missing-store RED.
- [ ] Implement transaction locking account then matching session without ciphertext projection. Validate all access predicates using statement_timestamp() after locks; invoke callback only on success; commit/release locks, propagate typed failures/cancellation. Run full Data suite GREEN. Commit.

### Task 3: Deliver protected identity command and route

**Files:** Command `Identity/UpdateIdentityCommand.cs`, validator/handler/output/statuses; WebApi IdentityController/Startup; Command unit tests; WebApi identity-update HTTP tests and controlled Heimdall fixture extensions; OpenAPI/README/operational docs.

**Interfaces:** strict command body Name/Email, server-only actor/token/access via SetActor; output public Id/Name/Email/EmailVerified; handler uses authorized store callback with typed adapter.

- [ ] Write handler/validator tests for input and trusted context, callback sequencing, success/errors/incomplete results/cancellation. Observe RED; implement, run Command suite GREEN.
- [ ] Write real-host tests for main/all five AFs, strict body/header/query rejection, owned access isolation, provider route/body/auth binding and failure/conflict contracts, name/email verification reset, and unchanged local vault state. Observe absent-route RED; implement controller/DI/controlled fixture, run full WebApi suite GREEN.
- [ ] Generate/inspect OpenAPI, mark only UC05 done and milestone5/9. Run fresh unfiltered coverage.py/helper/spec/contract/diff checks; report line>=90% and branch. Commit and ledger.
- [ ] One fresh final code review and one RED→GREEN correction pass if needed; open own PR closing #6, wait required CI green, merge/close/projectDone/verify remote branch deletion/sync/archive under existing authorization.
