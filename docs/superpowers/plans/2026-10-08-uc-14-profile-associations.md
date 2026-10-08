# UC14 Profile Associations Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Atomically replace owned profile associations to direct own items and currently accessible collections, with all existing lifecycle integrations.
**Architecture:** Real typed resource/grant/link persistence, current locked admission, single-snapshot visible projections and complete native rotation inventory. Strict command/HTTP boundary preserves immutable ownership and encrypted content.
**Tech Stack:** .NET10, EFCore10, PostgreSQL18.6, existing mediator/native crypto.
**Spec:** docs/superpowers/specs/2026-10-08-uc-14-profile-associations.md

## Global Constraints

- Own isolated feature/uc-14-set-profile-associations, issue15, one PR into develop; preserve primary user files.
- Native opaque content only, no plaintext/usable keys; canonical nonzero lowercase GUIDs, counters1..9007199254740991; UTC microsecond EditedAt minimum10ticks.
- Current statement expiry after waits; account/session/profile then deterministic collection/grant locks, no foreign account/profile locks.
- All six families0fail/0skip, >=90%productionline, branch reported; requiredCI exacthead/MERGEABLE/CLEAN before normalmerge. Independent approval deferral/release gate unchanged.
- First resource schemas integrate create/get/list/trash and complete content/grant rotation in this same PR.

## Review Focus

- Concurrent foreign owner closure or grant revocation must not leave an association admitted from stale permission or leak hidden links in reads.
- Shared read-only grants allow organizing recipient profiles but cannot transfer ownership or modify the owner's profile/content/grant.
- Cross-kind equal GUIDs must resolve typed items independently; same-owner composite FKs must reject direct foreign links.
- Trashing one of multiple profiles must retain underlying content/grants and other profiles' links, snapshot actual IDs and remove only its own links.
- Rotation must include retained resources and active owned grants, exclude received/revoked grants, and reject incomplete native contexts before consuming proof.

### Task 1: Typed resources, grants and membership schema

**Files:** Create Domain/Resources/{VaultRecord,VaultFolder,VaultCollection,CollectionGrant,ProfileRecord,ProfileFolder,ProfileCollection}.cs and Domain/Profiles/ProfileAssociationContracts.cs; modify Data/AppDbContext.cs with focused Configuration/ResourceModel.cs; migration; tests/Domain/.../ProfileAssociationInputTests.cs and tests/Infrastructure/.../ProfileAssociationSchemaTests.cs.
**Interfaces:** ProfileAssociationInput(long ExpectedRevision,Guid[]RecordIds,Guid[]FolderIds,Guid[]CollectionIds).IsValid(); ProfileAssociationRequest(Guid Actor,string AccessVerifier,Guid ProfileId,ProfileAssociationInput Input); ProfileAssociationDetails(Guid ProfileId,long Revision,long ServerSequence,Guid[]RecordIds,Guid[]FolderIds,Guid[]CollectionIds); IProfileAssociationStore.SetAsync(request,ct)->VaultResult<ProfileAssociationDetails>. Resources common owned fields, Folder.ParentFolderId/Record.FolderId?, Collection.KeyEpoch, Grant.PublicId/CollectionId/RecipientAccountId/Access(ReadOnly,ReadWrite)/State(Active,Revoked)/Revision/RecipientKeyEnvelope. Link composite keys and direct same-owner FKs.
- [x] Write input boundary tests and realPG schema tests: distinct typed IDs (sameGUID across kinds valid), null/duplicate/zero arrays invalid, safe revision; actual multiple-profile links, reject direct foreign owner, duplicate links/current grant pair, foreign parent refs; migration. Runfocused; expected missingtype compile RED.
- [x] Implement exact models/config/migration; no getter tests or speculative endpoints. Run wholeDomain and wholeData; expected allpass0skip. Commit.
**Completion:** Typed persistence enforces ownership/cardinality and supports later tasks.

### Task 2: Atomic association admission and replacement

**Files:** Create Data/Profiles/{ProfileAssociationVisibility,ProfileAssociationResolver,ProfileAssociationStore}.cs; tests/Data/.../ProfileAssociationStoreTests.cs.
**Interfaces:** Consumes Task1 models/contracts; produces ProfileAssociationVisibility.Records/Folders/Collections(db,accountId) IQueryable, ProfileAssociationResolver.ResolveAsync(db,accountId,actor,records,folders,collections,ct)->resolved internal IDs/error with deterministic collection/grant SHARE locks and native pin validation; ProfileAssociationStore.SetAsync.
- [ ] Write realPG tests owned/sharedReadOnly/sharedReadWrite/nonempty replacements, multi-profile sets/empty clears, immutable resource/profile bytes/time/owners, selectedscope subset/clear success, new own/shared identifier addition404 without scope expansion, currentaccount/session failures, hidden requested kind/owner/trash/terminal/closing owner/revoked grant, malformednativegrant503, stale/edit/delete/retry races, account/session/profile/collection/grant expiry waits and prewrite expiry, concurrentgrantchange, rollback postmetadata/linkwrite, unsafe/exhausted counters/cancellation/outage. Runfocused; expected missingstore RED.
- [ ] Implement resolver/current native binding and fresh lock-time checks; final raw UPDATE statement_timestamp, selected-session original-membership subset guard and all requested visibility counts, transaction link replacement/newrev/seq. Refuse hidden IDs before writes and preserve winning state. Run wholeData; expected allpass0skip. Commit.
**Completion:** Main and AF01–05 persistence verified, no partial or unauthorized membership.

### Task 3: Nonempty profile create/read/list/trash integration

**Files:** Modify Data/Profiles/{ProfileCreateStore,ProfileReadStore,ProfileListStore,ProfileTrashStore}.cs, Domain/Profiles/ProfileListContracts.cs, Query/Profiles/{ProfileListOutput,ProfileProjection}.cs; tests/Data/ProfileAssociationIntegrationTests.cs, Query tests and existing list HTTP shape assertion.
**Interfaces:** Consumes resolver/visibility; ProfileListRow and ProfileListItem gain nonnull init RecordIds/FolderIds/CollectionIds arrays default[], get Details keeps its arrays. Creation resolves actual sets. Trash snapshots all stored IDs and deletes links in existing transaction.
- [ ] Write failing integration tests actual create with owned/shared sets then get/list same-snapshot visible IDs; revoke/trash/closing/terminal omits links; selectedscope unchanged; invalid list arrays fail503. Actual trash retains underlying bytes/owners/grants and otherprofilelinks while snapshotting actual IDs; failure rolls back link deletion. Runfocused; expected old empty projection/unresolved404 failures.
- [ ] Integrate create admission/finalcurrentvisibility, EF single-query ID projections without N+1, list typed arrays/validation; trash actual snapshot/deactivation. Update existing strict list response expectation to additive9fields. Run wholeData/Query; expected allpass0skip. Commit.
**Completion:** No fake empty links remain and deletion preserves underlying content.

### Task 4: Complete content and active-grant rotation

**Files:** Modify Domain/Protection/ProtectionChange.cs and Data/Protection/VaultProtectionChangeStore.cs, create Domain/Resources/CollectionGrantReplacement.cs; tests/Domain/ProtectionChangeTests.cs plus Data/VaultResourceRotationTests.cs.
**Interfaces:** Extend ContentReplacement kinds; onlyprofileKeyWrappers, additive ProtectionChange.GrantReplacements default[]; CollectionGrantReplacement(Guid GrantId,long ExpectedRevision,RecipientEnvelope KeyEnvelope). Existing old requests unchanged for empty new inventory; rewrap requires both manifests empty.
- [ ] Write native realPG fullaccount/profiles/record/folder/collection/activeownedgrant rotation incltrash; missing/extra/duplicate/foreign resources/grants, received/revoked grants not authorized, stale/corrupt current/new epoch/native recipient/author/resource/grant/revision, no proof consumption on validation failure, fresh salts/nonces, rollback afterconsume, preserveEditedAt/owner and monotonics, rewrap byte retention. Runfocused; expected old domain/store rejects new manifest RED.
- [ ] Extend complete inventory/validation before proof consume, validate current/replacement native bindings against recipient/owner pins, atomic resource/grant updates/counters/collectionepoch and signedbody comparison. Run wholeDomain/Data; expected allpass0skip. Commit.
**Completion:** New content/grants cannot escape or undermine existing rotation guarantees.

### Task 5: Strict command and result

**Files:** Create Command/Profiles/SetProfileAssociations{Command,Validator,Handler,Output,Messages}.cs, tests/Command/SetProfileAssociationsHandlerTests.cs.
**Interfaces:** SetProfileAssociationsCommand body4fields/context(actor,access,profileId), consumes IProfileAssociationStore; output6fields; status200/400/401/403/404/409/503.
- [ ] Write trusted request/hash/currentcontext/input tests, success exact sets/newrev/safe seq, storeerror/cancel, corrupt/null/outputIDs/set mismatch503nullpayload. Runfocused; expected missingcommand RED.
- [ ] Implement strictJSON/validator/failclosedhandler and stable statuses; wholeCommand expected allpass0skip. Commit.
**Completion:** Body cannot forge context or return malformed dependency state.

### Task 6: Actual HTTP associations and verified delivery

**Files:** Modify ProfilesController.cs/Startup.cs, README/CHANGELOG/OpenAPI; create WebApi.Tests/ProfileAssociationHttpTests.cs and docs/operations/profile-associations.md; update native protection operations docs with complete inventory.
**Interfaces:** PUT/api/profiles/{id}/associations strict4fieldbody/6fieldresponse; list gains3arrays.
- [ ] Write actualHTTP/PG/nativeidentity tests owned and shared sets/read-only profile organization/get/list/trash/immutableownership, hidden resources/profile/currentaccess, no scope escape, stale/concurrent/retry, strictroute/header/query/body, identity/persistence failures, actual response/status/no-store and rollback. Runfocused; expected missingroute404/405.
- [ ] Implement endpoint/DI; focusedHTTP expected allpass0skip. Update truthfuldocs/backlog M03six/changelog/actualOpenAPI. helper23/spec/drift/diff pass; fullunfiltered sixfamilies0fail/0skip >=90%productionline/reportbranch. Commit.
**Completion:** One ordinary final review/oneTDDblockerfixpass, every declined scope cost ruled/minors retained; requiredCI strictcheckednormalmerge, issue15Done/remoteabsent/testedtree/userSHA/archive thenUC15.
