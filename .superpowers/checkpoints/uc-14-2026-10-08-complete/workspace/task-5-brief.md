### Task 5: Strict command and result

**Files:** Create Command/Profiles/SetProfileAssociations{Command,Validator,Handler,Output,Messages}.cs, tests/Command/SetProfileAssociationsHandlerTests.cs.
**Interfaces:** SetProfileAssociationsCommand body4fields/context(actor,access,profileId), consumes IProfileAssociationStore; output6fields; status200/400/401/403/404/409/503.
- [ ] Write trusted request/hash/currentcontext/input tests, success exact sets/newrev/safe seq, storeerror/cancel, corrupt/null/outputIDs/set mismatch503nullpayload. Runfocused; expected missingcommand RED.
- [ ] Implement strictJSON/validator/failclosedhandler and stable statuses; wholeCommand expected allpass0skip. Commit.
**Completion:** Body cannot forge context or return malformed dependency state.

