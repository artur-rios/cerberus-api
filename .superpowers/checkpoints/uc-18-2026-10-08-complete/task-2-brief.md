### Task 2: Strict get-record query and output

Create Query/Records/GetRecordQuery.cs/GetRecordHandler.cs/RecordDetailsOutput.cs/
RecordReadMessages.cs. Reuse RecordProjection for five fields and validate exact
requestedtarget, distinct/nonzero non-nullprofile/collection arrays, optionalnonzero
parent; exact8publicfields. Context validation pre-store, actualhandlehash/token,
safeerrorallowlist, null/errorwithdata/unknown failures503, callerctpropagation.

Write Query/GetRecordHandlerTests.cs first: actor/missingorbadhandle/zeroID pre-store;
forwardhash/request/ct; exactnative5fields+3visibilityrefs includingemptyarrays/null
parent andsameGUIDacrosskinds; nullresult/data/record/arrays/envelope, targetmismatch,
duplicate/zero refs/parent; unsafe/default/offset/submicro metadata, nativeunknown/
duplicate/case/numericstring/badformat/epoch/binary; safeerrors/unknown/errorswithdata/
successaserror/authasstoreerror failclosed/no partialoutput; cancellation. Expected
RED missingGetRecordQuery/Handler, then wholeQueryGREEN. Commit.
Interfaces consumes Task1, exposes QueryMediator handler/output to Task3. No cursor
or new public owner/protection/native grant fields.

- [ ] Write specified failing tests, run and read expected RED before product code.
- [ ] Implement this task, run named whole-family verification, read zero failures/skips and commit.

