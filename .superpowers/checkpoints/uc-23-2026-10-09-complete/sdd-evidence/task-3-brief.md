### Task 3: Strict command boundary

**Files:** Create src/Application/ArturRios.Cerberus.Command/Folders/CreateFolderCommand.cs, CreateFolderValidator.cs, CreateFolderHandler.cs, CreateFolderOutput.cs, FolderCreateMessages.cs and tests/Application/ArturRios.Cerberus.Command.Tests/FolderCreateHandlerTests.cs.
**Interfaces:** Consume Task1/CreateAsync store; expose CreateFolderCommand.SetContext(Guid actor,string? access), ToInput(), Handler with IValidator<CreateFolderCommand>/IFolderCreateStore, exact four-field CreateFolderOutput. Task4 consumes these classes and status map.

- [ ] Write command tests BEFORE product: strict required4/optionalparent, forged context/plaintext rejection, input/self-parent validation, trusted actor/current opaque handle hash/cancellation, safe error allowlist, known statuses, null/error-with-data/wrongid/revision/unsafe-sequence/offset/default/not-floored-or-mismatched-time output rejection. Valid initial revision1 and exact floorµs returns folder_created201/four fields.
- [ ] Run focused command tests; Expected: missing command/handler compile RED.
- [ ] Implement command/validator/handler/messages/output. Missing actor401, missing handle401, invalid hash/input400; store receives verifier only, caller ct unchanged. Any malformed result safely503 with no payload. Status map includes identity_unavailable503.
- [ ] Run focused then whole Command family; Expected: zero failures/skips. Commit `feat: validate folder creation requests`; task-done audit fresh whole-family result.

