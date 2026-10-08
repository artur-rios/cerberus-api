# UC09 Create profile implementation plan

> REQUIRED: executing-plans inline, TDD, one final ordinary whole-branch review. Existing batch authorization replaces approval gates.

**Goal:** issue10 owned encrypted profile creation and complete-profile rotation integration.
**Spec:** docs/superpowers/specs/2026-10-08-uc-09-create-profile-design.md.
**Architecture:** strict Domain contracts/native recipient author verification; EF profile + global sequence; transactional owner store; mediator command/controller.

## Global Constraints

- No server plaintext/root/private key/password, no new crypto construction; existing native recipient bytes/pinned keys.
- Active owner/current account-wide access; account/session lock order and statement-time INSERT permission.
- Safe monotonic sequence/revision/public ID reservation; no hidden disclosure; full AF01–05.
- Rotation inventory includes every owned profile, atomic with account/protection/proof; rewrap unchanged.
- Full unfiltered0fail0skip/line>=90/reportbranch and one scoped final review/green CI before merge.

## Review Focus

- Valid author signature with changed trusted owner/resource/epoch/recipient/pin is rejected.
- Natural access expiry waiting for either account or session lock creates nothing.
- Nonexistent or foreign relationships/owner never disclose another profile.
- Duplicate ID concurrent creates have one winner; persistence failure leaves no partial profile.
- Rotation missing/extra/foreign/stale profile or wrong wrapper leaves challenge and all ciphertext unchanged.
- PerProfile password wrapper rotation preserves mode/verifier, uses fresh slot/context; master rewrap preserves all profiles.

### Task 1: Native owner wrapper and profile contracts

**Files:** Domain/Profiles/Profile.cs, ProfileContracts.cs; Domain/Protection/RecipientEnvelope.cs, ProtectionChange.cs; Domain.Tests/ProfileContractsTests.cs; TestSupport/ProtectionFixture.cs.
**Interfaces:** RecipientEnvelope.IsValid/SigningBytes(owner,kind,id)/Verify(owner,kind,id,epoch,grantId,revision,identity,recipient,author); ProfileKeyWrappers.IsValid/IsBound/IsValidRotation; ProfileCreateInput(ProfileId,Envelope,KeyWrappers,EditedAt,RecordIds,FolderIds,CollectionIds).IsValid; ProfileCreateRequest(Actor,AccessVerifier,Input); ProfileCreateDetails(ProfileId,Revision,ServerSequence,EditedAt); IProfileCreateStore.CreateAsync(request,ct) returns VaultResult<ProfileCreateDetails>. ContentReplacement adds optional ProfileKeyWrappers? KeyWrappers=null. Profile entity carries common fields and serialized wrappers.
- [ ] Native signature/context/SEC1/base64/length/schema, master/per-profile required-wrapper, IDs/UTC/epoch and rotation inventory contracts RED; implement full Domain GREEN, commit/ledger.

### Task 2: Transactional profile persistence and complete rotation

**Files:** Data/Profiles/ProfileCreateStore.cs; AppDbContext/migration; VaultProtectionChangeStore; Data.Tests/ProfileCreateStoreTests.cs, ProfileRotationTests.cs.
**Interfaces:** IProfileCreateStore as Task1; global cerberus.server_sequence. Existing IVaultProtectionChangeStore adds exact profile inventory and writes after existing native proof validation.
- [ ] PostgreSQL creation/access variants/relationships/ID collision+tombstone/lock expiry/concurrency/outage/cancel/rollback and complete rotation/retained bytes RED.
- [ ] Implement account/session locking and statement-time INSERT authorization, strict trusted owner-wrapper validation, unique conflict mapping, sequence/migration. Extend atomic rotation validation and writes. Full Data GREEN, commit/ledger.

### Task 3: Strict command/HTTP contract and delivery

**Files:** Command/CreateProfile command/validator/handler/output; WebApi/ProfilesController and DI; Command.Tests/ProfileCreateHandlerTests.cs; WebApi.Tests/ProfileCreateHttpTests.cs; docs/README/OpenAPI.
**Interfaces:** seven public command fields matching ProfileCreateInput; trusted internal actor/access SetContext; handler hashes opaque access; output four profile metadata fields; POST /api/profiles with single access header.
- [ ] Command validation/context/error projection RED then GREEN. HTTP absent route RED then strict native owned creation, every AF/current provider/no-store/schema/body/query, one-winner concurrency GREEN.
- [ ] Update docs/README; generate/inspect OpenAPI; full unfiltered suite/coverage/helpers/spec/drift/diff, commit/ledger.
- [ ] Single final ordinary reviewer/fix pass if needed; required CI/pinned merge/issueDone/branch absence/tested tree/archive/sync.
