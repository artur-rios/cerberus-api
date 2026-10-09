### Task 1: Folder create contracts

**Files:** Create src/Domain/ArturRios.Cerberus.Domain/Folders/FolderCreateContracts.cs and tests/Domain/ArturRios.Cerberus.Domain.Tests/FolderCreateContractTests.cs.
**Interfaces:** Produce FolderCreateInput(Guid FolderId, EncryptedEnvelope Envelope, DateTimeOffset EditedAt, Guid[] ProfileIds, Guid? ParentFolderId=null), IsValid(), FolderCreateRequest(Guid Actor,string AccessVerifier,FolderCreateInput Input), FolderCreateDetails(Guid FolderId,long Revision,long ServerSequence,DateTimeOffset EditedAt), IFolderCreateStore.CreateAsync(FolderCreateRequest,CancellationToken) -> Task<VaultResult<FolderCreateDetails>>. Tasks 2/3 consume these exact interfaces.

- [ ] Write native envelope/required strict wire tests BEFORE contracts: valid root, parent, multiple profiles, same GUID as a profile, UTC minimum/precision/pre2000/max; invalid self-parent/empty IDs/null/duplicate profiles/native format/epoch/encoding/default/time offset; unknown ownership/plaintext/collectionIds/revision fields and numeric/case/duplicate body. No property holder tests.
- [ ] Run `dotnet test tests/Domain/ArturRios.Cerberus.Domain.Tests --filter FullyQualifiedName~FolderCreateContractTests`; Expected: missing FolderCreateInput compile RED before product.
- [ ] Implement the exact contracts and required JsonRequired/strict number/unmapped members. IsValid requires nonzero IDs, unique nonnull profiles, envelope valid initial epoch1, UTC ticks>=10, parent null/nonzero/different target.
- [ ] Run focused then whole Domain family; Expected: all pass with zero failures/skips. Commit `feat: define folder creation contract` and task-done audit unchanged complete whole-family log.

