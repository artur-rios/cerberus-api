# UC24 research notes (not an approved spec or implementation)

Read while UC23 PR84 CI runs; do not start product/worktree until UC23 all-nine DoD completes.
Fresh issue25 `/tmp/cerberus-uc24-issue-read-ahead.json`: List folders, FR-FD-02, BR07/22/28. Main permission-filter BEFORE pagination; AF01 visible invalid400, AF02 hidden omitted/accountmissing404, AF03 identity401/access403, AF04 dependency503. No mutation/revision conflict flow.
UC23 reference checkout `/tmp/cerberus-uc23-worktree`, base d72d920 through tested3bf6b37. Fresh actual4919PASS98.4line91.7branch awaiting remoteCI. Preserve primary four user SHA and future deferrals.

Existing useful paths discovered:
- `src/Domain/ArturRios.Cerberus.Domain/Records/RecordListContracts.cs`
- `src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordListStore.cs` (184lines), single SQL snapshot, candidate admission before complete ancestry, native ALL visible contributor bindings, ordering corruption before keyset.
- `src/Application/ArturRios.Cerberus.Query/Records/{ListRecordsHandler,ListRecordsQuery,RecordListCursor,RecordListMessages,RecordListOutput,RecordProjection}.cs`
- `tests/Application/ArturRios.Cerberus.Query.Tests/ListRecordsHandlerTests.cs`
- `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/RecordListStoreTests.cs`
- `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/RecordListHttpTests.cs`
- `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/{RecordsController,FoldersController}.cs`
- Native selected profile helpers actually issue UC15 proof/open in HTTP fixtures. Data ProfileSetup and AssociationSetup helpers reused by existing tests.

Candidate design ideas (must refine/self-review in actual UC24 design):
- Match existing list convention GET `/api/folders`, optional canonical positive pageSize<=MaxPageSize, default min50,max; exact case/unique query keys pageSize/cursor, single canonical access header, no GET body inclHTTP2/unknown length; no-store. Current Heimdall actor, never body/queryowner/profile.
- Exactly items/nextCursor; each folder item folderId/revision/serverSequence/editedAt/envelope (no parent/profile/collection/grant/owner IDs; future get relationships separate).
- Dedicated folder cursor purpose `cerberus-folder-list-cursor-v1`, own AESGCM/HMAC context with actor+hashedhandle+pagesize+after+initialboundary; reject record/profile token, actor/handle/page/key substitution/tamper/invalidstricttuple/duplicateunknownnumericstring JSON. Existing actor/hashedhandle sessions are scoped/immutable; current session/policy/gen checked on every page. Ordinary keyset list, not durable sync; new>boundary excluded, revocations/membership evaluated current.
- No schema/dependency/native crypto changes. Domain FolderListRequest/Row/Page/interface analogous record types. Query does safe allowlist, exact entire-page public projection/ordering/ID/time/native-envelope validation before any payload.
- Data single recursive SQL actor/current session -> eligible owned or current recipient collections -> direct selected ProfileFolder + CollectionFolder roots -> active sameowner descendant reachable -> admitted ownwide or reachable folders -> candidates activeowner/typedfolderterminal -> complete owned upward ancestry INCLUDING candidate itself -> included collectionroutes -> scoped -> visible -> ordering/boundary/page/relevantforeigngrants. No ProfileRecord or CollectionRecord route must expose their containing folder. Parent/ancestors/siblings above/beside a granted member remain hidden (ancestry traversal structural only).
- Full active ancestry mandatory. Hidden ancestor beats corruption/native inspection; scoped fully active cycle503, unrelated unselected cyclic/deep inventory omitted without availability oracle. Duplicate/zero/unsafe seq checked ALL visible before keyset; envelope/counters/time projection validates returnedpage. Relevant foreign native grants validated for ALL currently visible contributors before page payload, even nonpage/overlap; all owner/recipient pins internal, cache pins per account where possible as existing target readers optimize. Owned collections need no foreign grant evidence.
- Query plan tests: selected own direct leaf or foreign granted leaf with 100 unrelated deepfolders; CTE candidates<=1 and ancestry only that leaf+actualancestors. Unlike record-list rootless item, folder candidate itself adds one ancestryrow. One SQL reader/snapshot; revoke-afterstatement returns snapshot then next request omits, exclusive statementtime expiry denies.

Test strategy must not mechanically copy unrelated record behavior:
- Query existing ~100 common tests can adapt Folder output/row/cursor, add recordPurpose substitution; no getterholder tests.
- Data explicit native RO/RW collection folder membership/root+descendants (new UC23 minor warns fixture granted collection MUST actually include targetfolder), overlapping routes/dedup, selected both protectionmodes viaHTTP, owned collectionscope, record-only grants/directProfileRecord never widen folder list. Accountwide includes ownall+shared; selected onlydirectfolder/descendants+selectedcollections. Leaf member doesn't expose unshared parents/siblings. Check zero mutation bothowner/recipient persistedstates.
- All hidden authority/native corruption/typed crosskind markers/currentselection lifecycle/betweenpage revocation/session currentpolicy/corruptordering/cycles/deepcandidateplan/failure/callerct from existing list matrix applicable; no actual current future folderwriter exists to certify.
- HTTP initial mainflow create folders via actualUC23 endpoint, exact5fields, stable boundary; directdbseed ancestor fixtures okay. Fresh profile proof Open gets current revision after folder create metadata advances. Share helpers use actual CollectionFolder, no irrelevant CollectionRecord substitution. Unknownquery/noGETbody/identity/provider/access/dependency/empty413 native selected matrix applicable.
- Likely3 tasks: Query+Domain contracts, Data store, HTTP+DI+docs+READMEfullvalidation, followed by ONE mandatory gpt6astra fork_none review, normalPR/CI/DoD. Exactplan must name everyflow/fiveReviewFocus and task tests/commands/interfaces.

Inherited carry: all typedterminal kinds/priorreaders UC22fixed; never null selectedProfileId; maxsafeinteger, UTC>=10ticks/microprecision; selected routes/releaseclientroot/provisioning remain deferred; shared OpenAPI requiredness/nullability+empty413; UC23 two narrowed fixture Minors; priorpermission500 Minor; futurevisibilitysync/lifecycle/UC35implicitFKaudit. No release approval inferred.
