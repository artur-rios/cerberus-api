# UC-31 Get collection Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Return one currently permitted encrypted collection with only actor-visible direct relationships.
**Architecture:** Dedicated Domain/Query detail contracts reuse the existing collection projection. A target-first single PostgreSQL statement captures current collection admission, actor profile links, direct typed members, relevant member ancestry and the one foreign grant binding when required; the existing controller adds a strict GET action.
**Tech Stack:** Existing pinned .NET 10, ASP.NET Core, EF Core/Npgsql/PostgreSQL, Mediator/Output, xUnit/Moq, real PostgreSQL fixtures and native protocol helpers; no new dependency or schema change.
**Spec:** `docs/superpowers/specs/2026-10-11-uc-31-get-collection.md`

## Global Constraints

- One issue #32, branch `feature/uc-31-get-collection`, pull request into `develop`; one use case remains one branch/issue/PR.
- GET has canonical lowercase dashed nonzero `{id}`, one `X-Cerberus-Vault-Access`, no query and no body including unknown-length HTTP/2/transfer encoding; all responses no-store and shared empty 413 preserved.
- Success has exactly collectionId/revision/serverSequence/editedAt/envelope/profileIds/recordIds/folderIds; arrays are nonnull sorted unique nonzero direct public IDs and cross-kind aliases remain valid.
- Target projection uses positive safe integers <=9007199254740991, UTC nondefault exact microseconds, strict supported native envelope and matching stored key epoch.
- One target-first current statement; exact selected ProfileCollection admission; actor-owned direct profile refs; direct same-owner active member refs; relevant upward ancestry only; no descendants in arrays.
- Foreign target verifies only its current native RO/RW grant, collection binding and both protection pins; unrelated corruption is uninspected. No locks, writes, decryptions, new schema/package/algorithm or scope widening.
- Main and AF-01..04, TDD at Query/Data/HTTP layers, fresh full unfiltered six-family coverage >=90% production line, ordinary reviews, required helper/audit/OpenAPI checks and exact-head CI; no skipped tests, force push, admin merge or self-approval.

## Review Focus

- A selected profile linked only to a member record/folder must not expose the containing collection; only an exact current ProfileCollection link admits the detail.
- Recipient profileIds must expose only the actor's own active direct links, never the foreign owner or another recipient, while wide and selected actor scope remain distinct.
- Arrays are direct relationships: preserve explicit record+folder overlap, do not enumerate descendants, omit hidden/incomplete members, and fail only relevant fully active ancestry cycles.
- Relevant foreign target evidence must bind current collection epoch, grant revision, owner/recipient pins and identity; corrupt unrelated collection/grant evidence cannot poison this GET.
- Invalid target projection/reference arrays/transport/store contracts must fail without partial payload or mutation, while cross-kind identical GUIDs stay valid.

### Task 1: Strict collection detail contract and query

**Files:**
- Create `src/Domain/ArturRios.Cerberus.Domain/Collections/CollectionReadContracts.cs`.
- Create `src/Application/ArturRios.Cerberus.Query/Collections/GetCollectionQuery.cs`.
- Create `src/Application/ArturRios.Cerberus.Query/Collections/GetCollectionHandler.cs`.
- Create `src/Application/ArturRios.Cerberus.Query/Collections/CollectionDetailsOutput.cs`.
- Create `src/Application/ArturRios.Cerberus.Query/Collections/CollectionReadMessages.cs`.
- Test `tests/Application/ArturRios.Cerberus.Query.Tests/GetCollectionHandlerTests.cs`.

**Interfaces:** Produces `CollectionReadRequest(Guid Actor,string AccessVerifier,Guid CollectionId)`, `CollectionReadDetails(CollectionListRow Collection,Guid[] ProfileIds,Guid[] RecordIds,Guid[] FolderIds)`, `ICollectionReadStore.ReadAsync(CollectionReadRequest,CancellationToken):Task<VaultResult<CollectionReadDetails>>`, `GetCollectionQuery(Guid actor,string? access,Guid collectionId)`, `CollectionDetailsOutput` with the exact eight public fields, and `CollectionReadMessages.StatusCodes/SafeError`. Reuses unchanged `CollectionProjection.TryRead`.

- [ ] Write handler tests first using literal expectations: invalid actor/access/id rejects before persistence; valid context forwards the opaque-handle verifier, requested ID and exact caller token; valid data returns only the eight fields with independently sorted arrays. Cover target mismatch, null/duplicate/zero same-kind IDs, cross-kind identical IDs, every collection envelope/key-epoch/revision/sequence/time boundary, null result, data+error, unknown error, safe errors, thrown caller cancellation and store cancellation behavior. Review Focus 5 is covered here.
- [ ] Run `dotnet test tests/Application/ArturRios.Cerberus.Query.Tests --no-restore --filter FullyQualifiedName~GetCollectionHandlerTests`. Expect RED because the named collection-read/query types do not exist; fix fixture syntax until the failure is only missing product behavior.
- [ ] Implement the exact contracts, query, output, safe message map and handler. Validate all store data before constructing output; sort each typed array independently and use `collection_found` only on complete success.
- [ ] Run the focused tests, then the whole Query test project unfiltered. Expect zero failures and zero skipped tests.
- [ ] Commit only Task 1 files with `feat: define collection detail contract`, and record the RED/GREEN commands and counts in the SDD report.

### Task 2: Target-first current collection detail snapshot

**Files:**
- Create `src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionReadStore.cs`.
- Test `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionReadStoreTests.cs`.

**Interfaces:** Consumes Task 1's `ICollectionReadStore`, request/details records and existing `CollectionListRow`; produces `CollectionReadStore(IDbContextFactory<AppDbContext>)`. Reuses unchanged `CollectionGrantBinding` and the current typed schema.

- [ ] Write real PostgreSQL tests before the store. Cover owned wide/selected exact ProfileCollection admission; native RO/RW wide/selected access; current recipient-owned profile references; owner/other-recipient profile hiding; direct record/folder arrays, explicit overlapping links and no inherited descendant expansion. Prove actual record/folder GET access does not admit the collection without ProfileCollection. Review Focus 1 and 2 are covered here.
- [ ] Add tests for hidden/terminal/foreign members; incomplete/hidden relevant ancestry omission; relevant fully active cycle 503; unrelated cycles/corruption ignored; typed terminal aliases; only target ordering/projection corruption; all native epoch/signature/wrapper/strict JSON/pin/recipient/grant revision binding failures; unrelated native corruption isolation. Review Focus 3 and 4 are covered here.
- [ ] Add all current actor/session/selected-profile/account/collection/grant lifecycle cases, database/internal cancellation, caller cancellation, no mutations/locks, and one-statement snapshot cases where grant/profile/member/pin state changes after SQL but the next GET observes it. Add `EXPLAIN ANALYZE` with at least 100 unrelated deep folders and collections, asserting one target and only direct members plus their actual ancestry are candidates.
- [ ] Run the focused Data tests. Expect RED because `CollectionReadStore` is missing, with no fixture typo or zero-match filter.
- [ ] Implement one parameterized target-first `SqlQuery<Snapshot>`: actor/session; materialized public-ID target and matching current grant; direct member candidates; relevant upward ancestry with cycle detection; independent actor-profile/record/folder aggregates; one foreign target evidence object. Omit hidden/incomplete members, fail relevant fully active cycles, verify the one native target binding, project immutable copies, and map only internal dependency failures to safe 503 while preserving caller cancellation.
- [ ] Run focused Data tests, then the whole Data test project unfiltered. Expect zero failures/skips and bounded-plan assertions.
- [ ] Commit only Task 2 files with `feat: read permitted encrypted collection`, and record complete focused/whole Data evidence in the SDD report.

### Task 3: Native HTTP collection detail and complete verification

**Files:**
- Modify `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/CollectionsController.cs`.
- Modify `src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs`.
- Test `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionReadHttpTests.cs`.
- Modify `docs/security/collection-api.md`.
- Modify `README.md`.
- Modify `CHANGELOG.md`.
- Modify `docs/requirements/Operations & Infrastructure Document.md` and `docs/operations/foundation-status.md` only where their existing implementation-status sections require UC-31 evidence.
- Regenerate `docs/contracts/openapi.json` with the existing script.

**Interfaces:** Consumes Task 1 query/output/messages/handler and Task 2 store. Produces strict GET `/api/collections/{id}` through `QueryMediator`, preserving existing POST and list endpoints.

- [ ] Write HTTP tests before endpoint/DI. Create the target through actual UC-29 POST; exercise actual Master/PerProfile access, exact eight-field ciphertext payload, sorted direct links, overlap/no descendant expansion, actor-only profile links, native RO/RW grant fixtures and current removal/revocation. Prove actual member detail can succeed while collection detail is 404 without exact selected ProfileCollection. Review Focus 1..4 are covered here.
- [ ] Cover canonical route ID, duplicate/malformed/empty access, query/body/unknown-length HTTP2/transfer encoding, shared empty 413, identity/access lifecycle, missing/hidden target, relevant projection/ancestry/native corruption 503, unrelated corruption isolation, no-store and no mutation. Review Focus 5 is covered here.
- [ ] Run the focused WebApi tests. Expect installed-route failure before implementation, not fixture syntax or zero matched tests.
- [ ] Add the strict controller action and explicit store/query-handler DI adapted from current folder/record detail endpoints. Run focused HTTP tests, then the whole WebApi test project unfiltered; existing POST/list collection behavior must remain green.
- [ ] Document the detail request/response, direct relationship semantics, selected scope, native evidence boundary and error mapping. Update UC-31 tracking/status text and regenerate OpenAPI; assert the exact eight-field detail schema/status/transport shape and no unrelated contract drift.
- [ ] Run locked restore and direct/transitive dependency audit, native protocol corpus/helpers and vulnerability checks, specification verifier, OpenAPI drift/helpers, full range-whitespace checks, full unfiltered solution, Unit and Functional categories, and `python3 scripts/coverage.py`. Serialize .NET/native builds. Require zero failures/skips and every production assembly at least 90% line coverage with branch coverage reported.
- [ ] Commit Task 3 and documentation with `feat: expose collection detail endpoint`; record exact test/helper/coverage counts and tested HEAD. Implementation is complete only after task review and whole-branch review.

## Post-implementation review and delivery

After all three task reviews are clean, generate one whole-branch review package from the original `develop` base through tested HEAD and dispatch one fresh high-capability reviewer against the spec, plan, all five Review Focus items, SDD ledger and actual evidence. A Critical/Important result receives one TDD fix wave and one scoped re-review; deferred minors and every ruling remain explicit.

Push `feature/uc-31-get-collection`, open a pull request into `develop` closing issue #32, attach the PR to this chat, and move the project item through Testing. Wait for required `branch-policy`, `test` and `docker` checks on the exact delivered head. With all green, merge normally without admin bypass or force, delete the feature branch, verify issue #32 closed, project/README Done, nine Definition-of-Done items, tested tree equals fresh `develop`, and the worktree is clean before UC-32.
