# UC40 Recover Vault Access Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans inline, TDD, one final whole-branch ordinary review under existing batch authorization.

**Goal:** Deliver issue41 atomic one-use recovery and durable safe retry.
**Architecture:** Domain strict native replacement/contracts; Data account transaction/durable nonsecret outcome; Command/WebApi original-byte proof and trusted actor/signed iat context.
**Tech Stack:** Existing .NET10/EF PostgreSQL/native P256/SHA256/mediator/FluentValidation/xUnit/Moq/Testcontainers, no new dependency/server secrets.
**Spec:** docs/superpowers/specs/2026-10-08-uc-40-recover-vault-access-design.md.

## Global Constraints

- Fresh/current identity+old recovery-key proof; no existing vault session; no secret/private-key/decryption/signing/KDF in API.
- Exact native six-field body and purpose/generation/raw bytes; strict safe integers/GUIDs/JWK/wrappers.
- Complete wrappers/new recovery verifier, slot/protection/recovery/revocation counters+1; retained unlock/recipient/author pins and content unchanged.
- Fresh salt/nonce/KDF salt; distinct new recovery key; original-byte digest, no reconstructed signing.
- Unique own account/idempotency key/digest result returned before consumed/expired checks; changed body409; replay still current/fresh identity required.
- Account-first lock/fresh statement time; exclusive60s proof expiry and after-lock signed-iat freshness; all writes/consumption/outcome atomic.
- Main/AF01–07 complete, full unfiltered zero skips/failures/line>=90/report branch; independent approvals pending/release-gated.

## Review Focus

- Exact recovery retries after expiration or signature malleation must return the same stored outcome without another transition.
- Same idempotency key with changed original bytes or another actor must not return another operation's result.
- A queued recovery must recheck unchanged natural challenge/authentication expiry and current lifecycle/policy after the account lock.
- New recovery keys must never prove the current old credential, or substitute retained unlock/recipient/author pins.
- A database failure after consumption must roll back wrappers, generations, revocation and outcome so the identical proof can retry.

### Task 1: Strict recovery transition and durable-result contracts

**Files:** Domain/Protection/RecoveryReplacement.cs; Domain.Tests/RecoveryReplacementTests.cs; tests/TestSupport/ProtectionFixture.cs native recovery signing helper.
**Interfaces:** strict `RecoveryReplacement(string Operation,Guid IdempotencyKey,long ExpectedRevision,PasswordWrapper PasswordWrapper,RecoveryWrapper RecoveryWrapper,PublicJwk NewRecoveryVerifier)` with `IsValid()/IsValidTransition(ProtectionMaterial current)` and `Replace(ProtectionMaterial current)` returning complete material retaining other pins. `RecoveryRequest(Guid Actor,long IdentityIssuedAt,Guid ChallengeId,string Proof,byte[] RawBody,RecoveryReplacement Replacement)`; `RecoveryDetails(string Status,long ProtectionRevision,long Generation,long RevocationGeneration)`; `IVaultRecoveryStore.RecoverAsync(RecoveryRequest,CancellationToken)->Task<VaultResult<RecoveryDetails>>`; `VaultRecoveryOperation` entity accountId/key/digest43/counters. Existing challenge overload extends recover in Task2.
- [ ] Write metadata/safe integer/key role/fingerprint/slot/generation/fresh-context/private JSON cases; absent-types RED.
- [ ] Implement pure strict contracts and material replacement preserving retained pins, no server secrets. Full Domain GREEN; commit/ledger.

### Task 2: Atomic recovery and durable idempotent outcomes

**Files:** Data/Protection/VaultRecoveryStore.cs, AppDbContext.cs/additive VaultRecoveryOperations EF migration/snapshot; existing challenge store; Data.Tests/VaultRecoveryStoreTests.cs.
**Interfaces:** Task1 request/result/store/entity; existing challenge overload accepts recover with current nonnull generation. No old caller signature break.
- [ ] Write real PostgreSQL main/no-session recovery/new generation/unchanged content; current ownership/state/freshness/revisions/overflow/pins/native purpose+body; different-key and exact-key concurrent use; durable retry after expiry/malleated proof/new store; changed body key conflict; natural proof/iat lock-wait expiry, queued lifecycle/policy; actual outage/cancel; post-consumption write rollback then same proof success; account deletion cascade. Observe missing-store RED.
- [ ] Implement account-first lock/fresh time; own idempotency lookup first and validated historical outcome; full old-key proof/parsed-body match and complete atomic replacement/counters/result. Generate/inspect additive migration; full Data GREEN; commit/ledger.

### Task 3: Typed recovery handler and strict POST route

**Files:** Command/Protection/RecoverVaultCommand.cs/Validator.cs/Handler.cs/Output.cs; challenge validator/handler/maps; WebApi VaultController.cs/Startup.cs; Command.Tests/VaultRecoveryHandlerTests.cs; WebApi.Tests/VaultRecoveryHttpTests.cs; OpenAPI/docs/README.
**Interfaces:** command public six fields mirror RecoveryReplacement; internal actor/iat/proof context SetContext; output exact RecoveryDetails four fields. Handler validates before store and validates projected historical counters. Existing signed-iat freshness helper and canonical proof header binding.
- [ ] Write command trusted-context/validation/projection/failure/cancel and recover-challenge generation cases; absent types RED, implement, full Command GREEN.
- [ ] Write real host init→recover→oldkey rejects→newkey recovery and exact lost-response retries, all AFs/current provider checks/strict wire; absent-route RED. Implement marked raw-proof route/DI/statuses200/400/401/404/409/503. Full WebApi GREEN.
- [ ] Generate/inspect OpenAPI, document recovery/retry/historical status/client-secret boundary, README UC40 Done M02 8/9. Full unfiltered coverage/helpers/spec/drift/diff; commit/ledger.
- [ ] One final ordinary review, one TDD correction pass for blockers, own PR closes41/all CI green/authorized merge/Done/archive/sync.
