### Task 1: Strict folder update contract and command

**Files:** Create `src/Domain/ArturRios.Cerberus.Domain/Folders/FolderUpdateContracts.cs`; create `src/Application/ArturRios.Cerberus.Command/Folders/{UpdateFolderCommand,UpdateFolderValidator,UpdateFolderHandler,UpdateFolderOutput,FolderUpdateMessages}.cs`; test `tests/Application/ArturRios.Cerberus.Command.Tests/UpdateFolderHandlerTests.cs`.
**Interfaces:** `FolderUpdateInput(long ExpectedRevision,EncryptedEnvelope Envelope,DateTimeOffset EditedAt).IsValid()`, `FolderUpdateRequest(Guid Actor,string AccessVerifier,Guid FolderId,FolderUpdateInput Input)`, `IFolderUpdateStore.UpdateAsync(FolderUpdateRequest,CancellationToken):Task<VaultResult<FolderCreateDetails>>`. `UpdateFolderCommand` strict required body+private context SetContext(Guid,string?,Guid), ToInput(); handler/validator/output/messages mirror RecordUpdate names and exact four folder fields.

- [ ] Write GivenInvalidAuthorityOrInput_WhenHandling_ThenRejectBeforePersistence and GivenValidReplacement_WhenHandling_ThenForwardTrustedContextHashAndReturnExactFourMetadataFields based on actual UpdateRecordHandlerTests; include literal output fields, counter/µtime/target identity, strict body required/duplicate/numeric behavior, malformed store/errorwithdata/allowlist and caller cancellation. ReviewFocus5 handler boundary.
- [ ] Run `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests --no-restore --filter FullyQualifiedName~UpdateFolderHandlerTests > /tmp/cerberus-uc26-command-red.log 2>&1`. Expected missing named folder update types, no fixture typo/zero match.
- [ ] Implement named signatures following existing record-update validation/error/result logic, folder_updated and four-field metadata. Expected no persistence call on invalid input.
- [ ] Run focused command-green.log then whole Command command-whole.log with `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests --no-restore`. Expected all pass zero fail/skip.
- [ ] Commit `feat: define folder update contract`; task-done audit actual whole Command log; mark ledger/checkboxes.

