### Task 2: Strict update-record command, validator and metadata output

**Files:** Create `src/Application/ArturRios.Cerberus.Command/Records/UpdateRecordCommand.cs`, `UpdateRecordValidator.cs`, `UpdateRecordHandler.cs`, `UpdateRecordOutput.cs`, `RecordUpdateMessages.cs`; Test `tests/Application/ArturRios.Cerberus.Command.Tests/UpdateRecordHandlerTests.cs`.
**Interfaces:** consumes Task1 exact input/request/store; produces ICommandHandlerAsync<UpdateRecordCommand,UpdateRecordOutput> and exact4publicfields for CommandMediator/Task3. SetContext(Guid actor,string? access,Guid recordId) derives private/internal context; body fields only required ExpectedRevision/Envelope/EditedAt.

- [ ] Write RED tests for strict validinput/context/hash/ctforwarding, nativeformat/binary/saferevision/epoch/timeboundaries, no relationship/contextbodyexposure; pre-storemissingidentity/missingaccess/badhandle/zeroid; allowlistvalidation_failed/not_found/vault_access_denied/revision_conflict/persistence_unavailable only; nullresult/data/errorwithdata/unknownerrors/targetmismatch/wrongnextrevision/unsafesequence/wrongtime/defaultoffset/submicro all503; callerct. Run focused, expected RED missing UpdateRecordHandler/Command/Validator.
- [ ] Implement command/validator/handler/output/messages; run focused then wholeCommand GREEN with zero failures/skips; commit `feat: validate encrypted record replacement`.

Task command: `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests --logger "console;verbosity=minimal"`; Expected exit0/allCommand pass/zero failures-skips.

