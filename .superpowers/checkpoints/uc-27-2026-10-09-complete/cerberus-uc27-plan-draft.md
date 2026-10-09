# UC-27 Delete folder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans task-by-task inline. Steps use checkbox (`- [ ]`) syntax. One fresh whole-branch review after all tasks.

**Goal:** Atomically trash one owner-visible folder and its active subtree as one retained720hour operation.
**Architecture:** Folder trash Domain/Command contracts; current folder-only authority, recursive captured inventory, ordered locks and guarded transactional cascade; strict DELETE and explicit DI. Reuse typed trash/retention foundations without migrations.
**Tech Stack:** Existing .NET10/C#/EF Core/Npgsql/PostgreSQL/Mediator/FluentValidation/Output/xUnit/Moq/native helpers.
**Spec:** `docs/superpowers/specs/2026-10-09-uc-27-delete-folder.md`

## Global Constraints

- Exact required expectedRevision body and folderId/trashOperationId/revision/serverSequence/deletedAt/purgeAt output; canonical path/header/noquery/strict UTF8 bounded JSON/VaultProofBody/no-store/actualempty413.
- Owner-only current folder PF/CF scope; record-only routes excluded; all relevant foreign native contributors validate before visible403; hidden404 precedes stale/corrupt content. Preserve nonnull selected ProfileId.
- One root operation, typed entry per admitted active folder/record and one retention item; common UTC microsecond720hour timestamps; retain opaque bytes/edit times; never add independently trashed/terminal items or extend their deadlines.
- Own account UPDATE/session UPDATE, ordered own profiles/collections NO KEY UPDATE, captured folders+external parent UPDATE, records UPDATE, typed associations UPDATE; no foreign account/profile/grant or upward-chain locks. Reload complete inventory and final guarded current authority.
- Safe positive counters<=9007199254740991; all per-member/parent writes guarded and verified; transactional rollback; typed terminal/trash identities; snapshots all stored direct associations; distinct active external parent bump once.
- Fresh unfiltered six production families>=90line/aggregatebranch reported; lockedrestore/audits/native/spec/OpenAPI/rangechecks/ONEreview/exact-headCI; independent formal/client qualification development-only deferred.

## Review Focus

- A selected owner deleting a visible root must cascade active contained items without resurrecting or changing independently trashed/terminal items, even when records are shared elsewhere.
- Owner creation/move/rotation and recipient folder/record edits competing with subtree collection/content locks must preserve winners without cross-account or leaf-to-root deadlocks.
- Expiry or visibility change during any lock wait or immediately before the root guard must deny before stale metadata is disclosed and never widen selected access.
- New child/record/link inventory or a changed native contributor must fail safely rather than deleting an uncaptured item, snapshotting an unheld parent or accepting hidden/record-only authority.
- Any child/link/parent/trash/queue failure or exhausted/regressed sequence must roll back the complete cascade; every member shares one exact deadline and retained relationships permit only later revalidated restore.

### Task 1: Strict recoverable folder deletion command

**Files:** Create `src/Domain/ArturRios.Cerberus.Domain/Folders/FolderTrashContracts.cs`; create `src/Domain/ArturRios.Cerberus.Domain/Trash/FolderAssociationSnapshot.cs`; create `src/Application/ArturRios.Cerberus.Command/Folders/{DeleteFolderCommand,DeleteFolderValidator,DeleteFolderHandler,DeleteFolderOutput,DeleteFolderMessages}.cs`; test `tests/Application/ArturRios.Cerberus.Command.Tests/DeleteFolderHandlerTests.cs`.
**Interfaces:** `FolderTrashRequest(Guid Actor,string AccessVerifier,Guid FolderId,long ExpectedRevision)`; `FolderTrashDetails(Guid FolderId,Guid TrashOperationId,long Revision,long ServerSequence,DateTimeOffset DeletedAt,DateTimeOffset PurgeAt)`; `IFolderTrashStore.TrashAsync(FolderTrashRequest,CancellationToken):Task<VaultResult<FolderTrashDetails>>`; `FolderAssociationSnapshot(Guid? ParentFolderId,Guid[] ProfileIds,Guid[] CollectionIds)`. Strict DeleteFolderCommand SetContext(Guid,string?,Guid); handler/output/messages follow DeleteRecord contract with folder_deleted and six folder metadata fields.

- [ ] Write DeleteFolderHandlerTests from genuine DeleteRecord handler behavior: GivenInvalidAuthorityOrRevision_WhenHandling_ThenRejectBeforePersistence; GivenValidTrashMetadata_WhenHandling_ThenForwardTrustedContextAndReturnExactSixFields asserts six literal sorted fields/hash/exacttoken, expected+1 and UTC720hours; invalid/errorwithdata/nullstore/malformed outputs/safeallowlist/caller cancellation. Reject actor/vaultAccess/folderId/parentFolderId/recordId/profileIds/collectionIds/envelope/editedAt/trash overrides, missing/duplicate/case/numeric expectedRevision. ReviewFocus3/5 boundary.
- [ ] Run `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests --no-restore --filter FullyQualifiedName~DeleteFolderHandlerTests > /tmp/cerberus-uc27-command-red.log 2>&1`. Expected missing named folder trash/command types; no test-installation errors/zero-match.
- [ ] Implement exact contracts/handler/validator/output/messages preserving record-trash result validation, current trusted context hashing and no output on unsafe result. Typed folder snapshot carries original parent/profile/collection public IDs only.
- [ ] Run focused command-green.log then whole `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests --no-restore > /tmp/cerberus-uc27-command-whole.log 2>&1`. Expected all pass zero fail/skip.
- [ ] Commit `feat: define folder trash contract`; task-done actual whole Command audit, checkbox and ledger.

### Task 2: Guarded owner-only recursive trash transaction

**Files:** Create `src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderTrashStore.cs`; test `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/FolderTrashStoreTests.cs`.
**Interfaces:** Consumes Task1 request/details/store/snapshot and existing RecordAssociationSnapshot/TrashOperation/TrashEntry/RetentionWorkItem; produces `FolderTrashStore(IDbContextFactory<AppDbContext>)`.

- [ ] Write real PostgreSQL cases for visible owner wide/directPF/ancestorPF/directCF/ancestorCF scope; a root+children+records mixed subtree yields common720hours, exact one operation/many typed snapshots/one queue, every revision/sequence advances, bytes/time/ownership retained, direct links detached, external active profile/collection/rootparent bumped once and unrelated/recipient snapshots unchanged. Snapshot inactive associations; independently trash a record through RecordTrashStore and a child through an earlier FolderTrashStore call, reload root expected revision, then assert later root operation excludes them and preserves original deadlines; terminal items remain untouched; typed aliases safe. ReviewFocus1/5.
- [ ] Add actual native RO/RW folder-only visible denial403, record-only granted/selected RecordRead success first then404, malformed overlapping contributors503/selection isolation. Cover current account/session/profile lifecycle, hidden corruption/stale precedence, cycle503, structural counter/time/purge/oldentry/exhaustion errors and damaged owned opaque deletion. ReviewFocus3/4.
- [ ] Add actual every-phase lock-wait expiry plus final-root guard expiry/typed terminal; captured inventory growth via existing detached child/record reparenting or existing unheld profile/collection link insertion before content locks returns409 without partial operation; new entity insertion would block on the strong account FK lock and is tested through actual owner creation serialization. Same-root concurrent/lostretry one winner/404. Actual recipient FolderUpdateStore and RecordUpdateStore interleavings both orders, actual FolderCreateStore/RecordCreateStore/RecordMoveStore/ProfileAssociationStore owner serialization, complete VaultProtectionChangeStore rotation race and retained cascade inventory include/omit proof. EXPLAIN root1/bounded subtree among unrelated deep folders and verify only captured lock set/no foreign account/profile/grant/upward chains. ReviewFocus2..4.
- [ ] Add stage-specific faults AFTER root, child, record, links, external parent, operation, entry and queue; whole graph snapshots rollback and clean retry succeeds. Exhausted/regressed root/member/parent sequence; outage/internalexceptions versus caller cancellation. ReviewFocus5.
- [ ] Run focused FolderTrashStoreTests to data-red.log before store. Expected missing FolderTrashStore only; exclude any installer/fixture mistakes from runtime RED.
- [ ] Implement current FolderUpdateStore authority adapted for owner-only trash. Strong own account lock serializes owner writers; capture active recursive descendants/direct associations; ordered lock/reload/complete inventory comparison; final root authority+originalmetadata/inventory guard; same root timestamps guarded children/records; typed snapshots/direct-link removals/distinct active parent bumps/one operation and queue; result verification before commit/rollback/safe errors. No new migrations/dependencies/decryption.
- [ ] Run focused data-green.log then whole `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --no-restore > /tmp/cerberus-uc27-data-whole.log 2>&1`. Expected all pass zero fail/skip; genuine native/race/snapshot/plan assertions.
- [ ] Commit `feat: trash owned folder cascades`; task-done actual whole Data audit/checkbox/ledger.

### Task 3: Native DELETE integration and fresh whole-branch validation

**Files:** Modify FoldersController.cs/Startup.cs in `src/Presentation/ArturRios.Cerberus.WebApi`; test `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/FolderTrashHttpTests.cs`; modify `docs/security/folder-api.md`, README.md, CHANGELOG.md, `docs/requirements/Operations & Infrastructure Document.md`, `docs/operations/foundation-status.md`, generated `docs/contracts/openapi.json`.
**Interfaces:** DELETE consumes Task1 DeleteFolderCommand/Handler/Validator/Output/Messages and Task2 IFolderTrashStore/FolderTrashStore; three explicit Startup registrations. Existing POST/list/GET/PUT stay functional.

- [ ] Write native HTTP tests before route/DI: actual POST folders+records and native Master/PerProfile selection; complete cascade exact six output/one operation/timestamps/typed snapshots/cipher preserve/parentbumps; independently trashed items; actual native RO/RW/record-only/privateparent denial and corrupt contributor503; recipient folder/record GET/list disappear with direct shares unchanged. Strict body/path/header/query/transport/forged cascade overrides/no-store/currentidentity/expiry/selection/stale/lostretry/necessarycorruption/outage. ReviewFocus1..5 HTTP boundaries.
- [ ] Run focused FolderTrashHttpTests to http-red.log. Expected installed missing DELETE route405 after all actual prerequisites succeed; shared middleware cases may already pass.
- [ ] Add focused [HttpDelete("{id}")]/[VaultProofBody] action matching RecordsController.Delete plus store/validator/handler DI; focused http-green.log all pass.
- [ ] Update folder-api/currentcascade/retention/restore/sync limits docs; READMEUC27Done/M03 **19 / 26 closed**; OpenAPIwrite+drift/exactexpectedRevision/sixoutput/statuses+retainedroutes. Shared schema Minors retained accurately.
- [ ] Run lockedrestore/NuGetdirect+transitive/nativeOSV153/nocorpusregression322x4/nativehelpers36/Pythonhelpers23/specs/rangewhitespace including alladdedfiles and `python3 scripts/coverage.py > /tmp/cerberus-uc27-coverage.log 2>&1`. Serialize .NET/native; expected unfiltered sixfamilies zerofail/skip, actual sixassemblies>=90line and aggregatebranch reported, freshbase/rules/primarySHA preserved.
- [ ] Commit `feat: expose recoverable folder deletion`; task-done actual coverage audit/checkbox/ledger. Mandatory sole whole-branch review and delivery follow implementation completion: package originalbase..testedHEAD/fulltemplate/spec/plan/fiveverbatimFocus/ALLRulings/real evidence; one blocking TDD correction pass if needed/fullsuite, Minors deferred/no secondreview/all declined FinalRulings reason+cost.

## Delivery

Exhaustive all rulings/minors PRbody/archive. Normalpush/PRdevelop closes28/projectTesting; latestexactHEADbranch-policy/test/dockerSUCCESS/freshbase/rules0approvals/MERGEABLE+CLEAN/primarySHA; normal matchHEAD squash. Actualissue28Closed9DoD/Done/remoteabsent/mergedREADME19of26/testedtree; hashverifyALLownarchive beforeonlyownSDDcleanup. Retain userfiles/worktrees; UC28issue29 next.
