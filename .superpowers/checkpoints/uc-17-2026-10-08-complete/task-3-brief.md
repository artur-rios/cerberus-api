### Task 3: Record cursor, strict projection and Query

Create Query/Records/RecordListCursor.cs, ListRecordsQuery/Handler, RecordListOutput,
RecordListMessages and RecordProjection helper. Use purpose-separated existing
AESGCM/HMAC cursor pattern with canonical bounded tuple. Bind actor/hash/size and
initial boundary, strict pre-store syntax, safe metadata/native JSON output guards,
allowlisted errors/null/error-with-data rejection and cancellation propagation.
Default min50/configuredMax, no size+1 overflow. Output exactly items+nextCursor and
five item fields; current stored UTC time >=10ticks and microsecond aligned.

Write Query/ListRecordsHandlerTests.cs first: default/custom/max bounds, actor/access
missing/malformed, actual handle hash and caller token, continuation/end, emptylist,
canonical cursor/tamper/wrong actor/wrong handle/wrong size/profile-purpose/keyrotation,
duplicate/unknown/case/numeric/unsafe cursor fields, impossible cursor/page metadata,
null/errors-with-data/unknown errors, strict native format/binary/epoch/unknown and
numeric-string envelope corruption, duplicate public IDs/ordering/outsideboundary/
unsafe metadata/default/offset/submicro times, exact public shape and safe errors.
Expected RED: missing Query. GREEN whole Query; commit.
Interfaces: consumes Task1/2; produces Task4 controller/DI/public contract. No grant
key blobs, owner profiles or potentially out-of-scope folder IDs in list output.

- [ ] Write specified failing tests, run and read expected RED before product code.
- [ ] Implement this task, run named whole-family verification, read zero failures/skips and commit.

