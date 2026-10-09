### Task 2: Atomic protection replacement store

**Files:** Data/Protection/VaultProtectionChangeStore.cs; existing VaultProtectionStore challenge issuance overload; Data.Tests/VaultProtectionChangeStoreTests.cs.
**Interfaces:** Task1 IVaultProtectionChangeStore/request/result. Existing IVaultProtectionStore old and new ChallengeAsync signatures. Native VaultProof.Verify with current change-protection binding and raw bytes. No schema change required.
- [ ] Write real PostgreSQL cases for rewrap preserving ciphertext/revision and rotation replacing complete account envelope; old session invalidation; all permission/state/revision/key/inventory/proof mutations; challenge-purpose mismatch both directions; one concurrent winner; unchanged challenge/session expiry across account/session locks; queued lifecycle/policy mutations; outage/cancel/injected save rollback; observe missing-store RED.
- [ ] Implement account-then-session locking/fresh SQL time; validate current metadata/pins/inventory; atomic challenge consumption including fresh session expiry; complete writes with revocation increment. Extend existing challenge creation to exact unlock-account/change-protection only. Full Data GREEN and commit/ledger.

