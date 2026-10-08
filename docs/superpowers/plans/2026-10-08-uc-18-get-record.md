# UC18 Get Record Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Goal: get one accessible native encrypted record with only current visible public
relationships. Spec: docs/superpowers/specs/2026-10-08-uc-18-get-record.md.
Architecture: targeted one-SQL current authority/ancestry/visible references/native
grant snapshot; strict existing content projection; GET route and explicit query DI.
Stack: existing .NET10/EF10/PostgreSQL18.6/native crypto/mediator; no dependency/schema
change. New isolated feature/uc-18-get-record from fresh fully integrated UC17 develop.

Global Constraints: preserve primary user checkout; one issue19/branch/PR; TDD before
product, no property-holder tests; one SQL/no read mutation/foreign locks/N+1; current
native grants/selected authority and hidden-before-corruption; no owner graph/plaintext.
UTC >=10ticks/microsecond/native safe metadata; global terminal GUID conservative
until required typed repair before physical purge; never purge-null selection.
Existing all-issues authorization replaces repeated approval menus; independent
security/client approvals development-only deferred, release gates remain strict.
Whole-family tasks/full unfiltered six-family >=90line/zero failure/skip/branchreport,
native/dependency/helper/spec/OpenAPI/diff verification; one ordinary final reviewer/
one blocking TDD fixpass/all decisions costs; exact-head requiredCI normal mergeDoD.

Review Focus: target lookup and complete ancestry must remain bounded to one potential
record and never traverse unrelated tenant inventories; direct-record admission must
not disclose an unselected/unshared parent folder or owner profiles; all current
permission routes and visible references/native evidence must share a SQL snapshot;
hidden target/ancestor/native corruption or cycles must remain nonrevealing404 unless
currently relevant; alternate malformed stored/native/metadata/reference shapes must
failwhole503, never partial data or permissive nulls. Add corresponding focused tests
in the owning task and review larger/unmodeled variants deliberately.

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

### Task 2: Strict get-record query and output

Create Query/Records/GetRecordQuery.cs/GetRecordHandler.cs/RecordDetailsOutput.cs/
RecordReadMessages.cs. Reuse RecordProjection for five fields and validate exact
requestedtarget, distinct/nonzero non-nullprofile/collection arrays, optionalnonzero
parent; exact8publicfields. Context validation pre-store, actualhandlehash/token,
safeerrorallowlist, null/errorwithdata/unknown failures503, callerctpropagation.

Write Query/GetRecordHandlerTests.cs first: actor/missingorbadhandle/zeroID pre-store;
forwardhash/request/ct; exactnative5fields+3visibilityrefs includingemptyarrays/null
parent andsameGUIDacrosskinds; nullresult/data/record/arrays/envelope, targetmismatch,
duplicate/zero refs/parent; unsafe/default/offset/submicro metadata, nativeunknown/
duplicate/case/numericstring/badformat/epoch/binary; safeerrors/unknown/errorswithdata/
successaserror/authasstoreerror failclosed/no partialoutput; cancellation. Expected
RED missingGetRecordQuery/Handler, then wholeQueryGREEN. Commit.
Interfaces consumes Task1, exposes QueryMediator handler/output to Task3. No cursor
or new public owner/protection/native grant fields.

- [ ] Write specified failing tests, run and read expected RED before product code.
- [ ] Implement this task, run named whole-family verification, read zero failures/skips and commit.

### Task 3: HTTP record get and complete delivery

Add GET/api/records/{id} to RecordsController, strict canonicalDnonzeroUUID/rawsingle
header/noquery/body/transferencoding, currentactorcontext, statusannotations including
commonempty413, explicitStartupReadStore/GetRecordHandler DI. Tests Web/RecordReadHttpTests
FIRST actualUC16creates/UC15nativeMasterandPerProfilehandles/validnativeRO/RWreceived
grants; owned/multidirectprofile/selected/direct+descendant/collection scopes; exact
8fieldbody/noforeignprofile/unselectedparent leakage; dynamicrefs/revokedlinks/grants/
currentselected/account/owner/identity/sessionstates; nativecorruptrelevant503 vs
hidden404/cycles; actualnativeenvelopebytes/metadata no mutation/no-store; allcanonical
ID/header/unknownquery/GETbody/empty413 failures; DBandidentityprovider503. Expected RED
missingGET{id}404, then focusedHTTP/wholeWebGREEN.

Update recordAPI/operators/READMEUC18Done/M03ten/Unreleasedchangelog; generate/check
actualOpenAPIexact8fields/statuses/route/header; truthfullytrackknownsharedgenerator
minors. Lockedrestore/NuGet/nativeaudits/corpus322/36harness/23helpers/spec/diff/full
unfilteredsixfamilycoverage>=90line/branchreport/zero failures-skips. Commit/taskdone.
Onefreshordinaryfinalreview/oneTDDblockingfixpass/fullGREEN, exhaustive declinedscope
reasoncosts+minorledger/aggregate/PRbody. PushPR/verifiedTesting; exact-head latest3CI
SUCCESS/MERGEABLE+CLEAN/freshunchangedtestedbase normalmatchheadsquashmerge; issueclose/
Done/9checkbox/remoteabsent/developtestedtree/primarySHA/archiveonlyownscratch. UC19next.

- [ ] Write specified failing tests, run and read expected RED before product code.
- [ ] Implement this task, run named whole-family verification, read zero failures/skips and commit.

Verification: Task1wholeData,Task2wholeQuery,Task3focusedHTTPthenwholeWebandfullcoverage.
Serialize shared .NET commands. Nativecache /tmp/cerberus-protocol.fCQIq1. Just-completed
unchanged whole-family/full-suite logs can be audited for task-done without rerunning.
Baseline must be fresh exact UC17 merged/tested tree; no unchanged baseline rerun.

Exact verification commands (from repository root):
- Task 1: `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --logger "console;verbosity=minimal"` (all Data tests, no filter).
- Task 2: `dotnet test tests/Application/ArturRios.Cerberus.Query.Tests --logger "console;verbosity=minimal"` (all Query tests, no filter).
- Task 3: `dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests --logger "console;verbosity=minimal"`, then `python3 scripts/coverage.py` (all six families).
Expected: completed command exit0, all tests passed, no failures/skips; final merged production line >=90%.
Focused RED/GREEN uses `--filter FullyQualifiedName~RecordReadStoreTests` / `~GetRecordHandlerTests` / `~RecordReadHttpTests` only during development.
File roots: Domain=`src/Domain/ArturRios.Cerberus.Domain`; Data=`src/Infrastructure/ArturRios.Cerberus.Data`; Query=`src/Application/ArturRios.Cerberus.Query`; Web=`src/Presentation/ArturRios.Cerberus.WebApi`; test roots mirror these under tests.
