### Task 1: Typed collection membership schema and read contracts

Create Domain/Resources/CollectionRecord.cs and CollectionFolder.cs; add DbSets and
same-owner composite FKs in Data ResourceModel, unique typed collection/target keys,
cascade association rows only. One additive CollectionMembership migration/snapshot.
Create Domain/Records/RecordListContracts.cs with the exact spec request/row/page/store
interfaces; no property-holder getter tests.
Tests Data/CollectionMembershipSchemaTests.cs first: native owned records/folders and
collections, both typed memberships including the same public GUID across kinds,
duplicate typed tuple rejected, foreign record/folder owner rejected, mixed AccountId
forged owner rejected, target or collection deletion removes only appropriate links,
unrelated resources preserved. Upgrade an isolated temporary database on the fixture
server from the previous migration with existing native resource rows, then verify
new membership constraints and byte-for-byte old content/metadata preservation.
Expected RED: missing member types/DbSets; GREEN whole Data. Commit.
Interfaces: produces collection-member persistence consumed by Task2; read contracts
consumed by Task2 store/Task3 Query. No public membership endpoint (UC34 later).

- [ ] Write specified failing tests, run and read expected RED before product code.
- [ ] Implement this task, run named whole-family verification, read zero failures/skips and commit.

