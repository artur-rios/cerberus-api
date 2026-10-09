### Task 2: Strict delete command and output

**Files:** Create src/Application/ArturRios.Cerberus.Command/Profiles/DeleteProfile{Command,Validator,Handler,Output,Messages}.cs; tests/Application/ArturRios.Cerberus.Command.Tests/DeleteProfileHandlerTests.cs.
**Interfaces:** Consumes IProfileTrashStore; produces DeleteProfileCommand(ExpectedRevision;SetContext(actor,access,profileId)), DeleteProfileHandler/Output6metadatafields and DeleteProfileMessages200/400/401/403/404/409/503.
- [ ] Write trustedcontext/input/hashedhandle tests; successmetadata/errorforward/cancellation; null/substitutedID/counters/operationID/timestamp/expiry503nullpayload. Require newrevisionexpected+1, UTCnondefaultmicroseconds, exact30day window and positive safe sequence. Run; expected missingcommandcompile failure.
- [ ] Implement strictJSON onebodyfield/validator/failclosed handler and statusmap.
- [ ] Run wholeCommandfamily; expected pass0skip. Commit.
**Completion:** no forgedowner/scope/bodyID and no partial bad dependency output.

