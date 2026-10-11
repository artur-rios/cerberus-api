# UC-30 List collections Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan. Steps use checkbox syntax for tracking.

**Goal:** Return only current permitted encrypted collections with bounded pagination.
**Architecture:** A query handler owns input/cursor/projection validation. A read-only
PostgreSQL store captures authority, collection inventory, grant evidence and page
in one statement. The existing CollectionsController adds strict GET dispatch.
**Tech Stack:** .NET10, ASP.NET Core, existing Mediator/Output, EF Core/PostgreSQL,
family TestSupport/Moq/real PostgreSQL fixtures. No new package or schema migration.
**Spec:** `docs/superpowers/specs/2026-10-11-uc-30-list-collections.md`

## Global Constraints

- One issue #31, branch `feature/uc-30-list-collections`, PR into `develop`.
- GET has exactly pageSize/cursor and one X-Cerberus-Vault-Access; no body.
- Default size min(50, MaxPageSize); positive size bounded by MaxPageSize.
- Page has exactly items/nextCursor; items have collectionId/revision/serverSequence/editedAt/envelope.
- One statement captures current authority; ownership, selected profile and sharing filter before pagination.
- Current native foreign grants bind collection epoch, owner/recipient pins and identity.
- No decryption, graph mutation, foreign account/profile locks or scope widening.
- All main/AF-01..04 flows, full fresh verification and ordinary independent code review.
- No force push, admin merge, self-approval, skipped tests or release authorization.

## Review Focus

- A selected profile linked only to content cannot expose its containing collection.
- Revoked grants or removed profile links disappear on continuations despite cursor validity.
- Valid native evidence from another collection/epoch/recipient is rejected without partial disclosure.
- Hidden corrupt evidence cannot poison a page; visible corrupt ordering fails closed.
- Duplicate query/header, unknown-length framed GET, or cross-purpose cursor receives400/no-store.

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
