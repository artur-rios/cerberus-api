# UC-26 Update folder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans task-by-task inline. Steps use checkbox (`- [ ]`) syntax. One fresh whole-branch review after all tasks.

**Goal:** Safely replace currently writable encrypted folder content with expected-revision conflict protection.
**Architecture:** Dedicated Domain/Command contract; current folder-only target+self authority and final guarded transactional write adapted from record update; strict PUT and explicit Startup DI.
**Tech Stack:** Existing .NET10/C#/EF Core/Npgsql/PostgreSQL/Mediator/FluentValidation/Output/xUnit/Moq/native helpers; no schema/dependency change.
**Spec:** `docs/superpowers/specs/2026-10-09-uc-26-update-folder.md`

## Global Constraints

- Exact body expectedRevision/envelope/editedAt; exact output folderId/revision/serverSequence/editedAt. Canonical path/header/noquery/bounded strict UTF8 JSON/VaultProofBody/no-store/common empty413.
- Safe positive counters<=9007199254740991, UTC>=10ticks/floorµ; same epoch; changed cipher fresh keySalt AND nonce; same envelope allowed; no ownership/parent/link/child mutation.
- Complete active same-owner target+self ancestry; folder-only PF/CF scope, no PR/CR authority; current typed terminals/selectedProfileId preserved, every foreign contributor native-verified with party pins once.
- Own account NO KEY UPDATE/session+selection UPDATE, ordered collections/grants SHARE, only target folder NO KEY UPDATE; no foreign account/profile or upward/descendant locks. Full current authority inside write with expectedrev/oldseq/envelope/heldsets.
- Main+AF01..05; TDD actual contracts/store/HTTP RED; fresh full six-family >=90line, all ordinary checks/ONE reviewer/exact-headCI. Independent formal/client release approval development-only deferred.

## Review Focus

- Record-only selected/native grant routes must never authorize folder updates, and a member-leaf recipient cannot edit an otherwise hidden parent/sibling or change organization through forged body fields.
- Permission/expiry/ancestor movement/terminal or owner native rotation during lock waits or just before the UPDATE must prevent stale writes without widening a dangling selection.
- Every eligible overlapping foreign contributor must be native-verified even alongside valid RW scope; unselected/unrelated corrupt grants remain irrelevant and new unheld authority returns conflict.
- Reciprocal foreign edits and owner creation/rotation competing on the target folder must avoid foreign account/profile or upward lock cycles and preserve exactly one winning revision.
- Invalid stored counters/time/envelope, sequence exhaustion or injected failure after UPDATE must return safe errors and rollback row changes, preserving unrelated metadata/associations and retry semantics.

### Task 1: Strict folder update contract and command

**Files:** Create `src/Domain/ArturRios.Cerberus.Domain/Folders/FolderUpdateContracts.cs`; create `src/Application/ArturRios.Cerberus.Command/Folders/{UpdateFolderCommand,UpdateFolderValidator,UpdateFolderHandler,UpdateFolderOutput,FolderUpdateMessages}.cs`; test `tests/Application/ArturRios.Cerberus.Command.Tests/UpdateFolderHandlerTests.cs`.
**Interfaces:** `FolderUpdateInput(long ExpectedRevision,EncryptedEnvelope Envelope,DateTimeOffset EditedAt).IsValid()`, `FolderUpdateRequest(Guid Actor,string AccessVerifier,Guid FolderId,FolderUpdateInput Input)`, `IFolderUpdateStore.UpdateAsync(FolderUpdateRequest,CancellationToken):Task<VaultResult<FolderCreateDetails>>`. `UpdateFolderCommand` strict required body+private context SetContext(Guid,string?,Guid), ToInput(); handler/validator/output/messages mirror RecordUpdate names and exact four folder fields.

- [x] Write GivenInvalidAuthorityOrInput_WhenHandling_ThenRejectBeforePersistence and GivenValidReplacement_WhenHandling_ThenForwardTrustedContextHashAndReturnExactFourMetadataFields based on actual UpdateRecordHandlerTests; include literal output fields, counter/µtime/target identity, strict body required/duplicate/numeric behavior, malformed store/errorwithdata/allowlist and caller cancellation. ReviewFocus5 handler boundary.
- [x] Run `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests --no-restore --filter FullyQualifiedName~UpdateFolderHandlerTests > /tmp/cerberus-uc26-command-red.log 2>&1`. Expected missing named folder update types, no fixture typo/zero match.
- [x] Implement named signatures following existing record-update validation/error/result logic, folder_updated and four-field metadata. Expected no persistence call on invalid input.
- [x] Run focused command-green.log then whole Command command-whole.log with `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests --no-restore`. Expected all pass zero fail/skip.
- [x] Commit `feat: define folder update contract`; task-done audit actual whole Command log; mark ledger/checkboxes.

### Task 2: Current transactional folder-only update

**Files:** Create `src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderUpdateStore.cs`; test `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/FolderUpdateStoreTests.cs`.
**Interfaces:** Consumes Task1 IFolderUpdateStore/Request/Input/FolderCreateDetails; produces `FolderUpdateStore(IDbContextFactory<AppDbContext>)`. Reuses CollectionGrantBinding and unchanged schema.

- [x] Write actual PostgreSQL tests based on RecordUpdateStoreTests with folder-specific target+self ancestry, direct/ancestor PF and CF scopes, actual native RO/RW at root/target/descendants, private parent/sibling and record-only negatives FIRST proving RecordReadStore success. Assert only target content/rev/seq/stamp/time changed, current links/parent/children/unrelated snapshots unchanged. ReviewFocus1/5.
- [x] Cover all current session/selection/typed terminals; hidden corrupt target stale404; every lock-wait natural expiry/current permission revoke/downgrade/member/move/rotation, final guard movement/trash/terminal/expiry and new authority growth409. All contributor signatures/pins/strictnumeric/native failures, unselected/unrelated isolation; safe counters/envelope/time/revision/keyepoch/salt/nonce, post-write fault rollback, exhausted/regressed sequence, caller/internal cancellation. ReviewFocus2/3/5.
- [x] Add concurrent same-revision owned/recipient exactly-one-winner/lost-retry, reciprocal foreign folder updates, ACTUAL FolderCreateStore interleaving with target lock held before upper ancestor lock (legitimate wait, then winning parent revision409), native owner rotation races. EXPLAIN target1+selfactualancestry against100 unrelated folders; assert exactly target folder lock/no foreign accounts/profiles/upwardlocks. ReviewFocus4.
- [x] Run focused FolderUpdateStoreTests to data-red.log. Expected missing FolderUpdateStore before implementation; no fixture syntax failure.
- [x] Implement record-update transaction adapted to UC25 folder-only authority; remove all PR/CR routes, include target itself in ancestry, require complete null-parent terminus. Ordered own/collection/grant/target locks, current reload and all native contributors, final full guarded UPDATE; only folder target row metadata; rollback+safe errors.
- [x] Run focused data-green.log then whole Data data-whole.log via `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --no-restore`. Expected all pass, true race/EXPLAIN assertions.
- [x] Commit `feat: update permitted encrypted folders`; task-done actual whole Data log and mark steps/ledger.

### Task 3: Native PUT integration and complete verification

**Files:** Modify FoldersController.cs/Startup.cs in `src/Presentation/ArturRios.Cerberus.WebApi`; test `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/FolderUpdateHttpTests.cs`; modify `docs/security/folder-api.md`, `README.md`, `CHANGELOG.md`, `docs/requirements/Operations & Infrastructure Document.md`, `docs/operations/foundation-status.md`, generated `docs/contracts/openapi.json`.
**Interfaces:** PUT consumes UpdateFolderCommand/Handler/Output/Messages and IFolderUpdateStore/FolderUpdateStore, explicit Startup registrations and validator. GET/list/POST preserved.

- [x] Write actual native HTTP tests before route/DI; actual POST creation and Master/PerProfile UC15 selection, exact four output fields/native ciphertext and preserved parent/links/children; real native RO/RW memberships, record-only GET success before update404, hidden parent/siblings, overlapping contributors/selection isolation. Strict route/body/transport/header/query, forged organization fields, µminimum/oldertime/nonce+salt/epoch, current identity/session/selection/grant changes, safe necessary corruption/outage, lost retry409/GET winning ciphertext/no-store/no mutation. ReviewFocus1..5 HTTP boundaries.
- [x] Run focused FolderUpdateHttpTests to http-red.log. Expected real installed missing-PUT route failures before route; no fixture typo/zero-match evidence.
- [x] Add focused [HttpPut("{id}")]/[VaultProofBody] action adapted RecordsController.Update and explicit Startup store/handler/validator DI. Run focused http-green.log, expected all pass.
- [x] Update docs current update/permission/conflict semantics; READMEUC26Done/M03 **18 / 26 closed**; OpenAPIwrite+drift/exact body/output/status shape; known shared metadata Minors retained truthfully.
- [x] Run locked restore/NuGet/native audit153/no findings/corpus322x4/36helpers/23Pythonhelpers/specs/full-range whitespace including added files and `python3 scripts/coverage.py > /tmp/cerberus-uc26-coverage.log 2>&1`. Serialize .NET/native builds. Expected fresh unfiltered all six families zerofail/skip, actual coverage six assemblies>=90line/branch reported; primarySHA/currentbase preserved.
- [x] Commit `feat: expose folder update endpoint`; task-done actual full log/ledger/steps. Then own originalbase..testedHEADreviewpackage, ONEfreshgpt-6-astra reviewer/fulltemplate/spec/plan/fiveverbatimFocus/ALLRulings/evidence. Regrade actual effects; one TDD blocking correction pass if needed, Minors deferred/no secondreview/all declined FinalRulings reason+cost.

## Delivery

Exhaustive rulings/minors PRbody/archive. Normalpush/PRdevelop closes27/projectTesting; exactlatestHEADbranch-policy/test/dockerSUCCESS/freshbase/rules0approvals/MERGEABLE+CLEAN/primarySHA; normalsquashmatchHEAD. Verify actualissue27Closed9DoD/Done/remoteabsent/mergedREADME18of26/testedtree==freshdevelop; hashverifyALLownarchivefiles BEFOREonlyownSDDcleanup. Retain userfiles/worktrees; UC27issue28 next.
