### Task 1: Targeted record store and current visible relationship snapshot

Create Domain/Records/RecordReadContracts.cs with exact request/details/store signatures
from spec; reuse RecordListRow for five fields. Create Data/Records/RecordReadStore.cs,
using CollectionGrantBinding and one typed raw SQL snapshot with currentactor/session/
selection/eligiblecollections, target-by-publicID (<=1candidate), visited upwardfolder
ancestry, exact permission, visible profileIds/folderId/collectionIds and relevant
native evidence. Snapshot single owner/recipient protection rows once, validate each
once then all relevant native grants. No additional query/write/lock/migration.

Write Data/RecordReadStoreTests.cs first: owned unlinked/multipleprofiles/accountwide;
selected direct/folder descendants/ownedcollection only; foreign native RO/RW direct/
folderdescendants with selected linkedcollection; overlaps no duplicate refs; foreign
ownerprofiles alwaysempty; selectedotherprofile omitted; parentnullable whenonlydirect
record admission, included whenprofileFolder/collectionFolder coversit; allactual
eligiblecollections only; active typed sameGUID refs; hidden/trash/terminal target/
ancestor/owner/collection/grant omitted404 beforecorruptinspection; currentaccount/
session/selection/expiry/policy/generation/dependency/cancellation errors; relevant
signature/epoch/revision/identity/owner/pins/material corruption503 wholepayload;
relevantactivecycle503/unselectedorhidden-cycle404; otherrecordcorruptmetadata/order
ignored. No mutation snapshots, actualone-reader SQL and controlled post-statement
revocation/reference removal keeps firstsnapshot then nextgetchanges; deadlinebefore
SQL403. Captured EXPLAIN ANALYZE with unrelatedtenant/unselecteddeepinventory bounds
candidate<=1 andonlytargetancestry. Expected RED missing RecordReadStore, then GREEN
whole Data. Commit.
Interfaces produces IRecordReadStore.ReadAsync(RecordReadRequest,ct), Details(Row,
ProfileIds,FolderId,CollectionIds) to Task2; Task3 HTTP uses samecontract.

- [ ] Write specified failing tests, run and read expected RED before product code.
- [ ] Implement this task, run named whole-family verification, read zero failures/skips and commit.

