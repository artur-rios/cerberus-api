# UC13 Delete Profile Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.

**Goal:** Recoverably trash an owned active profile with durable operation metadata and30day deadline.
**Architecture:** Transactional authorized profile transition, durable trash operation/membership and retention queue, strict command/HTTP boundary. Preserve opaque bytes and underlying content, revoke selected handles.
**Tech Stack:** .NET10, EFCore10, PostgreSQL18.6, first-party mediator/output.
**Spec:** docs/superpowers/specs/2026-10-08-uc-13-delete-profile.md

## Global Constraints

- Isolated feature/uc-13-delete-profile, issue14, onePR into develop; preserve original user files/currentdocs.
- Recoverabletrash only; no terminal erasure. Exact UTC720hour window; no plaintext/keys; no-store.
- Counters1..9007199254740991; current-statement expiry after locks.
- Fullunfiltered0fail/0skip >=90%productionline, reportbranch. Independent approvals development-only deferred, release gate unchanged.
- Future typed purge/restore/list belong UC50–53; require complete integration/collision repair before their release.

## Review Focus

- Profile trash must revoke selected handles so restore cannot revive old sessions; preserve unrelated/accountwide sessions.
- Expiry during any lock wait, including stale expected revision, and immediately prewrite denies without state change.
- Profile/edit/delete races retain exactly one winning transition; no duplicate operation/deadlineextension after retry.
- Profile ciphertext/client edit time/wrappers remain unchanged; actual association snapshot currently empty and future content must survive.
- Postwrite entry/queue failures roll back profile/session/metadata; durable expiry work must not pretend an absent purge handler exists.

### Task 1: Durable atomic profile trash

**Files:** Create src/Domain/ArturRios.Cerberus.Domain/Trash/{TrashOperation,TrashEntry,ProfileAssociationSnapshot}.cs, src/Domain/ArturRios.Cerberus.Domain/Profiles/ProfileTrashContracts.cs, src/Infrastructure/ArturRios.Cerberus.Data/Profiles/ProfileTrashStore.cs, EF migration; modify Data/AppDbContext.cs; create tests/Infrastructure/ArturRios.Cerberus.Data.Tests/ProfileTrashStoreTests.cs.
**Interfaces:** Produces ProfileTrashRequest(Guid Actor,string AccessVerifier,Guid ProfileId,long ExpectedRevision), ProfileTrashDetails(Guid ProfileId,Guid TrashOperationId,long Revision,long ServerSequence,DateTimeOffset DeletedAt,DateTimeOffset PurgeAt), IProfileTrashStore.TrashAsync(request,ct)->VaultResult<ProfileTrashDetails>; TrashOperation/TrashEntry durable typed membership; ProfileAssociationSnapshot(Guid[]RecordIds,Guid[]FolderIds,Guid[]CollectionIds).
- [x] Write realPG tests for owned bothmodes: exact retained opaque bytes/EditedAt and newrevision/sequence, durable operation/entry/empty snapshot/exact30days/queue, selectedsessionsrevoked while unrelated/accountwide survive/read-list omit. Hidden/currentaccount/session/scope states, stale/edit/delete/retry races; expiryaccount/session/profile locks inclstalerevision and prewrite; rollback postwrite/entry/queue, outage/cancel/counterexhaustion/invalidinternal. Runfocused; expected missing-store/type compile failure.
- [x] Implement schema/entities and generate reviewed migration. Implement account→session→profile locks and fresh postlockcheck, currentpredicate UPDATE using statement_timestamp and720hour interval. Validate result ordering then persist operation/entry/queue and revoke ownedselectedsessions in same transaction; no envelope decrypt/replace or irreversibleerasure.
- [x] Run wholeDatafamily including migration/rotation regressions; expected all pass0skip. Commit.
**Completion:** main/AF01–05 persistence and schema proven, no partialstate.

### Task 2: Strict delete command and output

**Files:** Create src/Application/ArturRios.Cerberus.Command/Profiles/DeleteProfile{Command,Validator,Handler,Output,Messages}.cs; tests/Application/ArturRios.Cerberus.Command.Tests/DeleteProfileHandlerTests.cs.
**Interfaces:** Consumes IProfileTrashStore; produces DeleteProfileCommand(ExpectedRevision;SetContext(actor,access,profileId)), DeleteProfileHandler/Output6metadatafields and DeleteProfileMessages200/400/401/403/404/409/503.
- [x] Write trustedcontext/input/hashedhandle tests; successmetadata/errorforward/cancellation; null/substitutedID/counters/operationID/timestamp/expiry503nullpayload. Require newrevisionexpected+1, UTCnondefaultmicroseconds, exact30day window and positive safe sequence. Run; expected missingcommandcompile failure.
- [x] Implement strictJSON onebodyfield/validator/failclosed handler and statusmap.
- [x] Run wholeCommandfamily; expected pass0skip. Commit.
**Completion:** no forgedowner/scope/bodyID and no partial bad dependency output.

### Task 3: HTTP deletion and verified delivery

**Files:** Modify src/Presentation/ArturRios.Cerberus.WebApi/Controllers/ProfilesController.cs, Startup.cs, README.md, CHANGELOG.md, docs/contracts/openapi.json; create ProfileTrashHttpTests.cs and docs/operations/profile-trash.md.
**Interfaces:** DELETE /api/profiles/{id}, strictbodyexpectedRevision, singleaccessheader;200metadata6fields or400/401/403/404/409/503.
- [ ] Write actualHTTP/PG/Heimdall tests for owned/scoped deletion and retainedbytes/operation/queue/no-store, subsequentread/list/selected revocation, unrelatedpreservation, hidden404/currentpermission, strictpath/query/header/body, identityoutage/persistencefailure, stale/retry/conflict. Run; expected missingDELETE405.
- [ ] Implement VaultProofBody endpoint/trustedcontext/DI; focusedHTTPexpected pass0skip.
- [ ] Update operations/backlog/Unreleased changelog (include UC12 catch-up dueparallelnewpolicy), actualOpenAPI generation/drift/helper/spec/diff. Fullunfiltered includes wholeWeb;0fail/0skip >=90%line, branches reported. Commit.
**Completion:** allflows implemented, explicit futurepurge/association obligations; oneordinaryfinalreview, oneTDDfixpass ifblocking; strictchecked requiredCIgate thennormalmerge issue14Done/archive/UC14.
