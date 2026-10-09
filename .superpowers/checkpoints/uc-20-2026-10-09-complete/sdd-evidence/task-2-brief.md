### Task 2: Strict delete command and six-field metadata

**Files:** Create `src/Application/ArturRios.Cerberus.Command/Records/DeleteRecordCommand.cs`, `DeleteRecordValidator.cs`, `DeleteRecordHandler.cs`, `DeleteRecordOutput.cs`, `DeleteRecordMessages.cs`; `tests/Application/ArturRios.Cerberus.Command.Tests/DeleteRecordHandlerTests.cs`.
**Interfaces:** consumes Task1 exact request/store/details; produces ICommandHandlerAsync<DeleteRecordCommand,DeleteRecordOutput>, JsonRequired ExpectedRevision only, internalcontext SetContext(Guid actor,string? access,Guid recordId), publicexact6fields for Task3.

- [ ] Write Command RED: strict positive/safe revision/canonical context/hash/ctforwarding, exactly1body/6outputfields; missingidentity/missingaccess/badhandle/zeroid prestore; safe errorallowlist validation_failed/not_found/vault_access_denied/revision_conflict/persistence_unavailable only; nullresult/data/errorwithdata/unknownerror/mismatchedrecord/zerooperation/wrongnextrevision/unsafesequence/nonUTC/submicro/defaultdeletedAt/wrong30dayduration all503no partialoutput; callerct. Run focused; Expected missinghandler/command RED.
- [ ] Implement command/validator/handler/output/messages minimally. Focused then wholeCommand GREEN zero failures/skips. Mark Task2 checks only, commit `feat: validate owned record deletion`, task-done audits fresh completed unchanged wholeCommand log.

Task command: `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests --logger "console;verbosity=minimal"`; Expected exit0/allCommand pass/zero failures-skips.

