### Task 1: Record create contracts

Create Domain/Records/RecordCreateContracts.cs and tests/Domain/.../RecordCreateContractTests.cs. Required strict RecordCreateInput(Guid RecordId,EncryptedEnvelope Envelope,DateTimeOffset EditedAt,Guid[] ProfileIds,Guid? FolderId=null); IsValid nonzeroID/nativevalid initialepoch1/UTCticks>=10/profileIDs nonnullunique/nonzero/foldernullornonzero. ProfileIdmay equalRecordId acrosskind. RecordCreateRequest(Guid Actor,string AccessVerifier,RecordCreateInput Input); RecordCreateDetails(Guid RecordId,long Revision,long ServerSequence,DateTimeOffset EditedAt); IRecordCreateStore.CreateAsync(request,ct). Required body members recordId/envelope/editedAt/profileIds. Tests beforecode: validempty/multiplerelationships, nativeboundary/time/pre2000, required/unknown/number/case/duplicatebody, malformed fields/canonicalprotocol. No property-holder gettertests. GREENwholeDomain; commit.
Interfaces: these contracts feed Task2 persistence andTask3 commands; no new entity schema.

- [ ] Write the specified failing tests, run the focused project and read the expected RED output.
- [ ] Implement only this task, run the named whole-family tests, read zero failures/skips and commit.

