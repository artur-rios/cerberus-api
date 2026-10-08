# UC39 Change Vault Protection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans inline, TDD, one final whole-branch ordinary review under existing batch authorization.

**Goal:** Deliver issue40 complete atomic master-vault rewrap/content rotation.
**Architecture:** Domain defines strict transition/payload contracts; Data combines current session/native proof/inventory checks in account-first transaction; Command/WebApi bind original bytes and trusted actor/header values.
**Tech Stack:** Existing .NET10/EF PostgreSQL/native ECDsa/SHA256/mediator/FluentValidation/xUnit/Moq/Testcontainers; no new dependencies or server crypto secrets.
**Spec:** docs/superpowers/specs/2026-10-08-uc-39-change-vault-protection-design.md.

## Global Constraints

- Current active non-erased owner AND current account-wide vault handle AND old unlock proof; no login-only write.
- Strict safe integers/GUIDs/base64url/unknown-member rejection; original UTF8 bytes only, no reconstructed signing.
- New protection revision/slot epoch current+1; current recovery generation/four public pins preserved; fresh wrapper salts/nonces and password KDF salt.
- Rewrap preserves account ciphertext/revision; rotate-content requires complete current inventory, account epoch/revision+1; no partial mutation.
- Account-then-session locks/fresh statement_timestamp; exclusive proof/session expiry rechecked at consume; revocation generation+1 invalidates prior sessions/challenges.
- Main/AF01–06 complete; full unfiltered green suite/line>=90/branch reported; pending independent approvals remain release-gated.

## Review Focus

- A valid-looking replacement must not substitute recovery, unlock, recipient or author identities under a password-change operation.
- Concurrent rewrap and account edits must preserve one coherent wrapper/content revision set without partial changes.
- A queued change whose unchanged session or challenge naturally expires during a lock wait must write nothing.
- Complete content rotation must reject omitted/extra/foreign replacements before challenge consumption.
- A valid proof for unlock or a reformatted raw body must never authorize a protection change.

### Task 1: Strict replacement and transition contracts

**Files:** Domain/Protection/ProtectionChange.cs; Domain.Tests/ProtectionChangeTests.cs; tests/TestSupport/ProtectionFixture.cs test-only fresh-material helper.
**Interfaces:** strict `ContentReplacement(string ResourceKind,Guid ResourceId,long ExpectedRevision,EncryptedEnvelope Envelope)`; `ProtectionChange(Guid AccountId,long ExpectedProtectionRevision,long ExpectedAccountRevision,string Mode,ProtectionMaterial Material,ContentReplacement[] ContentReplacements)` with `IsValid()` and `IsValidTransition(ProtectionMaterial current)`; `ProtectionChangeRequest(Guid Actor,string AccessVerifier,Guid ChallengeId,string Proof,byte[] RawBody,ProtectionChange Change)`; `ProtectionChangeDetails(Guid AccountId,long ProtectionRevision,long KeyEpoch,long RecoveryGeneration,long AccountRevision)`; `IVaultProtectionChangeStore.ChangeAsync(ProtectionChangeRequest,CancellationToken)->Task<VaultResult<ProtectionChangeDetails>>`. New IVaultProtectionStore.ChallengeAsync overload takes actor,operation,requestHash,ct while existing overload remains usable.
- [ ] Write unit cases: legitimate rewrap/rotation, all unsafe/missing fields, changed key roles/generation, nonincrementing/overflow slot epoch, reused visible salt/nonces; absent-types RED.
- [ ] Implement strict records and pure shape/transition checks. Full Domain suite GREEN and commit/ledger.

### Task 2: Atomic protection replacement store

**Files:** Data/Protection/VaultProtectionChangeStore.cs; existing VaultProtectionStore challenge issuance overload; Data.Tests/VaultProtectionChangeStoreTests.cs.
**Interfaces:** Task1 IVaultProtectionChangeStore/request/result. Existing IVaultProtectionStore old and new ChallengeAsync signatures. Native VaultProof.Verify with current change-protection binding and raw bytes. No schema change required.
- [ ] Write real PostgreSQL cases for rewrap preserving ciphertext/revision and rotation replacing complete account envelope; old session invalidation; all permission/state/revision/key/inventory/proof mutations; challenge-purpose mismatch both directions; one concurrent winner; unchanged challenge/session expiry across account/session locks; queued lifecycle/policy mutations; outage/cancel/injected save rollback; observe missing-store RED.
- [ ] Implement account-then-session locking/fresh SQL time; validate current metadata/pins/inventory; atomic challenge consumption including fresh session expiry; complete writes with revocation increment. Extend existing challenge creation to exact unlock-account/change-protection only. Full Data GREEN and commit/ledger.

### Task 3: Typed change handler and protected PUT contract

**Files:** Command/Protection/ChangeVaultProtectionCommand.cs/Handler.cs/Validator.cs/Output.cs; existing challenge validator/handler/maps; WebApi VaultController.cs/Startup.cs; Command.Tests/VaultProtectionChangeHandlerTests.cs; WebApi.Tests/VaultProtectionChangeHttpTests.cs; OpenAPI/docs/README.
**Interfaces:** public command mirrors Task1 ProtectionChange six fields; internal actor/access/challenge/proof/raw set via SetContext; output Task1 confirmation five fields. Handler hashes canonical handle and calls store. Challenge operation new exact change-protection supported; outputs must match command purpose.
- [ ] Write success/error/incomplete output/trusted context/validator/cancel handler tests; observe RED, implement, full Command GREEN.
- [ ] Write real host main init/unlock/challenge/change/re-unlock/account-read and all AFs; absent PUT RED. Implement strict header/query/raw body binding, DI, maps200/400/401/403/404/409/503. Full WebApi GREEN.
- [ ] Document complete inventory/password-slot vs content epochs/retry/no copied-key revocation, generate/inspect OpenAPI; README UC39 Done M02 7/9. Full unfiltered coverage/helpers/spec/OpenAPI drift/diff checks; commit/ledger.
- [ ] One final ordinary review, one TDD correction pass for blockers, own PR closing40, all CI green, authorized merge/issueDone/archive/sync.
