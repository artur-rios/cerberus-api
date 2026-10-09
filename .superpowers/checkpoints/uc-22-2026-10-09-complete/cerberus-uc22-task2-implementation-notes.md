# UC22 Task2 implementation notes before production

Task1 typed prerequisite DONE c5fc55df79b61c1e3ac533e637a6cfe61d7636e7,1493DataPASS.
Task2 started with BASEc5fc55d, brief read. Spec/plan binding;15th ruling configured
lease constructor,16th ruling exclude terminal RECORD pending bytes from protection
rotation inventory (retain recoverable trash). All copied to ledger/aggregate.

Task2 test files written BEFORE any product:
RecordPermanentDeleteStoreTests.cs(main+helper), RecordPermanentDeleteFailureTests.cs,
RecordPermanentDeleteConcurrencyTests.cs (partial same class), RecordPurgeHandlerTests.cs.
Initial and expanded RED logs permanent-red.log/permanent-red-2.log CS0246 missing
store/request. No Task2 Domain/Data/handler/replay product yet.
No.NETactive after87516 finishedexit1. Some planned remaining gaps still need tests
BEFORE product: new-unheld earlier association, safe sequence exhaustion/regression,
internal provider cancellation/DB unavailable, malformed relevant native overlaps/
hidden native, caller-after-intent, rotation waiting on deletion. Existing observed
RED compilation is expected missing API; do not mistake it for behavioral passes.
Small test-only corrections made: missing TestSupport import on Task1; current Task2
association entities have COMPOSITE keys (NO Id), Snapshot orders profile/collection
thenrecord; World.State now mutable and Request uses State; concurrency test creates
a NEW account-wide session rather than nulling an existing selected ProfileId.

Ctor required by tests/plan:
RecordPermanentDeleteStore(factory,ledger,CerberusOptions{RetentionInterval="00:00:30"},TimeProvider.System)
RecordPurgeHandler(factory,ledger):IRetentionHandler.Kind="record-purge".
Request(Actor,AccessVerifier,RecordId,ExpectedRevision), details(RecordId,DeletedAt).
No target revision increment; matching maximum safe revision is deletable.
Intent advances active direct parent metadata once, not target live revision.
Return200 only after actual fsynced ledger, physical delete and claim completion;
503/ct after committed intent remains irreversible pending work. Existing worker
runner completes claim separately; request uses same actual RetentionWorkStore.

Recommended implementation shape:
- DeleteAsync calls separate CommitIntentAsync transaction; on success transaction
  disposed/committed, claim queued work using configured RetentionInterval lease,
  invoke RecordPurgeHandler then actual TryComplete. Never hold authorization tx
  while filesystem ledger runs. Handle IOException/InvalidData/known claim error
  as503; caller cancellation propagates, internal cancellation503.
- Clone targeted current-authority/native/lock helpers from RecordTrashStore,
  purpose-built permanent SQL; don't parse/decrypt source ciphertext/client time.
  Current account/session/native/foreign403/hidden404 order remains that store's.
- Own accountNoKey→sessionUpdate→own direct profiles+selection GUIDNoKey→own direct
  collections GUIDNoKey→immediateactivefolderNoKey→recordUpdate→existing typedlinks.
  Reload current state after every wait and inside final guarded UPDATE; newly
  unheld earlier parents/links or changed parent409 after current classification.
- Final guarded target deleted_at transition accepts active or owned trash,
  expectedrevision+originalparent/links. Typed terminal marker and immediate
  retention work record-purge/GUID commit atomically with logical link/snapshot
  removal and active immediate/direct parent bumps. Terminal marker time is fresh
  DB statement UTCmicrosecond; response uses that time, no target revision bump.
- Delete typed record trash membership at intent; empty ROOT record trash operation
  can later be identified by root_resource_kind='record' and root_resource_id,
  and removed only if no entries. Preserve other-kind/nonempty cascade operations
  and their trash work. Physical helper idempotently deletes matching membership too.
- Physical worker validates existing typed terminal intent AND exact persisted
  work publicID/token/key/uncompleted/exclusive expiry before recording ledger.
  Record ledger BEFORE physical tx, then revalidate/fence EVERY mutation under
  current claim. Late expiry/theft writes ledger but leaves bytes for next claim.
  Initial forged/invalid claim writes NO ledger. Keep terminal marker forever.
  Worker lease token/id authoritative from persisted row, not caller ExpiresAt.
- RecordErasure common helper removes exact record and direct associations,
  exacttypedtrashentry/emptyrootrecordoperation+trashwork, never folders/collections/
  profiles/grants/otherrecords/otherkindGUID. Worker use claim fence; restore path
  uses trusted operator replay transaction before RestoreTrafficGate opens.
- TerminalErasureStore.ReapplyAsync after upsert invokes exact record physical
  erasure in same DB transaction (external ledger already read by RestoreReconciler).
  Other kinds keep typed fail-closed tombstone behavior; no selected ID nulling.

Selected trash current-scope implementation details to settle:
UC20 retained RecordAssociationSnapshot(FolderId,ProfileIds,CollectionIds), active
links now removed, record.folder_id=NULL. Account-wide owned trash can erase even
unused malformed snapshot. Selected must match an owned same-account trashoperation
entry and validate required snapshot strictly (actual3fields/arrays/IDs); direct
historical selected profile ID OR currently active owned historical collection
attached to selected OR current active complete retained-folder ancestry attached
to selected directly/through own collection. No foreign-grant admission. Missing
history/path404, malformed needed history503. Fresh selection itself active/owned/
nonterminal/current session mandatory; no blind link/grant restoration.
Avoid unnecessary old-folder corruption hiding a valid direct historical profile
or collection route: direct historical route can stand independently per spec.
For SQL adaptation, candidates permit OWNED trashed records, never foreign trash;
history CTE restricts to valid selected session/own typed operation. Use typed
snapshot exact-bytes guard at final intent and current organization recomputation.
JSON decoding only when required for selected trash (accountwide ignores it).
Can validate needed historical JSON in C# each fresh snapshot and pass GUID arrays
plus original bytes to final SQL, or strict guarded JSONB CTE. Keep target candidate
<=1, complete visited upward ancestry/cycle handling; no broad inventory scan.

Rotation integration:
VaultProtectionChangeStore currently selects ALL owned records including trash,
even already typed terminal pending record. Add record-kind terminal exclusion to
record rotation inventory only; recoverable trash remains included. Two tests
TerminalRecordAwaitingLedger require OMIT terminal -> success, INCLUDE terminal
extra ->existing validation_failed with no proof consumption. Pending terminal
cipher/revision unchanged; surviving four-item inventory rotates to epoch2.
Other future terminal kinds need equivalent inventory integration in their UCs.

Tests requiring controlled interception:
BeforeIntent matches UPDATE cerberus.record NonQuery; BeforeTarget matches FROM
cerberus.record plus FOR NonQuery. CaptureTarget reads first WITH RECURSIVE snapshot
and clones params into real EXPLAINANALYZE; candidates scan0..1/exact1. Finalizer
post-delete fault matches DELETE FROM cerberus.record NonQueryExecuted. Keep these
real boundaries (can fix test interceptor if actual authorized implementation uses
equivalent driver method; document test-only correction, never weaken assertion).

Current test World creates owner native fixture+account, unrelated item record,
folder+collection, separate target; routewide/direct/folder/collection/descendant;
selected actual own profile setup before optional UC20trash; then session selects
profile. Class instance private real ledger dir. Snapshot captures full owned
records/profiles/folders/collections/links, alltrashentries/terminal/work to prove
no mutation; real DB testcollection serialized. Existing wholeData baseline1493.

## Resume update
Domain contracts, RecordPermanentDeleteStore, RecordPurgeHandler, RecordErasure and record replay now exist UNCOMMITTED. Five test files (including Boundary) observed compile RED before product. First production compile found nullable lease and SQL analyzer expression errors; corrected. No behavioral test PASS yet. Rotation production untouched, and separate Fence SELECT still needs same-statement mutation fencing regression.

## Current authoritative update
Task2 complete141focusedPASS after actualRED6mutation/rotation,4history/completion,2unusedclienttimestamp. New RecordPermanentDeleteMatrixTests six totalDatafiles; all native/selectedtrash/parent/race/expiry/rollback/fsync/backup tested. RetentionWorkStore record-purge completion now DBtime fenced (17thRuling); otherretentioncontractsunchanged. SnapshotItem nowidentityonly ignoresdamagedclienttime. TerminalRecord excludedrotation,trashretained. Current fresh final wholeData ACTIVEsession69655 permanent-data-final.log expected1634, not claimeduntilactualsummary. Task2 remainsuncommitted BASEc5fc55d; noother.NET. Task3commandtest/tmp/cerberus-uc22-command-tests.cs andTask4HTTPtest/tmp/cerberus-uc22-http-tests.cs prepared outsideworktree only; doNOTinstalluntilrespectiveTaskstart/brief. Earlier no-product notes are historic, not current.

## Tasks1–3 complete / Task4 coverage active
Task2 committed244d33c1634DataPASS/taskdone; Task3committedc11cad966new/783CommandPASS/taskdone. Task4actualHTTP107PASS afterobserved105FAIL/2PASS missingroute, route+DI/currentcontext+purgehandler nowuncommitted. Docs andgeneratedOpenAPIupdated; allordinarynative/audit/helper/specchecksPASS. NewlycopiedHTTPblank183 removedBEFOREreview tofixfullcachedrangewhitespace exit2; range5c994f6nowPASS, UC21oldformattingMinoruntouched. Freshfull6familycoverage ACTIVEsession6881 expected4596(log /tmp/cerberus-uc22-coverage.log); helper/tmp/cerberus-uc22-verify-coverage.py validatesall6counts/actualSummary. NOother.NET/nativebuilduntilthisfinishes. FinalTask4commit/taskdoneBASEc11cad9/ONEreview/remoteCIcloseoutstillpending, noPR/reviewernow. TempdraftHTTPfile stillhashistoricblankspaces: doNOTreinstall/rerungeneratorovercurrenttest. Whitelistednativeenvknownpaths /tmp/cerberus-protocol.fCQIq1/{maven-user,m2,wheels,maven.zip,venv/bin/python}; neverdumpwholeenv.
