### Task 2: Current transactional folder-only update

**Files:** Create `src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderUpdateStore.cs`; test `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/FolderUpdateStoreTests.cs`.
**Interfaces:** Consumes Task1 IFolderUpdateStore/Request/Input/FolderCreateDetails; produces `FolderUpdateStore(IDbContextFactory<AppDbContext>)`. Reuses CollectionGrantBinding and unchanged schema.

- [ ] Write actual PostgreSQL tests based on RecordUpdateStoreTests with folder-specific target+self ancestry, direct/ancestor PF and CF scopes, actual native RO/RW at root/target/descendants, private parent/sibling and record-only negatives FIRST proving RecordReadStore success. Assert only target content/rev/seq/stamp/time changed, current links/parent/children/unrelated snapshots unchanged. ReviewFocus1/5.
- [ ] Cover all current session/selection/typed terminals; hidden corrupt target stale404; every lock-wait natural expiry/current permission revoke/downgrade/member/move/rotation, final guard movement/trash/terminal/expiry and new authority growth409. All contributor signatures/pins/strictnumeric/native failures, unselected/unrelated isolation; safe counters/envelope/time/revision/keyepoch/salt/nonce, post-write fault rollback, exhausted/regressed sequence, caller/internal cancellation. ReviewFocus2/3/5.
- [ ] Add concurrent same-revision owned/recipient exactly-one-winner/lost-retry, reciprocal foreign folder updates, ACTUAL FolderCreateStore interleaving with target lock held before upper ancestor lock (legitimate wait, then winning parent revision409), native owner rotation races. EXPLAIN target1+selfactualancestry against100 unrelated folders; assert exactly target folder lock/no foreign accounts/profiles/upwardlocks. ReviewFocus4.
- [ ] Run focused FolderUpdateStoreTests to data-red.log. Expected missing FolderUpdateStore before implementation; no fixture syntax failure.
- [ ] Implement record-update transaction adapted to UC25 folder-only authority; remove all PR/CR routes, include target itself in ancestry, require complete null-parent terminus. Ordered own/collection/grant/target locks, current reload and all native contributors, final full guarded UPDATE; only folder target row metadata; rollback+safe errors.
- [ ] Run focused data-green.log then whole Data data-whole.log via `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --no-restore`. Expected all pass, true race/EXPLAIN assertions.
- [ ] Commit `feat: update permitted encrypted folders`; task-done actual whole Data log and mark steps/ledger.

