# UC41 Refresh recovery key implementation plan

> REQUIRED: executing-plans inline, test-driven development, one final ordinary whole-branch review. Existing explicit batch authorization replaces approval gates.

**Goal:** issue42 authorized full-wrapper refresh, old recovery invalidation and the missing named fresh recovery-material read.
**Architecture:** extend tested strict RecoveryReplacement/Request and shared atomic recovery store/handler; PUT pins refresh purpose, POST pins recover; no new migration/dependency/secret handling.
**Spec:** docs/superpowers/specs/2026-10-08-uc-41-refresh-recovery-key-design.md.

## Global Constraints

- Fresh/current owner+account-wide session+old unlock proof for new refresh operation; native six-field/full-wrapper protocol.
- Current slot/recovery/protection/revocation increments, retained pins/content unchanged; safe counters/fresh contexts.
- Original bytes, purpose/identity/scope/epoch/revision binding; null refresh challenge generation.
- Account/session locks and fresh statement timestamps; expiry checked at atomic consume, rollback all writes.
- Historical exact retries before consumed/expired/current-session checks; current/fresh owner still mandatory, no secret returned.
- Full main/AF01–05, unfiltered0fail0skip/line>=90/reportbranch, single ordinary review/CI before merge.

## Review Focus

- POST cannot invoke refresh; PUT cannot invoke recover, even with valid signatures.
- Recovery key cannot authorize refresh; current unlock key cannot recover; fresh new recovery key proves neither old credential.
- Natural session/challenge/identity expiry after waiting for account/session lock rejects without consumption.
- Refreshed generation invalidates old recovery and old handles; new unlock and new recovery succeed.
- Exact committed retry under stale original handle returns historical nonsecret outcome without another transition; changed bytes/purpose conflict.
- Named recovery-material GET requires fresh/current identity and never consumes or creates sessions.

### Task 1: Native refresh transition contracts

**Files:** Domain/Protection/RecoveryReplacement.cs; Domain.Tests/RecoveryReplacementTests.cs.
**Interfaces:** existing RecoveryReplacement.IsValid accepts recover/refresh-recovery; existing RecoveryRequest adds optional string? AccessVerifier=null after Replacement. Existing callers compile unchanged. Pure transition validation/material replacement shared.
- [ ] Write refresh full-wrapper/pin/generation/context tests and unsupported-operation regression; observe refresh rejected RED.
- [ ] Extend strict contracts, full Domain GREEN, commit/ledger.

### Task 2: Current-session authorized atomic refresh

**Files:** Data/Protection/VaultRecoveryStore.cs, VaultProtectionStore.cs; Data.Tests/VaultRecoveryRefreshStoreTests.cs.
**Interfaces:** shared RecoverAsync(RecoveryRequest,ct); operation selects old recovery/current unlock key and generation binding. Challenge accepts refresh-recovery with null generation. Reuse unique outcomes; no migration.
- [ ] Real PostgreSQL refresh/new recovery/old credential rejection; denied session variants, wrong key/purpose/body, lifecycle/revision/policy/overflow, lock-wait account/session/proof/iat expiry, concurrent same/different-key, historical replay/purpose collision, actual outage/cancel and post-consumption rollback. Observe unsupported purpose RED.
- [ ] Implement account-first/current session locks and statement-time consume checks, purpose-selected native key, atomic replacement/outcome. Full Data GREEN, commit/ledger.

### Task 3: Strict PUT refresh and named fresh recovery read

**Files:** shared RecoverVaultCommand/Validator/Handler, challenge validator/handler, VaultController; Command.Tests/VaultRecoveryRefreshHandlerTests.cs; WebApi.Tests/VaultRecoveryRefreshHttpTests.cs; docs/README/OpenAPI.
**Interfaces:** existing command SetContext adds optional access=null internal; handler hashes access only for refresh; same six public fields/four output. POST checks operation recover; PUT checks refresh-recovery. GET recovery-material shares GetVaultProtectionQuery projection and strict GET body/query checks, requires fresh current identity.
- [ ] Command context/access/purpose/hash/projection/error/cancel/challenge generation tests RED; implement full Command GREEN.
- [ ] HTTP missing PUT/read RED; implement native refresh→old recovery denial→new unlock/recovery, exact retry, AFs/strictness/current and fresh identity, read no consumption/session. Full WebApi GREEN.
- [ ] Docs/README UC41Done/M02 9/9, supporting UC40 read correction; generate/inspect OpenAPI; helpers/spec/drift/diff/full unfiltered coverage; commit/ledger.
- [ ] One final ordinary review/blocker correction pass if needed; required CI/pinned merge/issueDone/branch absence/tested tree equality/archive/sync.
