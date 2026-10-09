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
