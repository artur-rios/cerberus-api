# UC23 Create Folder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Create client-encrypted owned folders with atomic permitted profile/parent relationships and four public metadata fields.
**Architecture:** Follow current record creation with focused folder contracts/store/command/controller. Use existing typed terminal identity, profile projection and protection rotation; extend selected parent admission to active owned collections without foreign grants.
**Tech Stack:** Existing .NET 10, PostgreSQL 18.6, EF Core/Npgsql, FluentValidation and native protocol.
**Spec:** docs/superpowers/specs/2026-10-09-uc-23-create-folder.md.

## Global Constraints

- Preserve primary user branch/files; one isolated feature/uc-23-create-folder branch from verified merged develop, issue 24 and one PR.
- No migrations, added dependencies, new crypto, plaintext name, ownership input or usable server keys.
- Strict required folderId/envelope/editedAt/profileIds and optional nullable parentFolderId; self-parent is 400. Initial key epoch and revision are 1; positive counters at most 9007199254740991.
- UTC input minimum 10 ticks; floor microseconds before PostgreSQL binding. Output exactly folderId/revision/serverSequence/editedAt.
- Account/session/owned UUID-ordered profiles/owned parent ancestry lock order. No foreign account/profile/grant locks. Recheck current policy/generation, selected scope and exclusive DB-time expiry after waits and at final INSERT.
- Current account-wide supports zero/multiple owned profile links; selected creation requires exactly its selected profile and already owned permitted parent ancestry. No foreign-grant parent authority.
- Preserve ciphertext, wrappers, client time and existing links on structural parent updates. Only direct linked profiles/immediate parent advance once.
- All AF01–05 implemented; fresh whole-family task results and final six-family unfiltered coverage, zero failures/skips, all six assemblies >=90% line. Serialize .NET/native builds.
- ONE fresh ordinary whole-branch review, one blocking TDD fix pass if needed; all rulings and Minors recorded. Exact-head required CI and normal merge/all nine DoD before next UC. Independent formal/security/client qualification development-only deferred, release gates retained.

## Review Focus

1. Selected authority through current own collection and inherited parent ancestry must allow a new owned child but never foreign recipient parent administration or another profile attachment; Task 2/4 pin each route and RO/RW foreign rejection.
2. Current access expires while waiting for account/session/profile/parent or immediately before INSERT; Task 2 uses controlled actual DB waits and a final-statement interceptor.
3. Existing same UUID in another kind is independent while typed folder reservation cannot resurrect; Task 2/4 cover all six kinds and a final terminal boundary.
4. Partial insert/link/parent failures and concurrent duplicate/profile-association/trash/rotation winners preserve atomic content and metadata; Task 2 verifies actual persisted winning state and retries.
5. Complete owned ancestry including cycles, inactive/foreign ancestors, irrelevant malformed ciphertext/counters and immediate-parent-only updates; Task 2/4 pin necessary versus unused metadata and no widening through corrupt routes.

### Task 1: Folder create contracts

**Files:** Create src/Domain/ArturRios.Cerberus.Domain/Folders/FolderCreateContracts.cs and tests/Domain/ArturRios.Cerberus.Domain.Tests/FolderCreateContractTests.cs.
**Interfaces:** Produce FolderCreateInput(Guid FolderId, EncryptedEnvelope Envelope, DateTimeOffset EditedAt, Guid[] ProfileIds, Guid? ParentFolderId=null), IsValid(), FolderCreateRequest(Guid Actor,string AccessVerifier,FolderCreateInput Input), FolderCreateDetails(Guid FolderId,long Revision,long ServerSequence,DateTimeOffset EditedAt), IFolderCreateStore.CreateAsync(FolderCreateRequest,CancellationToken) -> Task<VaultResult<FolderCreateDetails>>. Tasks 2/3 consume these exact interfaces.

- [x] Write native envelope/required strict wire tests BEFORE contracts: valid root, parent, multiple profiles, same GUID as a profile, UTC minimum/precision/pre2000/max; invalid self-parent/empty IDs/null/duplicate profiles/native format/epoch/encoding/default/time offset; unknown ownership/plaintext/collectionIds/revision fields and numeric/case/duplicate body. No property holder tests.
- [x] Run `dotnet test tests/Domain/ArturRios.Cerberus.Domain.Tests --filter FullyQualifiedName~FolderCreateContractTests`; Expected: missing FolderCreateInput compile RED before product.
- [x] Implement the exact contracts and required JsonRequired/strict number/unmapped members. IsValid requires nonzero IDs, unique nonnull profiles, envelope valid initial epoch1, UTC ticks>=10, parent null/nonzero/different target.
- [x] Run focused then whole Domain family; Expected: all pass with zero failures/skips. Commit `feat: define folder creation contract` and task-done audit unchanged complete whole-family log.

### Task 2: Atomic scoped folder persistence

**Files:** Create src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderCreateStore.cs and tests/Infrastructure/ArturRios.Cerberus.Data.Tests/FolderCreateStoreTests.cs; additional focused partial tests/helpers only if useful.
**Interfaces:** Consume Task1; implement IFolderCreateStore using existing IDbContextFactory<AppDbContext>. No schema change or new cross-resource abstraction.

- [ ] Write real PostgreSQL/native fixtures BEFORE store: account-wide root/parent/multiple profiles, selected root/direct/descendant/own-collection parent, exact new direct ProfileFolder projection, no foreign RO/RW parent creation, no other selection/empty selected expansion. Assert new exact encrypted row/ownership/revision1/sequence/time; direct profile/immediate parent advance once with unchanged bytes/wrappers/time; ancestors/other profiles/resources/sessions untouched.
- [ ] Add active/missing/foreign/trash/typed-terminal account/profile/parent/ancestor, selected scope precedence before unused corruption, complete ancestry/cycle, parent/direct-profile counter corruption503/saturation409; typed reserved folder active/trash/foreign/terminal409 and all other kind same-UUID survival. Current invalid access policy/generation/issue/expiry/revocation/dangling selection403 and disabled renewal/null expiry success.
- [ ] Add actual duplicate same/cross-owner winner, profile association and trash winners, expiry after four lock classes plus final INSERT; late terminal account/profile/folder reservation; link/post-insert/parent failure rollback and same-ID retry; unsafe global sequence/exhaustion; needed versus irrelevant ancestor time/cipher counters. Caller cancellation and dead dependency mapping.
- [ ] Run focused Data tests; Expected: missing FolderCreateStore compile RED before product.
- [ ] Implement current transaction/admission/final INSERT and atomic direct links/profile/immediate-parent structural updates. Selected parent route combines ProfileFolder and own active ProfileCollection/CollectionFolder along complete same-owner ancestry; never consume foreign grants. Typed terminal checks at every relevant predicate and final statement. Resolve hidden state before reporting necessary corrupt metadata; no ciphertext parse for unused parent/ancestor content. Floor UTC before binding; safe metadata validation before commit. Unique/sequence exhaustion409, dependency503, caller cancellation propagates.
- [ ] Run focused matrix, inspect failures and correct root causes with observed RED→GREEN; run whole Data family. Expected: zero failures/skips, no partial state or widened visibility. Commit `feat: create scoped owned folders` and task-done audit fresh whole-family result.

### Task 3: Strict command boundary

**Files:** Create src/Application/ArturRios.Cerberus.Command/Folders/CreateFolderCommand.cs, CreateFolderValidator.cs, CreateFolderHandler.cs, CreateFolderOutput.cs, FolderCreateMessages.cs and tests/Application/ArturRios.Cerberus.Command.Tests/FolderCreateHandlerTests.cs.
**Interfaces:** Consume Task1/CreateAsync store; expose CreateFolderCommand.SetContext(Guid actor,string? access), ToInput(), Handler with IValidator<CreateFolderCommand>/IFolderCreateStore, exact four-field CreateFolderOutput. Task4 consumes these classes and status map.

- [ ] Write command tests BEFORE product: strict required4/optionalparent, forged context/plaintext rejection, input/self-parent validation, trusted actor/current opaque handle hash/cancellation, safe error allowlist, known statuses, null/error-with-data/wrongid/revision/unsafe-sequence/offset/default/not-floored-or-mismatched-time output rejection. Valid initial revision1 and exact floorµs returns folder_created201/four fields.
- [ ] Run focused command tests; Expected: missing command/handler compile RED.
- [ ] Implement command/validator/handler/messages/output. Missing actor401, missing handle401, invalid hash/input400; store receives verifier only, caller ct unchanged. Any malformed result safely503 with no payload. Status map includes identity_unavailable503.
- [ ] Run focused then whole Command family; Expected: zero failures/skips. Commit `feat: validate folder creation requests`; task-done audit fresh whole-family result.

### Task 4: Actual HTTP and delivery

**Files:** Create src/Presentation/ArturRios.Cerberus.WebApi/Controllers/FoldersController.cs and tests/Presentation/ArturRios.Cerberus.WebApi.Tests/FolderCreateHttpTests.cs; modify Startup DI, README, CHANGELOG, docs/contracts/openapi.json; create docs/content/api/folders.md and relevant focused operations documentation.
**Interfaces:** POST /api/folders consumes Task3 command with current actor/access context and VaultProofBody; explicit validator/handler/store DI. Output/statuses exactly as spec.

- [ ] Write actual native/Heimdall/PostgreSQL HTTP tests BEFORE route: account-wide encrypted root/multiple profiles/parent, actual UC15 selected Master/PerProfile root/direct/descendant/own-collection parent, foreign RO/RW404, strict required/body/identifiers/transport/query/header/no-store/current identity/session/lifecycle, concurrent duplicate winning state and rollback retry, min/pre2000/max times, typed collisions and direct profile get/list projection.
- [ ] Prove complete protection-rotation inventory includes a newly HTTP-created folder; omitted inventory rejects before consuming challenge, complete rotates native content and preserves editedAt/parent/profile links. Prove existing record creation/read uses a newly created folder ancestry.
- [ ] Run focused HTTP; Expected: missing endpoint failures before controller/DI.
- [ ] Implement focused controller [VaultProofBody], one header/no query, trusted current actor and status mapping; explicit DI. Run focused HTTP; Expected: all pass zero failures/skips.
- [ ] Document actual contract/failures/inventory/typed reservation/atomic additive metadata. README UC23 Done and M03 15/26; add CHANGELOG entry. Generate/check OpenAPI exact route/required4/optional nullable parent/output4/statuses. Shared generator preexisting requiredness/empty413 Minors remain visible, no unrelated repair.
- [ ] Run locked restore, direct/transitive dependency/native OSV audits, native corpus four directions/helpers, Python helpers, requirement consistency, OpenAPI write/drift/shape, full branch-range whitespace. Run `python3 scripts/coverage.py` fresh/unfiltered; Expected: all six families pass zero failures/skips and six production assemblies >=90% line. Read actual report and all command exits; commit `feat: expose folder creation endpoint` and task-done audit fresh coverage log.
- [ ] Execute ONE fresh whole-branch ordinary review with exact plan Review Focus and all rulings; grade actual effects and all Declined lines. One blocking TDD pass/full suite if needed, no rereview; record exhaustive Minors and rulings. Push/open PR closing24, Testing, exact-head three required checks SUCCESS, fresh develop/rules/MERGEABLE+CLEAN/clean tested HEAD and user SHA. Normal match-head squash merge; issueClosed/nineDoD/projectDone/remoteabsent/develop tree==tested tree; archive own evidence before own scratch cleanup. Continue UC24.
