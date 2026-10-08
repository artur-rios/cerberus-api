# UC-38 Initialize Vault Protection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: superpowers:executing-plans inline, TDD and one fresh final whole-branch review under existing batch authorization.

**Goal:** Deliver issue39, opaque owner-bound vault protection and usable account unlock proofs.

**Architecture:** Domain validates strict wrappers/public key roles and native proofs; Data serializes account initialization/challenge consumption; Command/Query and WebApi bind current actor and original request bytes.

**Tech Stack:** Pinned .NET10/EF PostgreSQL, native ECDsa/SHA256, existing mediator/output/FluentValidation/xUnit/Moq/Testcontainers tooling; no new crypto dependency or server decryption.

**Spec:** docs/superpowers/specs/2026-10-08-uc-38-initialize-vault-protection-design.md.

## Global Constraints

- Preserve all account ciphertext/revision/policy; own active non-erased account only.
- Strict public GUID/safe integer/base64url/schema contracts; reject raw secrets/private JWK fields.
- Initial key/protection/recovery revisions1; exact protocol formats and Argon2 metadata65536/3/4/16 bytes.
- Distinct P256 unlock/recovery/recipient/author keys; recovery thumbprint binding.
- Native verifier only, no plaintext/KDF/decryption; original raw request digest, single proof headers.
- Challenges exactly60 seconds and one committed use; signed fresh iat within60 seconds; sessions store SHA256 verifier only and current policy/generation.
- No session issued by init/login/challenge; account unlock proof issues account-wide session; UC15 remains selected profile.
- Main and AF01–06 all mapped; protocol review deferred only for development, pending release gate unchanged.
- Full unfiltered zero-failure/skip suite,>=90% line/report branch, fresh final review/all required CI before merge develop.

## Review Focus

- Valid-looking but invalid/off-curve/private/role-reused public keys must fail before persistence.
- Concurrent initialization must preserve one complete wrapper/verifier winner without changing vault data.
- A queued unlock must recheck current lifecycle/policy/revision/time after acquiring account locks.
- A valid proof for another actor/body/challenge/scope or a consumed/expired challenge must issue no session.
- Raw byte hashing and native signature verification must match independent existing harness wire vectors; no reconstructed JSON signing.

### Task 1: Strict protection metadata and native proof contracts

**Files:** Domain Protection/ProtectionMaterial.cs, PublicJwk.cs, VaultProof.cs, contracts/entities; Domain.Tests ProtectionContractTests.cs and VaultProofTests.cs.
**Interfaces:** `ProtectionMaterial.IsValid()`, `PublicJwk.IsValid()/Fingerprint()/Verify(byte[],byte[])`; `VaultProof.SigningBytes(VaultProofChallenge)` and `Verify(VaultProofChallenge,VaultProofBinding,byte[],PublicJwk,string,long)`. All metadata records use strict unknown-member rejection. `VaultResult<T>(T? Data=null,string? Error=null)` for class T. `VaultProtectionDetails(Guid AccountId,long ProtectionRevision,long KeyEpoch,long RecoveryGeneration,ProtectionMaterial Material)`, `VaultAccessDetails(Guid AccountId,string Access,DateTimeOffset IssuedAt,DateTimeOffset? ExpiresAt)`.
`IVaultProtectionStore.InitializeAsync(Guid actor,Guid accountId,long expectedAccountRevision,ProtectionMaterial material,CancellationToken) -> Task<VaultResult<VaultProtectionDetails>>`; `ReadAsync(Guid actor,CancellationToken)` same result; `ChallengeAsync(Guid actor,string requestHash,CancellationToken) -> Task<VaultResult<VaultProofChallenge>>`; `UnlockAsync(Guid actor,long expectedProtectionRevision,Guid challengeId,string proof,byte[] rawBody,CancellationToken) -> Task<VaultResult<VaultAccessDetails>>`. `VaultProtection` entity stores AccountId/Revision/KeyEpoch/RecoveryGeneration/opaque Material bytes; `VaultUnlockChallenge` stores AccountId/PublicId/opaque Challenge bytes/PolicyRevision/RevocationGeneration/Consumed/ExpiresAt.

- [ ] Write tests covering exact wrappers/KDF/key roles, safe integer/base64 bounds, private/unknown JSON, off-curve keys and fingerprints; native known-answer/wire verification and each binding/body/expiry mutation. Observe missing-types RED.
- [ ] Implement contracts with native ECDsa P256/SHA256/P1363 verification and exact protocol array UTF8 encoding, no decrypt/sign/KDF in production. Run full Domain suite GREEN. Commit.

### Task 2: Atomic PostgreSQL initialization and account unlock store

**Files:** Data Protection/VaultProtectionStore.cs, AppDbContext.cs, additive migration; Data.Tests VaultProtectionStoreTests.cs.
**Interfaces:** Task1 store/request/result/entity contracts, original body bytes and registered verifier only.

- [ ] Write real PostgreSQL cases: own bootstrap success/unchanged account, hidden/foreign/closing/erased, stale/concurrent init one winner; opaque material read; bound60s challenge without session; correct unlock hashed-only session/current policy/no-expiry; wrong actor/body/proof/missing/consumed/expired/future/stale challenge; concurrent consume one winner; after-lock expiry/lifecycle mutation, outage/cancellation and rollback. Observe missing-store RED.
- [ ] Implement account-first locking and fresh statement_timestamp decisions; single transaction writes; migration one protection/account and unique challenge GUID with cascades. Run complete Data suite GREEN. Commit.

### Task 3: Typed application handlers and protected HTTP routes

**Files:** Command Protection commands/validators/handlers/outputs/maps; Query Protection read handler/output; WebApi VaultController/DI/raw body capture; mirrored Command/Query/WebApi tests.
**Interfaces:** init accountId/expectedAccountRevision/material; challenge operation/requestHash; unlock expectedProtectionRevision plus trusted internal actor/proof headers/raw bytes; read actor only.

- [ ] Write handler tests for validation/trusted actor/context/statuses/incomplete store results/cancellation; observe RED, implement and run Command/Query GREEN.
- [ ] Write real host main init/read/challenge/native-sign/unlock/account-read and all AFs, strict JSON/headers/query/encoding, current identity/fresh signed iat, ownership isolation and hashed-only persistence. Observe absent-route RED; implement strict controllers/DI/body capture, then full WebApi GREEN.
- [ ] Generate/inspect OpenAPI, document client plaintext contract and bootstrap/unlock/expiry/retry/protocol-deferral behavior, mark only UC38 Done and M02 6/9. Full unfiltered coverage/helpers/spec/OpenAPI/diff checks, commit/ledger.
- [ ] One fresh final review; one RED→GREEN correction pass for Critical/Important if needed; own PR closing39, all required CI green, authorized merge/close/projectDone/sync/archive.
