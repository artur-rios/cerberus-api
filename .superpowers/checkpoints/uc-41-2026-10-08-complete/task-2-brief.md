### Task 2: Current-session authorized atomic refresh

**Files:** Data/Protection/VaultRecoveryStore.cs, VaultProtectionStore.cs; Data.Tests/VaultRecoveryRefreshStoreTests.cs.
**Interfaces:** shared RecoverAsync(RecoveryRequest,ct); operation selects old recovery/current unlock key and generation binding. Challenge accepts refresh-recovery with null generation. Reuse unique outcomes; no migration.
- [ ] Real PostgreSQL refresh/new recovery/old credential rejection; denied session variants, wrong key/purpose/body, lifecycle/revision/policy/overflow, lock-wait account/session/proof/iat expiry, concurrent same/different-key, historical replay/purpose collision, actual outage/cancel and post-consumption rollback. Observe unsupported purpose RED.
- [ ] Implement account-first/current session locks and statement-time consume checks, purpose-selected native key, atomic replacement/outcome. Full Data GREEN, commit/ledger.

