# UC-33 Delete collection implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: use
> `superpowers:executing-plans` task by task. Every task is test-first and receives
> a separate implementation review. One fresh whole-branch review follows all tasks.

**Goal:** Recoverably trash one owner-visible collection, deactivate memberships and
grants, and preserve every underlying record and folder.

**Architecture:** Dedicated Domain/Command contract; owner-only current-authority
transaction with captured graph, ordered locks, guarded lifecycle update, membership
removal, grant revocation and generic trash/retention rows; strict DELETE endpoint
and explicit dependency injection.

**Tech stack:** Existing .NET 10, C#, FluentValidation, Mediator, EF Core, Npgsql,
PostgreSQL, xUnit, Moq and native protocol helpers. No schema, package or algorithm
change.

**Spec:** `docs/superpowers/specs/2026-10-11-uc-33-delete-collection.md`

## Global constraints

- Exactly required `expectedRevision`; exact six-field output
  `collectionId/trashOperationId/revision/serverSequence/deletedAt/purgeAt`;
  canonical path/header, no query, strict bounded UTF-8 JSON, `VaultProofBody`,
  no-store and common empty `413`.
- Owner-only deletion. Wide access permits owned target; selected access requires
  exact active `ProfileCollection`. Current visible foreign RO/RW validates relevant
  native evidence then returns `403`; hidden/unselected/revoked foreign returns `404`.
- Preserve collection bytes/edit time/epoch/owner and all underlying content. Remove
  PC/CF/CR rows; snapshot sorted public IDs; bump active linked owned resources once.
- Revoke active grants in place with revision plus one; preserve their identities,
  recipients, access and wrappers; never reactivate on restore or revoke recipient
  sessions.
- One collection trash operation, one typed entry and one retention item with exact
  UTC microsecond 720-hour timestamps. Existing typed entry/conflicting graph is
  `409`; all writes are transactional and rollback verified.
- Own account/session, ordered own profiles, target collection, owned folders,
  records, grants and associations; reload complete inventory and guard current
  authority. No foreign account/profile/protection locks.
- Cover Main and AF-01 through AF-05; fresh full six-family suite, at least 90% line
  coverage per production assembly, zero failures/skips, native audits, one final
  reviewer and exact-head CI.

## Review focus

- Deletion must never trash included records/folders or disturb their ciphertext,
  parents, direct noncollection links or ownership.
- Every active grant must be revoked exactly once and remain revoked after future
  collection restoration; recipients must lose online visibility immediately.
- Selected-profile loss/link removal must never widen owner access, and a visible
  foreign recipient must receive only a validated `403`.
- Captured membership/grant inventory and ordered locks must prevent partial graph
  deletion, missed new links and cross-owner deadlocks under real writer races.
- Any association, parent, grant, trash or queue failure must roll back collection
  lifecycle and the entire graph while preserving a clean retry.

### Task 1: Strict collection trash contract and command

**Files:** Create
`src/Domain/ArturRios.Cerberus.Domain/Collections/CollectionTrashContracts.cs`;
create
`src/Domain/ArturRios.Cerberus.Domain/Trash/CollectionAssociationSnapshot.cs`;
create `src/Application/ArturRios.Cerberus.Command/Collections/DeleteCollectionCommand.cs`,
`DeleteCollectionValidator.cs`, `DeleteCollectionHandler.cs`,
`DeleteCollectionOutput.cs`, `DeleteCollectionMessages.cs`; create
`tests/Application/ArturRios.Cerberus.Command.Tests/DeleteCollectionHandlerTests.cs`.

**Interfaces:** `CollectionTrashRequest(Guid Actor,string AccessVerifier,Guid
CollectionId,long ExpectedRevision)`; `CollectionTrashDetails(Guid CollectionId,
Guid TrashOperationId,long Revision,long ServerSequence,DateTimeOffset DeletedAt,
DateTimeOffset PurgeAt)`; `ICollectionTrashStore.TrashAsync(...):
Task<VaultResult<CollectionTrashDetails>>`; `CollectionAssociationSnapshot(Guid[]
ProfileIds,Guid[] RecordIds,Guid[] FolderIds)`. Command exposes only required
`ExpectedRevision` and trusted `SetContext(Guid,string?,Guid)`.

- [x] Write handler/validator tests first for invalid context/revision, strict body
  requiredness, no persistence on rejection, exact trusted forwarding and handle
  hashing, caller cancellation, safe error mapping, null/error-with-data/unknown
  results, mismatched IDs/counters/timestamps and exact six-field output.
- [x] Run focused Command tests and capture the expected missing-type RED result;
  prove test discovery and exclude fixture/compiler mistakes unrelated to missing
  production types.
- [x] Implement the Domain and Command contracts using the established record/folder
  trash result validation and `collection_deleted` status mapping.
- [x] Run focused green and the entire Command test project with zero failures/skips.
- [x] Commit `feat: define collection trash contract`; record evidence and obtain a
  separate task review before Task 2.

### Task 2: Guarded owner-only collection trash transaction

**Files:** Create
`src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionTrashStore.cs`;
create
`tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionTrashStoreTests.cs`.

**Interfaces:** Consume Task 1 contracts/snapshot and existing `TrashOperation`,
`TrashEntry`, `RetentionWorkItem`, collection authority and `CollectionGrantBinding`.

- [x] Write PostgreSQL tests first for wide/selected owner success, exact lifecycle
  metadata, unchanged collection bytes/time/epoch/owner, unchanged underlying
  records/folders and direct links, sorted complete snapshots, PC/CF/CR removal,
  distinct active owned resource bumps, one operation/entry/queue and retained
  inactive links without inactive bumps.
- [x] Cover active grant revocation/revision increments, already-revoked preservation,
  unchanged wrapper/recipient/access, recipient GET/list disappearance, foreign
  profile link removal without profile mutation, no recipient session revocation,
  and retained collection participation in complete protection rotation.
- [x] Cover native valid RO/RW visible `403`, hidden/unselected/revoked `404`, malformed
  relevant evidence `503`, unrelated corruption ignored, account/session/selection/
  lifecycle precedence, damaged owned opaque bytes, counters/times/purge fields,
  stale/max revisions, existing typed entries and sequence exhaustion.
- [x] Add exactly-one-winner/lost-retry tests, lock-wait expiry at every phase,
  membership/grant growth/removal, final-guard lifecycle/selection changes, and
  actual CollectionUpdateStore/CollectionCreateStore/ProfileAssociationStore/
  VaultProtectionChangeStore/recipient RecordUpdateStore and FolderUpdateStore
  interleavings in both relevant orders. Explain one target among 100 unrelated
  collections and assert no unrelated or foreign authority locks.
- [x] Add stage-specific faults after collection write, each association family,
  resource bumps, grant revocation, operation, entry and queue; compare complete
  before/after state, verify clean retry, database outage/internal cancellation and
  caller cancellation behavior.
- [x] Run focused Data tests and capture the expected missing-store RED result.
- [x] Implement ordered locking, current reload, owner/foreign classification,
  stored validation, captured inventory comparison, guarded lifecycle mutation,
  membership snapshot/removal, active owned resource bumps, grant revocation and
  generic trash rows with post-write verification and rollback-safe errors.
- [x] Run focused green and the entire Data test project with zero failures/skips.
- [ ] Commit `feat: trash owned collections`; record evidence and obtain a separate
  task review before Task 3.

### Task 3: DELETE integration, documentation and full verification

**Files:** Modify
`src/Presentation/ArturRios.Cerberus.WebApi/Controllers/CollectionsController.cs`
and `src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs`; create
`tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionTrashHttpTests.cs`;
update `docs/security/collection-api.md`, `README.md`, `CHANGELOG.md`, applicable
operations/status documentation and generated `docs/contracts/openapi.json`.

**Interfaces:** `DELETE /api/collections/{id}` consumes `DeleteCollectionCommand`
and returns `DeleteCollectionOutput`; register the store, validator and handler
explicitly. Existing create/list/detail/update endpoints remain unchanged.

- [ ] Write actual HTTP tests before the route for native owner wide/selected
  success, exact six fields/deadline, preserved underlying content and direct access,
  removed memberships, revoked grants and recipient visibility; cover valid foreign
  RO/RW `403`, hidden `404`, strict path/query/header/body/transport, lifecycle and
  concurrency, no-store and no mutation on failure.
- [ ] Run focused Web tests and capture the expected missing-DELETE RED result with
  nonzero discovery and working prerequisites.
- [ ] Add the focused `[HttpDelete("{id}")]`/`[VaultProofBody]` action and explicit
  store/validator/handler registrations. Run focused HTTP green.
- [ ] Update security, changelog, operations, README milestone status and generated
  OpenAPI for exact collection trash, grant-revocation and restore semantics.
- [ ] Run locked restore and dependency audit; native audit with zero findings,
  corpus/helper suites, spec validation, OpenAPI drift, full-range whitespace, then
  a fresh unfiltered six-family test/coverage run. Require zero failures/skips and
  every production assembly at least 90% line coverage; report branch coverage.
- [ ] Commit `feat: expose collection trash endpoint`; record all evidence and obtain
  a separate task review.

## Final review and delivery

- [ ] Build one original-base-to-tested-head review package containing the design,
  plan, five review-focus statements, all rulings and verification evidence. Dispatch
  one fresh ordinary whole-branch reviewer. Apply at most one blocking TDD correction
  pass; defer nonblocking minors with explicit rationale and cost.
- [ ] Re-run exact changed-scope and full verification after any correction. Confirm
  clean worktree and unchanged base assumptions.
- [ ] Push `feature/uc-33-delete-collection`, open one PR into `develop` closing issue
  34, attach it to this chat and move the project item to Testing.
- [ ] Wait for required `branch-policy`, `test` and `docker` checks on the exact
  delivered head. Require success, mergeable/clean state and no base drift; perform
  a normal match-head squash merge without admin bypass or force and delete the
  feature branch.
- [ ] Verify issue 34 closed, project/README Done, all Definition-of-Done items,
  remote/local feature branch absent, fresh `develop` tree equals the tested merge
  tree, primary user files preserved and worktree clean before UC-34.
