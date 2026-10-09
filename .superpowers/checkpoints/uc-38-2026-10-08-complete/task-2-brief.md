### Task 2: Atomic PostgreSQL initialization and account unlock store

**Files:** Data Protection/VaultProtectionStore.cs, AppDbContext.cs, additive migration; Data.Tests VaultProtectionStoreTests.cs.
**Interfaces:** Task1 store/request/result/entity contracts, original body bytes and registered verifier only.

- [ ] Write real PostgreSQL cases: own bootstrap success/unchanged account, hidden/foreign/closing/erased, stale/concurrent init one winner; opaque material read; bound60s challenge without session; correct unlock hashed-only session/current policy/no-expiry; wrong actor/body/proof/missing/consumed/expired/future/stale challenge; concurrent consume one winner; after-lock expiry/lifecycle mutation, outage/cancellation and rollback. Observe missing-store RED.
- [ ] Implement account-first locking and fresh statement_timestamp decisions; single transaction writes; migration one protection/account and unique challenge GUID with cascades. Run complete Data suite GREEN. Commit.

