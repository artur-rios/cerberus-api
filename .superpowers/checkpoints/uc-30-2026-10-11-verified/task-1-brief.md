### Task 1: Deliver the collection-list flow across its existing layers

This is one independently reviewable read operation; contracts, store and HTTP
dispatch are coupled, so the task owns their shared interface and meaningful tests.

**Files:**
- Create Domain/Collections/CollectionListContracts.cs beneath `src/Domain/ArturRios.Cerberus.Domain/`.
- Create Query/Collections/{ListCollectionsQuery,ListCollectionsHandler,CollectionListCursor,CollectionListOutput,CollectionProjection,CollectionListMessages}.cs beneath `src/Application/ArturRios.Cerberus.Query/`.
- Create Data/Collections/CollectionListStore.cs beneath `src/Infrastructure/ArturRios.Cerberus.Data/`.
- Modify `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/CollectionsController.cs` and `Startup.cs`.
- Test `tests/Application/ArturRios.Cerberus.Query.Tests/ListCollectionsHandlerTests.cs`, `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionListStoreTests.cs`, `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionListHttpTests.cs`.
- Document GET in `docs/security/collection-api.md`; regenerate the existing OpenAPI artifact through scripts/openapi.py.

**Interfaces:**
- `CollectionListRequest(Guid Actor,string AccessVerifier,int PageSize,long After,long? Boundary)`.
- `CollectionListRow(Guid CollectionId,long Revision,long ServerSequence,DateTimeOffset EditedAt,byte[] Envelope,long KeyEpoch)`.
- `CollectionListPage(IReadOnlyList<CollectionListRow> Items,long Boundary,bool HasMore)`.
- `ICollectionListStore.ListAsync(CollectionListRequest,CancellationToken): Task<VaultResult<CollectionListPage>>`.
- `ListCollectionsQuery(Guid actor,string? access,int? pageSize,string? cursor): BaseQuery`.
- `ListCollectionsHandler: IQueryHandlerAsync<ListCollectionsQuery,CollectionListOutput>`.
- Output follows FolderListOutput replacing FolderId with CollectionId; private cursor purpose is `cerberus-collection-list-cursor-v1`.

- [ ] Write handler/cursor behavioral tests first, including valid opaque page, invalid size/access, tampered/actor/access/size/cross-purpose continuation, duplicate/out-of-order IDs/counters, malformed time/envelope/key epoch, null/error-with-data or corrupt page. Run Query.Tests filter ListCollections and capture RED for missing feature.
- [ ] Add minimal domain/query contracts and implementations using existing folder/record list patterns. Run the handler/cursor tests; expect GREEN.
- [ ] Write real PostgreSQL store tests first: active own/RO/RW and selected PC filtering, direct content does not imply collection permission, omissions before size1 paging, highwater inserts/revocation/PC removal, all native binding cases, hidden corruption, typed terminal aliases, current session/account policy/expiry, unavailable dependency, caller cancellation and immutable snapshots. Run Data.Tests filter CollectionList; capture RED before adding store.
- [ ] Implement one snapshot CTE adapted from FolderListStore's actor/session/collections/grant evidence. No ancestry or member traversal is needed. Validate safe ordering over visible inventory and snapshot native binding; project byte arrays/keyEpoch into CollectionListRow. Run Data.Tests filter CollectionList; expect GREEN.
- [ ] Write HTTP tests first: actual POST controls, exact GET shape/ciphertext/no-store, malformed query/header/body, identity/access failures and dependency503, selected native sharing/revocation, cross-purpose cursor. Run WebApi.Tests filter CollectionList; capture RED with missing GET.
- [ ] Add strict CollectionsController.GET, query/store/cursor DI and safe status messages. Run WebApi.Tests filter CollectionList and all three new test classes; expect GREEN. Document request/response/statuses, highwater semantics and grant evidence boundaries.
- [ ] Self-review against spec and all AFs, record deviations with reasoning. Commit only this task's files with `feat: expose scoped collection listing`.

Controller then runs required unfiltered/coverage/category suites and helper/protocol/
audit/OpenAPI checks, obtains task and whole-branch reviews, updates README/changelog
and project status, opens PR, verifies exact head CI, normally merges and closes out.
