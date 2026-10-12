# UC-32 Update collection implementation plan

> **For agentic workers:** REQUIRED SUB-SKILL: use `superpowers:executing-plans` task by task. Every task is test-first and receives a separate implementation review. One fresh whole-branch review follows all tasks.

**Goal:** Replace one currently authorized owner's encrypted collection envelope with optimistic conflict protection and an exact synchronization change.

**Architecture:** Dedicated Domain/Command contract; owner-only target transaction with current session/selection checks and a final guarded update; strict PUT endpoint and explicit dependency injection.

**Tech stack:** Existing .NET 10, C#, FluentValidation, Mediator, EF Core, Npgsql, PostgreSQL, xUnit, Moq and native protocol helpers. No schema, package or algorithm change.

**Spec:** `docs/superpowers/specs/2026-10-11-uc-32-update-collection.md`

## Global constraints

- Exactly `expectedRevision`, `envelope`, `editedAt`; exact output `collectionId`, `revision`, `serverSequence`, `editedAt`; canonical path/header, no query, strict bounded UTF-8 JSON, `VaultProofBody`, no-store and common empty `413`.
- Safe positive counters at most `9007199254740991`; UTC time at least ten ticks and normalized to microseconds; same key epoch; changed ciphertext requires fresh key salt and nonce; identical envelope allowed.
- Owner-only mutation. Wide access permits owned target; selected access requires exact active `ProfileCollection`. Current visible foreign RO/RW target validates its one relevant native grant, then returns `403`; hidden/unselected/revoked foreign target returns `404`.
- Lock own account, own session, selected own profile and target collection in order. Do not lock/inspect owner grants for owner update or lock foreign authority rows. Guard current authority, expected revision, old sequence/envelope and key epoch in the write.
- Update only envelope/time/revision/sequence/stamp. Preserve ownership, epoch, lifecycle, profile/content memberships, grants/wrappers and related metadata.
- Cover Main and AF-01 through AF-05; fresh full six-family suite, at least 90% line coverage per production assembly, zero failures/skips, native audits, one final reviewer and exact-head CI.

## Review focus

- Active read/write recipients must remain unable to update the collection itself, while truly hidden foreign targets stay nonrevealing.
- Selected-profile loss or link removal must never widen to account-wide owner access, including after lock waits.
- Owner updates must not depend on unrelated or malformed grants; visible foreign denial must fail closed on its one relevant malformed native binding.
- Same-revision writers and lifecycle/access/protection races must preserve exactly one winner without cross-owner lock cycles.
- Result validation and injected post-write failures must roll back target changes while leaving every association and grant untouched.

### Task 1: Strict collection update contract and command

**Files:** Create `src/Domain/ArturRios.Cerberus.Domain/Collections/CollectionUpdateContracts.cs`; create `src/Application/ArturRios.Cerberus.Command/Collections/UpdateCollectionCommand.cs`, `UpdateCollectionValidator.cs`, `UpdateCollectionHandler.cs`, `UpdateCollectionOutput.cs`, `CollectionUpdateMessages.cs`; create `tests/Application/ArturRios.Cerberus.Command.Tests/UpdateCollectionHandlerTests.cs`.

**Interfaces:** `CollectionUpdateInput(long ExpectedRevision, EncryptedEnvelope Envelope, DateTimeOffset EditedAt).IsValid()`; `CollectionUpdateRequest(Guid Actor, string AccessVerifier, Guid CollectionId, CollectionUpdateInput Input)`; `ICollectionUpdateStore.UpdateAsync(...): Task<VaultResult<CollectionCreateDetails>>`. `UpdateCollectionCommand` owns only body data plus private `SetContext(Guid,string?,Guid)` and `ToInput()`; the handler hashes the access handle, invokes the store and returns exact four-field output.

- [ ] Write handler/validator tests first for invalid context/input, no persistence on rejection, exact trusted forwarding, access-handle hashing, allowed error mapping, malformed store results, safe revision/sequence/time/target validation, strict body requiredness and caller cancellation.
- [ ] Run focused Command tests and capture the expected missing-type RED result; confirm the filter discovers tests and the failure is not fixture syntax.
- [ ] Implement the named Domain and Command contracts, using established folder/record update semantics and `collection_updated`/400/401/403/404/409/503 messages.
- [ ] Run focused green and the entire Command test project with zero failures/skips.
- [ ] Commit `feat: define collection update contract`; record actual commands/results in the SDD ledger and obtain task review before Task 2.

### Task 2: Owner-only transactional collection update

**Files:** Create `src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionUpdateStore.cs`; create `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionUpdateStoreTests.cs`.

**Interfaces:** Consume Task 1 request/input/store contracts and reuse `CollectionCreateDetails`; use current schema and `CollectionGrantBinding` only for visible foreign denial.

- [ ] Write PostgreSQL tests first for wide/selected owners, exact mutation and unchanged memberships/grants/wrappers/related rows; identical envelope, timestamp normalization and target-only update. Cover valid native RO/RW visible `403`, hidden/unselected/revoked foreign `404`, and malformed relevant foreign evidence `503` while unrelated malformed evidence is ignored.
- [ ] Cover invalid actor/session/selection/target lifecycle, strict stored counters/time/envelope/epoch, expected/max revision, input epoch/salt/nonce rules, global sequence exhaustion, caller/internal cancellation, database failures and injected post-write rollback.
- [ ] Add same-revision exactly-one-winner/lost-retry tests and lock-wait races for expiry, policy/generation, selected link/profile, target delete/terminal, protection rotation and final guarded authority changes. Explain target lookup against at least 100 unrelated collections and assert target-only/no-foreign authority locking.
- [ ] Run focused Data tests and capture the expected missing-store RED result.
- [ ] Implement the transaction: own account/session/selection/target locks, current reload, owner/foreign classification, owner stored validation, expected revision, safe replacement validation, fully guarded write, result verification and rollback-safe exception mapping.
- [ ] Run focused green and the entire Data test project with zero failures/skips.
- [ ] Commit `feat: update owned collection content`; record actual evidence and obtain task review before Task 3.

### Task 3: PUT integration, documentation and full verification

**Files:** Modify `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/CollectionsController.cs` and `src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs`; create `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionUpdateHttpTests.cs`; update `docs/security/collection-api.md`, `README.md`, `CHANGELOG.md`, applicable operations status documentation and generated `docs/contracts/openapi.json`.

**Interfaces:** `PUT /api/collections/{id}` consumes `UpdateCollectionCommand` and returns `UpdateCollectionOutput`; register the store, validator and handler explicitly. Existing create/list/detail endpoints remain unchanged.

- [ ] Write actual HTTP tests before the route for native owner wide/selected success, exact four-field output, preserved GET detail/memberships/grants, valid foreign RO/RW `403`, hidden foreign `404`, strict path/query/header/body/transport failures, nonce/salt/epoch/time rules, access/lifecycle races, lost retry and no-store/no-mutation behavior.
- [ ] Run focused Web tests and capture the expected missing-PUT RED result; confirm actual route failure and nonzero test discovery.
- [ ] Add the focused `[HttpPut("{id}")]`/`[VaultProofBody]` action and explicit store/validator/handler registrations. Run focused HTTP green.
- [ ] Update security, changelog, operations, README milestone status and generated OpenAPI to document exact owner-only update semantics and response shapes. Preserve known shared metadata limitations truthfully.
- [ ] Run locked restore and dependency audit; native audit with zero findings, corpus and helper suites, spec validation, OpenAPI drift, full-range whitespace, then a fresh unfiltered six-family test/coverage run. Require zero failures/skips and every production assembly at least 90% line coverage; report branch coverage.
- [ ] Commit `feat: expose collection update endpoint`; record all evidence and obtain task review.

## Final review and delivery

- [ ] Build one original-base-to-tested-head review package containing the design, plan, all five review-focus statements, all rulings and verification evidence. Dispatch one fresh ordinary whole-branch reviewer. Apply at most one blocking TDD correction pass; defer nonblocking minors with explicit rationale and cost.
- [ ] Re-run exact changed-scope and full verification after any correction. Confirm clean worktree and unchanged base assumptions.
- [ ] Push `feature/uc-32-update-collection`, open one PR into `develop` closing issue 33, attach it to this chat and move the project item to Testing.
- [ ] Wait for required `branch-policy`, `test` and `docker` checks on the exact delivered head. Require success, mergeable/clean state and no base drift; perform a normal match-head squash merge without admin bypass or force and delete the feature branch.
- [ ] Verify issue 33 closed, project/README Done, nine Definition-of-Done items, remote/local feature branch absent, fresh `develop` tree equals the tested merge tree, all primary user files preserved and the worktree clean before UC-33.

