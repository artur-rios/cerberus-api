# UC17 List Records Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

Goal: list only current permitted active native-encrypted records with bounded opaque
pagination, including actual collection-recipient access.
Spec: docs/superpowers/specs/2026-10-08-uc-17-list-records.md.
Stack: existing .NET10/EF10/PostgreSQL18.6/native crypto and mediator/output libraries;
no dependency changes. Use the existing locked EF version for migration tooling.

Global: isolated branch from fresh merged UC16 after its full integration DoD;
preserve primary user checkout. One issue18/branch/PR. Required unfiltered six-family
suite, zero failures/skips, >=90% line coverage and reported branches; native and
dependency audits/helper/spec/OpenAPI/diff checks. Single SQL snapshot and visibility
before keyset pagination; never disclose another owner's organization. Existing
independent approval deferral is development-only; strict release gates unchanged.
One fresh ordinary final review, one TDD Critical/Important correction pass, exhaustive
declined-scope rulings and costs, exact-head latest required CI before normal merge.

Review Focus: grant-derived content visibility must follow current native recipient
bindings and selected-profile collection links, without ownership transfer or owner
profile/parent leakage; all folder ancestry must be same-owner/active/nonterminal and
terminate cycles; hidden/unselected corrupt content or grants must not become an
availability oracle; overlapping direct/folder/collection routes deduplicate before
pagination; session/grant revocation between pages applies to the next SQL snapshot;
cursor purpose/actor/handle/size substitution and corrupt ordering cannot skip or
disclose content; membership schema deletion removes links only and preserves typed
same-GUID resources; read never mutates any content/protection/access row.

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

- [x] Write specified failing tests, run and read expected RED before product code.
- [x] Implement this task, run named whole-family verification, read zero failures/skips and commit.

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

- [x] Write specified failing tests, run and read expected RED before product code.
- [x] Implement this task, run named whole-family verification, read zero failures/skips and commit.

### Task 3: Record cursor, strict projection and Query

Create Query/Records/RecordListCursor.cs, ListRecordsQuery/Handler, RecordListOutput,
RecordListMessages and RecordProjection helper. Use purpose-separated existing
AESGCM/HMAC cursor pattern with canonical bounded tuple. Bind actor/hash/size and
initial boundary, strict pre-store syntax, safe metadata/native JSON output guards,
allowlisted errors/null/error-with-data rejection and cancellation propagation.
Default min50/configuredMax, no size+1 overflow. Output exactly items+nextCursor and
five item fields; current stored UTC time >=10ticks and microsecond aligned.

Write Query/ListRecordsHandlerTests.cs first: default/custom/max bounds, actor/access
missing/malformed, actual handle hash and caller token, continuation/end, emptylist,
canonical cursor/tamper/wrong actor/wrong handle/wrong size/profile-purpose/keyrotation,
duplicate/unknown/case/numeric/unsafe cursor fields, impossible cursor/page metadata,
null/errors-with-data/unknown errors, strict native format/binary/epoch/unknown and
numeric-string envelope corruption, duplicate public IDs/ordering/outsideboundary/
unsafe metadata/default/offset/submicro times, exact public shape and safe errors.
Expected RED: missing Query. GREEN whole Query; commit.
Interfaces: consumes Task1/2; produces Task4 controller/DI/public contract. No grant
key blobs, owner profiles or potentially out-of-scope folder IDs in list output.

- [x] Write specified failing tests, run and read expected RED before product code.
- [x] Implement this task, run named whole-family verification, read zero failures/skips and commit.

### Task 4: HTTP record listing and complete delivery

Modify RecordsController to share Command/Query mediator and add GET/api/records,
strict one-value known query parser/canonical decimal pageSize/single access header,
no GET body/transfer encoding, current actor context; explicit store/cursor/Query DI.
Create Web/RecordListHttpTests.cs before route: actual UC16-created encrypted records,
UC15-issued Master/PerProfile selected handles, native RO/RW recipient collection
membership/direct+descendant scope, no organization leakage, duplicate route dedup,
stable pagination/emptylist/highwater/newinsert/grantrevocationbetweenpages, current
identity/session/owner/selected lifecycle, strict malformed/duplicate/unknown/query/
header/GETbody/commonempty413/cursor/substitution, corrupt visible metadata/native envelope503,
real database/provider outage and read no-mutation/no-store.
Expected RED: missing GET route405. GREEN focused HTTP and whole Web.

Update record API and ops migration/read/grant/cursor docs, changelog, README UC17
Done/M03nine, generate/check actual OpenAPI and audit exact item/output/query/status
shape (track known shared-generator minors truthfully). Locked restore/NuGet native
audits,322case4direction corpus/36harness/23helpers/spec/diff; full unfiltered coverage
six families >=90line/zero failures/skips, branch report. Commit. One fresh final
review/one blocking TDD fix pass with full suite if needed, all decline rulings/costs,
push PR and verified Testing, fresh exact-head latest branch-policy/test/dockerSUCCESS
MERGEABLE+CLEAN/unchanged testedbase normalmatchheadmerge, close/Done/allDoD/remotebranch
absent/develop exact testedtree/user SHA/archive only owned scratch. Continue UC18.

- [ ] Write specified failing tests, run and read expected RED before product code.
- [ ] Implement this task, run named whole-family verification, read zero failures/skips and commit.

Verification: whole Data for Tasks1/2, whole Query for Task3, focused RecordListHttpTests
then whole Web and full python3 scripts/coverage.py for Task4. Serialize shared .NET
build commands; native cache /tmp/cerberus-protocol.fCQIq1. A just-completed unchanged
whole-family/full-suite log can be audited for task-done rather than rerunning it.
Fresh UC16 merge DoD is complete; no UC17 code/tests yet.
