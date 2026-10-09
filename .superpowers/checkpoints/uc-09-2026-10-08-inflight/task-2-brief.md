### Task 2: Transactional profile persistence and complete rotation

**Files:** Data/Profiles/ProfileCreateStore.cs; AppDbContext/migration; VaultProtectionChangeStore; Data.Tests/ProfileCreateStoreTests.cs, ProfileRotationTests.cs.
**Interfaces:** IProfileCreateStore as Task1; global cerberus.server_sequence. Existing IVaultProtectionChangeStore adds exact profile inventory and writes after existing native proof validation.
- [ ] PostgreSQL creation/access variants/relationships/ID collision+tombstone/lock expiry/concurrency/outage/cancel/rollback and complete rotation/retained bytes RED.
- [ ] Implement account/session locking and statement-time INSERT authorization, strict trusted owner-wrapper validation, unique conflict mapping, sequence/migration. Extend atomic rotation validation and writes. Full Data GREEN, commit/ledger.

