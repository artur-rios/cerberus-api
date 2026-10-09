### Task 2: Authorized terminal intent and durable fenced record purge

**Files:** New Domain Records/RecordPermanentDeleteContracts.cs; Data Records/
RecordPermanentDeleteStore.cs, Erasure/RecordPurgeHandler.cs, Erasure/RecordErasure.cs;
modify TerminalErasureStore record replay. New Data RecordPermanentDeleteStoreTests.cs
and RecordPurgeHandlerTests.cs/restore cases. Reuse existing retention schema.
**Interfaces:** RecordPermanentDeleteRequest(Guid Actor,string AccessVerifier,
Guid RecordId,long ExpectedRevision); RecordPermanentDeleteDetails(Guid RecordId,
DateTimeOffset DeletedAt); IRecordPermanentDeleteStore.DeleteAsync(request,CT)
returns VaultResult<details>. RecordPurgeHandler implements IRetentionHandler Kind
"record-purge"; common record erasure removes only exact typed data, fenced by
current claim for workers, operator-gated restore replay before traffic for restore.

- [ ] Write RED tests all main/AF/current owner/native/selected active+trash scope,
  malformed restricted snapshot/no content parse, safe/stale/max revisions/parent
  capacities, typed collision survival, structural bumps exactly once, no selection
  nulling; locks/expiry/final current scope, newly unheld authority, current actual
  edit/trash/move/create/association/rotation winner races and target query plan.
  Intent rollback faults; after-intent ledger outage keeps all reads/writes denied
  and queued work; true fsynced ledger precedes removal; after-ledger/post-delete
  crash retries; actual claim expiry/theft fence all mutations/completion; nonempty
  cascade and other-kind trash survive; caller/internal cancellation/outages.
  Actual backup before deletion then restore/replay removes record but same-GUID
  other-kind survives and corrupt ledger/DB keeps gate closed. Expected missing
  contracts/store/handler compile RED before product.
- [ ] Add minimal contracts/store/finalizer/replay; focused GREEN then wholeData
  zero failures/skips, inspect real races/faults/ledger/backup evidence. Commit
  `feat: permanently erase owned records`; task-done audits fresh wholeData log.

Task command: same wholeData; focused permanent/purge/restore tests. All Task1
typed prerequisites must already be committed/verified before physical purge code.

