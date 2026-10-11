# UC-30 task 1 implementation report

Status: DONE_WITH_CONCERNS (implementation and required fresh full verification
complete; only independently reviewed nonblocking refinements and previously
tracked release qualifications remain with the controller).

## Change

Implement the single collection-list read operation through domain contract,
query/cursor/projection, one PostgreSQL snapshot store, strict GET and DI.
Return exactly five encrypted item fields, with stored KeyEpoch internal only.
Authorize wide own/native RO/RW and exact selected ProfileCollection links before
pagination; validate all visible foreign native evidence including nonpage and
highwater-excluded evidence; omit hidden/out-of-selection evidence. Do not traverse
content, acquire resource locks, reread authority, or mutate state.

## Files

- src/Domain/ArturRios.Cerberus.Domain/Collections/CollectionListContracts.cs
- src/Application/ArturRios.Cerberus.Query/Collections/ListCollectionsQuery.cs
- src/Application/ArturRios.Cerberus.Query/Collections/ListCollectionsHandler.cs
- src/Application/ArturRios.Cerberus.Query/Collections/CollectionListCursor.cs
- src/Application/ArturRios.Cerberus.Query/Collections/CollectionListOutput.cs
- src/Application/ArturRios.Cerberus.Query/Collections/CollectionProjection.cs
- src/Application/ArturRios.Cerberus.Query/Collections/CollectionListMessages.cs
- src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionListStore.cs
- src/Presentation/ArturRios.Cerberus.WebApi/Controllers/CollectionsController.cs
- src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs
- tests/Application/ArturRios.Cerberus.Query.Tests/ListCollectionsHandlerTests.cs
- tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionListStoreTests.cs
- tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionListHttpTests.cs
- docs/security/collection-api.md
- docs/contracts/openapi.json

## Commits

Feature commit: 2c4bca6e8368be7ce1a3b607366558d644dab099
Subject: `feat: expose scoped collection listing`.
Base task commit: 1d01abd.

## RED/GREEN evidence

All tests follow existing xUnit family UnitFact/UnitTheory and
FunctionalFact/FunctionalTheory with Given/When/Then names, native fixture signatures
and real PostgreSQL/HTTP fixtures. External persistence is mocked only in query
unit tests; production code is exercised throughout.

Query RED: `dotnet test tests/Application/ArturRios.Cerberus.Query.Tests/ArturRios.Cerberus.Query.Tests.csproj --filter FullyQualifiedName~ListCollections --no-restore -m:1`
Log: /tmp/uc30-query-red.log. Missing domain/query feature types produce compile
failure, as specifically requested by brief. Add implementation after observing
RED. Initial green attempt exposed a copied test field-name literal (`folderId`),
corrected to the spec's sorted `collectionId` expectation without changing behavior.
Final query GREEN: 101 passed, zero failed/skipped; /tmp/uc30-query-green.log.

Store RED: `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ArturRios.Cerberus.Data.Tests.csproj --filter FullyQualifiedName~CollectionList --no-restore -m:1`
Log: /tmp/uc30-store-red.log. Only missing CollectionListStore produces compile
failure. Add store after observing RED. Store GREEN: original 81 passed, zero
failed/skipped; /tmp/uc30-store-green.log. Strengthened current signed epoch/revision,
input guards and caller cancellation add ten scenarios; final focused run below
passes 91 store tests.

HTTP RED: `dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests/ArturRios.Cerberus.WebApi.Tests.csproj --filter FullyQualifiedName~CollectionList --no-restore -m:1`
Log: /tmp/uc30-http-red.log. 66 expected failures and 5 preexisting middleware checks
pass; new GET calls return 405 before route implementation. Add GET and registrations
after observing RED. HTTP GREEN: 71 passed, zero failed/skipped;
/tmp/uc30-http-green.log.

Final focused command:
`dotnet test src/ArturRios.Cerberus.sln --filter 'FullyQualifiedName~ListCollectionsHandlerTests|FullyQualifiedName~CollectionListStoreTests|FullyQualifiedName~CollectionListHttpTests' --no-restore -m:1`
Log: /tmp/uc30-focused-all-green.log. Query 101 + Store 91 + HTTP 71 = 263 passed,
zero failed/skipped. Other assemblies report no matching cases for this deliberate
focused filter. No build warnings or errors.

`python3 scripts/openapi.py --write` regenerated actual endpoint metadata;
`python3 scripts/openapi.py` confirms contract current, exit0;
/tmp/uc30-openapi-check.log. `git diff --check` passes.

## Full-suite evidence

Fresh unfiltered command: `PATH=/root/.dotnet/tools:$PATH python3 scripts/coverage.py`
completed exit0 on 2026-10-11 at 19:13 UTC. Log: /tmp/uc30-full-coverage.log.
All 7,268 tests pass, zero failed/skipped across all six families:

| Family | Passed |
| --- | ---: |
| Domain | 361 |
| Shared | 125 |
| Query | 512 |
| Command | 1,174 |
| Data | 2,753 |
| WebApi | 2,343 |

ReportGenerator merges six fresh Cobertura files into
`docs/coverage-report/Summary.json` and HTML. Merged production line coverage is
98.4% (14,806 / 15,039), above the 90% gate; branch coverage is separately 92.1%
(5,556 / 6,030). All six production assemblies separately exceed 90% lines:

| Assembly | Line coverage | Covered / coverable lines | Branch coverage |
| --- | ---: | ---: | ---: |
| Command | 100% | 963 / 963 | 97.7% |
| Data | 98.4% | 11658 / 11837 | 88.6% |
| Domain | 99.3% | 482 / 485 | 96.4% |
| Query | 100% | 576 / 576 | 98.8% |
| Shared | 97.2% | 355 / 365 | 88% |
| WebApi | 94.9% | 772 / 813 | 89.2% |

No production code, test, security endpoint document or OpenAPI source changed
during the run. Feature commit remains 2c4bca6e8368be7ce1a3b607366558d644dab099;
parent-only completion docs moved branch HEAD to 8ad4e3949c7df43d3d3e9260e2be3991982bb3f9
while verification ran. `git diff --name-only -- src tests docs/contracts/openapi.json docs/security/collection-api.md`
is empty after completion, and `git diff --check` passes. Separate category suites,
exact delivered-head CI, PR/merge and lifecycle closeout remain controller work;
no release/main/tag or protection bypass is claimed or attempted here.

The controller authorized committing focused-green work before this full run to
overlap read-only independent reviews. Both task and whole-branch review report no
Critical/Important issues; no source-changing review fix was requested, so this is
the only fresh unfiltered final run and none was redundantly repeated.

## Self-review against spec and AFs

Main flow: strict transport/context, one current-authority read, visibility before
ordering, all native evidence from same snapshot, safe opaque output and bound
continuation. Tests establish current own and native RO/RW scope, exact selected
links and no inference from direct folder/record links, native signatures, owner /
recipient / collection / grant identity substitutions, stored and signed epoch /
revision substitutions, malformed/missing/current pins, hidden evidence exclusions,
current policy and statement-time expiry, snapshot-only revocation and immutable
reads.

AF-01: canonical sizes, access/cursor schema/tamper/context/purpose, duplicate or
unknown queries, duplicate/empty headers, body/framing/oversize; 400 or existing413.
AF-02: inaccessible hidden/foreign/terminal/closed rows omitted, typed UUID aliases,
unknown/inactive acting account404, empty scope200.
AF-03: fresh identity401/dependency503, missing access401, expired/revoked/stale /
foreign/dangling/hidden selected profile403.
AF-04: visible safe ordering, native and projection corruption fail closed503 with
no partial data; dependency/timeout/internal cancellation503, caller cancellation
propagates. Highwater insert exclusion and current revocation/PC removal are tested.

## Deviations / concerns

- No new schema, package, release workflow, content traversal or lock protocol.
- Existing folder/record query structure and fixture helpers adapted as required;
  collection admission omits every content ancestry/membership traversal.
- Shared OpenAPI requiredness/empty413 metadata remains the already tracked
  repository limitation, outside this task; actual runtime shape is tested from
  raw HTTP JSON and regenerated contract adds the new endpoint/item/page schemas.
- Initial sandboxed .NET attempt failed on forbidden build/test sockets; all
  meaningful dotnet tests used approved escalation. Functional tests use approved
  Docker access, and commit uses approved shared .git access.
- Parent-owned README/changelog/checkpoint/operations edits are left untouched and
  excluded from this task's feature commit.
- Independent security/client release qualification remains deferred by existing
  develop authorization, and no release/main/tag/protection bypass is attempted.

## Independent task review

Spec and quality Approved, zero Critical/Important issues. Review artifact:
.superpowers/sdd/2026-10-11-uc-30-list-collections/task-1-review.md.
Nonblocking findings: compact copied test/fixture formatting; an explicit
continuation regression for corrupt visible foreign evidence beyond an already
carried highwater; benign no-matching-test selection messages from unrelated
assemblies in the focused solution command. Existing tests cover foreign
nonpage corruption and continuation revocations; independent review confirms the
store evidence CTE reads all visible grants regardless of boundary. Main controller
requests no source/test changes now, preserving the stable full-run source; final
triage remains with the controller. No build warnings/errors or skipped UC-30
cases are implied by the unrelated filter selection messages.
