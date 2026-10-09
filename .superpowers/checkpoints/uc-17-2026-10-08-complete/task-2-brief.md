### Task 2: Single-snapshot permitted record list store

Create Data/Records/RecordListStore.cs and focused internal SQL/snapshot helper only
if needed. Parameterized one SQL statement computes actor/account/current session,
selected direct/folder/collection scope, current owned/foreign grant visibility,
typed membership and dynamic folder ancestry with visited-ID cycle termination,
deduplicated permitted inventory, corrupt ordering before keyset filter, boundary,
bounded page and HasMore. Snapshot native grant/pin evidence in that same statement;
reuse CollectionGrantBinding for relevant in-scope foreign grants, no later DB reads.
Only five record output fields leave Data; no plaintext/raw handles/organization.

Write Data/RecordListStoreTests.cs first: owner unlinked/multiple profiles, selected
direct/descendant and selected owned/shared collection scope, native RO/RW direct and
folder descendant shares, same-owner collection membership, overlapping route dedup,
wrong recipient/revoked/terminal grants/hidden or closing owner/trashed collection
and content omitted before pagination, hidden ancestor and relevant cycle behavior,
outside-scope corrupt rows/grants not inspected, relevant bad native signature/pins/
epoch/binding503, active selected handle and all session/lifecycle failures, empty
list, boundary/gaps/page limit/HasMore and inserts after boundary, revoke grant/link
or session between pages, permitted zero/unsafe/duplicate sequence503 beforeTake,
read no-mutation snapshots, one intercepted reader command/no N+1, controlled
post-statement revocation preserves that one snapshot then next page reflects it,
pre-execution expiry, unavailable DB/missing table/current caller cancellation.
Expected RED: missing RecordListStore; GREEN whole Data. Commit.
Interfaces: consumes Task1 schema/contracts, returns RecordListPage with strictly
current SQL visibility/native-grant validation; Task3 handles public projection/cursor.

- [ ] Write specified failing tests, run and read expected RED before product code.
- [ ] Implement this task, run named whole-family verification, read zero failures/skips and commit.

