### Task 2: Hold current account permission across external update

**Files:** Data `Identity/IdentityUpdateStore.cs`; Data `IdentityUpdateStoreTests.cs`.

**Interfaces:** Consume Task1 authorized-callback store contract/result; no encrypted account bytes are retrieved; no local state writes.

- [ ] Write PostgreSQL tests for valid/no-expiry callbacks, all account/session denials without callback, lock contention and access changed before guard, expired-after-wait boundary, callback provider errors/response loss, database outage/cancellation, unchanged vault state and account/session lock blocking. Observe missing-store RED.
- [ ] Implement transaction locking account then matching session without ciphertext projection. Validate all access predicates using statement_timestamp() after locks; invoke callback only on success; commit/release locks, propagate typed failures/cancellation. Run full Data suite GREEN. Commit.

