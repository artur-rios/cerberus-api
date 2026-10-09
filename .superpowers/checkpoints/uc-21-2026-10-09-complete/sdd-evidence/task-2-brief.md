### Task 2: Strict move command and result

**Files:** Create `src/Application/ArturRios.Cerberus.Command/Records/MoveRecordCommand.cs`, `MoveRecordValidator.cs`, `MoveRecordHandler.cs`, `MoveRecordOutput.cs`, `MoveRecordMessages.cs`, `tests/Application/ArturRios.Cerberus.Command.Tests/MoveRecordHandlerTests.cs`.
**Interfaces:** consumes Task1 exactrequest/store/details; produces ICommandHandlerAsync<MoveRecordCommand,MoveRecordOutput>, JsonRequired ExpectedRevision and nullableFolderId only, internal SetContext(Guid actor,string? access,Guid recordId), exact4publicoutputfields.

- [ ] Write Command RED for successnull/nonnull/allowedboundaries, strictcontext/hash/input/ctforwarding, exactly2requiredbody/4outputfields; identity/missinghandle/badhandle/emptytarget/emptydestination/unsafeexpected prestore. Safe known error allowlist only, nullresult/data/errorwithdata/unknownerrors→503. Mismatchedrecord/folder, zero/unsafe/wrongnextrevision/unsafesequence→503 no partialpayload. Callerct propagation. Expected missing MoveRecordCommand/Handler compilation failure before product.
- [ ] Add minimal five command files. Focused then wholeCommand GREEN zero failures/skips; inspect actual output. Mark Task2checks only, commit `feat: validate owned record moves`; task-done audits fresh unchanged wholeCommand log.

Task command: `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests --logger "console;verbosity=minimal"`; Expected exit0/allCommand pass/zero failures-skips. Focused filter FullyQualifiedName~MoveRecordHandlerTests.

