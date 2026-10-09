### Task 1: Native owner wrapper and profile contracts

**Files:** Domain/Profiles/Profile.cs, ProfileContracts.cs; Domain/Protection/RecipientEnvelope.cs, ProtectionChange.cs; Domain.Tests/ProfileContractsTests.cs; TestSupport/ProtectionFixture.cs.
**Interfaces:** RecipientEnvelope.IsValid/SigningBytes(owner,kind,id)/Verify(owner,kind,id,epoch,grantId,revision,identity,recipient,author); ProfileKeyWrappers.IsValid/IsBound/IsValidRotation; ProfileCreateInput(ProfileId,Envelope,KeyWrappers,EditedAt,RecordIds,FolderIds,CollectionIds).IsValid; ProfileCreateRequest(Actor,AccessVerifier,Input); ProfileCreateDetails(ProfileId,Revision,ServerSequence,EditedAt); IProfileCreateStore.CreateAsync(request,ct) returns VaultResult<ProfileCreateDetails>. ContentReplacement adds optional ProfileKeyWrappers? KeyWrappers=null. Profile entity carries common fields and serialized wrappers.
- [ ] Native signature/context/SEC1/base64/length/schema, master/per-profile required-wrapper, IDs/UTC/epoch and rotation inventory contracts RED; implement full Domain GREEN, commit/ledger.

