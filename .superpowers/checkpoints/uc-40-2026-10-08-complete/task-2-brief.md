### Task 2: Atomic recovery and durable idempotent outcomes

**Files:** Data/Protection/VaultRecoveryStore.cs, AppDbContext.cs/additive VaultRecoveryOperations EF migration/snapshot; existing challenge store; Data.Tests/VaultRecoveryStoreTests.cs.
**Interfaces:** Task1 request/result/store/entity; existing challenge overload accepts recover with current nonnull generation. No old caller signature break.
- [ ] Write real PostgreSQL main/no-session recovery/new generation/unchanged content; current ownership/state/freshness/revisions/overflow/pins/native purpose+body; different-key and exact-key concurrent use; durable retry after expiry/malleated proof/new store; changed body key conflict; natural proof/iat lock-wait expiry, queued lifecycle/policy; actual outage/cancel; post-consumption write rollback then same proof success; account deletion cascade. Observe missing-store RED.
- [ ] Implement account-first lock/fresh time; own idempotency lookup first and validated historical outcome; full old-key proof/parsed-body match and complete atomic replacement/counters/result. Generate/inspect additive migration; full Data GREEN; commit/ledger.

