### Task 3: Strict permanent-delete command

**Files:** New Command Records/PermanentlyDeleteRecord{Command,Validator,Handler,
Output,Messages}.cs and Command.Tests/PermanentlyDeleteRecordHandlerTests.cs.
**Interfaces:** consumes Task2 request/store/details; internal context(actor,
handle,recordId), exactly one JsonRequired ExpectedRevision input; exact two output
fields recordId/deletedAt. Known stable safe error allowlist and canonical statuses.

- [ ] Write RED success/context/hash/ct forwarding, safe input boundaries and
  pre-store failures, exact required input/output shape; null/unknown/malformed
  store result or mismatched ID/invalid UTCµs timestamp→503 no partial output;
  expected errors and caller cancellation. Expected missing command/handler.
- [ ] Minimal fivefiles, focused then wholeCommand GREEN, commit `feat: validate
  permanent record deletion`; task-done audits fresh unchanged wholeCommand log.

Task command: dotnet test tests/Application/ArturRios.Cerberus.Command.Tests
--logger "console;verbosity=minimal"; focused PermanentlyDeleteRecordHandlerTests.

