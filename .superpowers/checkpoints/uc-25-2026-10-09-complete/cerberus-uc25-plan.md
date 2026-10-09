# UC-25 Get folder Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task inline. Steps use checkbox (`- [ ]`) syntax for tracking. One fresh whole-branch review after all tasks.

**Goal:** Return one currently permitted encrypted folder and only visible organization references.
**Architecture:** Dedicated Domain/Query details contract using existing folder projection; a target-first single SQL permission/ancestry/reference/native-evidence snapshot; strict GET on existing FoldersController and Startup DI.
**Tech Stack:** Existing pinned .NET10/C#/EF Core/Npgsql/PostgreSQL/Mediator/Output/xUnit/Moq/native helpers; no dependency or schema change.
**Spec:** `docs/superpowers/specs/2026-10-09-uc-25-get-folder.md`

## Global Constraints

- Exactly eight fields folderId/revision/serverSequence/editedAt/envelope/profileIds/parentFolderId/collectionIds, nonnull sorted unique public GUID arrays and nullable immediate parent; no internal/private/owner/grant/key disclosure.
- Safe positive integer<=9007199254740991, UTC>=10ticks exact microsecond precision, strict supported native envelope, target-matching ID and no self parent.
- Canonical lowercase dashed nonzero path GUID, one canonical access header, no query or GET body including unknown-length HTTP2/chunked, common empty413 and no-store.
- One current statement_timestamp authority/ancestry/reference/native snapshot, all foreign contributors verified with pins once per account, typed terminal exclusions and selected ProfileId preserved. No record-only folder authority.
- Admit requested target before ancestry; hidden/incomplete404 before corruption/native, relevant fully active cycles503; no locks/writes/unused ciphertext parsing/new dependency/schema/crypto.
- Main and AF01..04, actual native ownership/sharing fixtures, fresh unfiltered six-family coverage >=90% production line, ordinary checks/ONE review/exact-head CI. Independent formal/client qualifications development-only deferred.

## Review Focus

- Direct target profile links or member-leaf grants must not expose an otherwise unshared immediate parent, sibling or owner profile; record-only routes must never expose the folder even when actual record access succeeds.
- Hidden or incomplete target ancestry must return404 before corrupt native/content inspection, relevant fully active cycles503 and unrelated deep/corrupt inventories remain uninspected.
- Every overlapping foreign collection contributing to target or visible parent authority must be native-verified; corrupt unselected/unrelated routes cannot deny permitted content and pins are parsed once per party.
- A selected profile becoming dangling/trashed/terminal or current policy/expiry/grant/member removal must never widen access; after-SQL changes preserve one snapshot and next GET observes removal.
- Public detail arrays and parent must be complete, nonnull/unique/nonzero and correctly typed, and corrupt native envelope/time/revision/sequence or extra GET transport selectors must fail safely without partial payload or mutations.

### Task 1: Strict folder detail contract and query

**Files:** Create `src/Domain/ArturRios.Cerberus.Domain/Folders/FolderReadContracts.cs`; create `src/Application/ArturRios.Cerberus.Query/Folders/{GetFolderQuery,GetFolderHandler,FolderDetailsOutput,FolderReadMessages}.cs`; test `tests/Application/ArturRios.Cerberus.Query.Tests/GetFolderHandlerTests.cs`.
**Interfaces:** Produces `FolderReadRequest(Guid Actor,string AccessVerifier,Guid FolderId)`, `FolderReadDetails(FolderListRow Folder,Guid[] ProfileIds,Guid? ParentFolderId,Guid[] CollectionIds)`, `IFolderReadStore.ReadAsync(FolderReadRequest,CancellationToken):Task<VaultResult<FolderReadDetails>>`, `GetFolderQuery(Guid actor,string? access,Guid folderId)`, `FolderDetailsOutput` eight fields and `FolderReadMessages.StatusCodes/SafeError`. Reuses existing FolderProjection.TryRead.

- [ ] Write GivenInvalidContext_WhenGetting_ThenRejectBeforePersistence, GivenVisibleDetails_WhenGetting_ThenForwardHashAndCallerTokenAndReturnOnlyEightPublicFields and stored-result tests before product types. Adapt actual GetRecordHandlerTests: literal eight folder fields, hash verifier, current token, sorted refs, cross-kind identical GUIDs allowed, null/duplicate/zero arrays and parent zero/self503, all envelope/time/revision/sequence boundaries, broken/unknown/errorwithdata store contract, allowed errors and caller cancellation. ReviewFocus5 covered here.
- [ ] Run `dotnet test tests/Application/ArturRios.Cerberus.Query.Tests --no-restore --filter FullyQualifiedName~GetFolderHandlerTests > /tmp/cerberus-uc25-query-red.log 2>&1`. Expected missing named folder-read/query types before implementation, not fixture syntax errors.
- [ ] Implement exact signatures in named files following existing GetRecord boundaries and folder projection. Success folder_found, safe allowlist and full result validation before attaching output.
- [ ] Run focused tests to query-green.log then `dotnet test tests/Application/ArturRios.Cerberus.Query.Tests --no-restore > /tmp/cerberus-uc25-query-whole.log 2>&1`. Expected focused/whole Query zero failures/skips.
- [ ] Commit `feat: define folder detail contract`; task-done audit actual completed whole Query log and mark steps/ledger RED/GREEN.

### Task 2: Target-first current folder detail snapshot

**Files:** Create `src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderReadStore.cs`; test `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/FolderReadStoreTests.cs`.
**Interfaces:** Consumes Task1 IFolderReadStore/Request/Details and FolderListRow; produces `FolderReadStore(IDbContextFactory<AppDbContext>)`. Reuses unchanged native CollectionGrantBinding and typed schema.

- [ ] Write actual PostgreSQL tests before store based on RecordReadStoreTests with folder-specific fixtures. Own wide/selected direct/ancestor/owned collection/member-leaf/current direct profile and effective collection refs, actual foreign RO/RW root+leaf memberships and private owner refs, separate parent authority, no unrelated siblings/parent. Record-only selected/granted cases must first prove actual RecordReadStore access then folder404. ReviewFocus1 covered.
- [ ] Cover hidden/incomplete/typed terminal ancestry and relevant/unselected cycles; invalid ordering only target, unrelated corruption ignored; all native binding/epoch/signature/strict JSON/pin failures and overlapping contributing corruption/unselected route isolation; pin reuse, no mutations/locks; every current actor/session/selected lifecycle path, expiration before SQL, authority/member/profile removal after one SQL. EXPLAIN ANALYZE with100 unrelated deep folders asserts candidates1 and target+self actual ancestry only. ReviewFocus2/3/4/5 covered.
- [ ] Run `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --no-restore --filter FullyQualifiedName~FolderReadStoreTests > /tmp/cerberus-uc25-data-red.log 2>&1`. Expected missing FolderReadStore before implementation, not fixture typos/zero matches.
- [ ] Implement target-first actor/session/eligiblecollections/candidates/self-upward ancestry/included/scoped/visible/relevantgrants snapshot. Profile refs direct only; parent independent route excludes candidate itself; collection refs effective included only. Capture pins once per party and every grant binding, no later DB read/mutation/lock. Current safe errors/internal timeout/caller cancellation.
- [ ] Run focused tests to data-green.log then `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --no-restore > /tmp/cerberus-uc25-data-whole.log 2>&1`. Expected all focused/whole Data pass zero failures/skips and bounded plan assertions.
- [ ] Commit `feat: read permitted encrypted folders`; task-done audits actual completed whole Data log and ledger evidence.

### Task 3: Native HTTP folder detail and complete verification

**Files:** Modify `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/FoldersController.cs` and `src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs`; test `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/FolderReadHttpTests.cs`; modify `docs/security/folder-api.md`, `README.md`, `CHANGELOG.md`, `docs/requirements/Operations & Infrastructure Document.md`, `docs/operations/foundation-status.md`, generated `docs/contracts/openapi.json`.
**Interfaces:** Consumes Task1 query/output/messages/handler and Task2 store. Produces strict GET `/api/folders/{id}` through QueryMediator, preserving POST/list.

- [ ] Write actual native HTTP tests before endpoint/DI. Create folder via actual POST, current Master/PerProfile UC15 proofs, exact eight fields/time/ciphertext/native RO/RW memberships/current visible refs and hidden parent/siblings/ownerprofiles, record-only negatives first proving actual record GET, overlapping corrupt grant/unselected isolation. Current identity/session/profile/grant/member state, dependency/corrupt metadata/hidden cycles, canonical path/noquery/header/nobody includingHTTP2/unknown length/chunked/empty413, no-store/no mutation. ReviewFocus1..5 covered.
- [ ] Run `dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests --no-restore --filter FullyQualifiedName~FolderReadHttpTests > /tmp/cerberus-uc25-http-red.log 2>&1`. Expected actual installed GET route failures before implementation; no fixture typo or zero-match RED.
- [ ] Add focused GET action adapted from RecordsController.Get and actual Startup explicit store/query-handler DI. Run focused tests to http-green.log. Expected all cases pass; existing POST/list checked again in full suite.
- [ ] Document detail/current visible refs/errors at existing folder-api; ops/foundation/changelog; feature README UC25Done/M03 **17 / 26 closed**. Run OpenAPI write/drift and exact folder detail schema/status/transport shape. Expected actual eight fields, known shared generator Minors truthfully retained.
- [ ] Run locked restore, NuGet direct/transitive audit, native OSV153/no findings/corpus322x4/36helpers, 23Pythonhelpers, verify_specs, complete range whitespace including additions and `python3 scripts/coverage.py > /tmp/cerberus-uc25-coverage.log 2>&1`. Serialize all .NET/native builds. Expected fresh unfiltered six-family zero failures/skips, actual Summary six production assemblies>=90line/branch reported; primary four SHA/currentbase preserved.
- [ ] Commit `feat: expose folder detail endpoint`; audit actual full log for task-done, mark steps/ledger. Implementation complete only; review and integration follow.

## Post-implementation review and delivery

All three task contracts complete -> own review-package originalbase..testedHEAD -> exactly ONE fresh gpt-6-astra fork_none reviewer with full template/spec/plan/five verbatim ReviewFocus/ALLRulings/actual evidence. Regrade actual effects, Critical/Important ONE TDD correction pass+full suite, Minors defer, no second reviewer. Every declined judgment explicit Final Ruling reason/cost. Exhaustive all rulings/minors in PR/archive/linked full report.

Normal push/PR develop closes26, actualTesting; latest exact-head branch-policy/test/dockerSUCCESS, fresh unchangedbase/rules0approvals/MERGEABLE+CLEAN/primarySHA, normal squash --match-head-commit. Verify remote absence/actualissue26Closed9DoD/projectDone/mergedREADMEUC25Done17of26/testedtree==freshdevelop/primarySHA; hash-verify all owned archive files before own SDD cleanup, retain worktrees/userfiles, then UC26issue27.
