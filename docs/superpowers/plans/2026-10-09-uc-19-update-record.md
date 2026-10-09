# UC19 Update Record Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** replace one currently writable encrypted record with expected-revision protection, no relationship changes and exact four-field metadata.
**Architecture:** targeted current-scope transaction, consistent actor/collection/grant/record lock order, native evidence reload and final database-time guarded write; strict command/HTTP contracts.
**Tech Stack:** existing .NET10/EF10/PostgreSQL18.6/mediator/native crypto; no dependency/schema changes.
**Spec:** docs/superpowers/specs/2026-10-09-uc-19-update-record.md

## Global Constraints

Preserve primary user files; isolated feature/uc-19-update-record from fresh verified UC18 develop; one issue20/branch/PR. No plaintext/decryption/usablekey/foreign account/profile locks. Same encrypted epoch; changed content freshsaltANDnonce; matching expectedrevision not offlineLWW. UTC>=10ticks flooredmicroseconds prebinding; safepositive revision/sequence. Hidden404 before conflict/corruption, validvisibleRO403, nativecontributingforeign malformed503wholetransaction. Lock ownaccount/session/selection -> collectionsSHAREpublicGUID -> grantsSHAREpublicGUID -> recordUPDATE; reload afterwaits/newunheldauthority409; finalstatement fullrecursiveancestry/time/lifecycle/heldvalidatedscope guard; NO folderlocks (existing create leaf-to-root order). GlobalterminalGUID conservative until typedrepair BEFOREANYpurge; neverselectionNULLonpurge. TDDfirst, wholefamilies/fullsixfamily>=90line/zero failure-skip/reportbranch; allnative/dependency/OpenAPI/spec/helperchecks. Onefreshordinaryfinalreview/oneblockingTDDpass/allrulingscosts/minors. Existingallissuesauthorization replaces repeated approvalmenus; independentsecurity/clientdevelopment-onlydeferred/releasegatesunchanged. Exacthead3requiredCISUCCESS/MERGEABLE/CLEAN/freshunchangedbase before normalmatchheadsquash+completeDoD.

## Review Focus

- Multiple valid and malformed overlapping RO/RW grants must never allow a native-invalid current authority route or disclose hidden content; Task1 matrix plus larger unexpected combinations in review.
- A folder reparent, membership change or new permission path between discovery and locks must never let unheld/unvalidated authority authorize a write; complete ancestry reevaluated INSIDE finalUPDATE without stale path or new folder locks; Task1 controlled races and final predicate tests.
- Reciprocal recipient edits and owner protection rotation must not create foreign-account lock cycles or overwrite a winner read before collection locks; Task1 concurrency/lock-trace tests, review untested multi-resource rotations.
- Expiry/terminal state committed immediately before final mutation must deny despite earlier success; Task1 intercepted finalstatement and every lockclass, Task3 actualHTTP currentstate matrix.
- Corrupt/partial store results and alternate JSON/native metadata must not report success or leak arbitrary store errors; Task2 fake-store corruption matrix and Task3 raw HTTP bodies.

### Task 1: Targeted transactional record update and domain input

**Files:** Create `src/Domain/ArturRios.Cerberus.Domain/Records/RecordUpdateContracts.cs`, `src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordUpdateStore.cs`, `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/RecordUpdateStoreTests.cs`; reuse existing CollectionGrantBinding/RecordCreateDetails and existing test fixtures without a generic permission refactor.
**Interfaces:** produces RecordUpdateInput(ExpectedRevision,Envelope,EditedAt).IsValid; RecordUpdateRequest(Actor,AccessVerifier,RecordId,Input); IRecordUpdateStore.UpdateAsync(request,ct)->VaultResult<RecordCreateDetails> to Task2/Task3.

- [ ] Write failing behavioral Data tests before product: owned accountwide/unlinked and selected direct/folder/ownedcollection; nativeforeignRO/RW direct/folder selected/accountwide; overlaps/allnativecontributors; hidden/unselected/trash/terminal/cycle/session/currentowner/currentgrant beforeconflict. Assert exact ciphertext, rev+1/seqgreater/normalizedtime, immutable relationships/owner/parents/pins. Current invalidaccount/session/selection matrix, all native signature/binding/pins/epoch/revision/strictJSON variants, irrelevant malformed hidden grants. Concurrent sameexpectedrevision acrossowner/tworecipients one winner, oldretry409, older/equaltime notLWW, salt/nonce/epoch rejects. Expiry during allsixlockclasses and finalUPDATE; grantrevoke/downgrade/memberremove/folderreparent whileblocked; newunheldcollection/grant409; concurrentcreationunderthe sameparentnodeadlock; reciprocaledits/rotation/locktrace; terminalfinalguard; afterwrite503rollbackretry; invalid/regressed/exhaustedsequence; callerct/outage. Run focused test, read expected RED missing RecordUpdateStore/RecordUpdateInput.
- [ ] Implement minimal strict Domain + purpose-built targeted transaction following spec; run focused GREEN then wholeData, zero failures/skips. Compare each case/expected against real output; commit `feat: update currently writable encrypted record`.

Task command: `dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests --logger "console;verbosity=minimal"`; Expected exit0/allData pass/zero failures-skips.

### Task 2: Strict update-record command, validator and metadata output

**Files:** Create `src/Application/ArturRios.Cerberus.Command/Records/UpdateRecordCommand.cs`, `UpdateRecordValidator.cs`, `UpdateRecordHandler.cs`, `UpdateRecordOutput.cs`, `RecordUpdateMessages.cs`; Test `tests/Application/ArturRios.Cerberus.Command.Tests/UpdateRecordHandlerTests.cs`.
**Interfaces:** consumes Task1 exact input/request/store; produces ICommandHandlerAsync<UpdateRecordCommand,UpdateRecordOutput> and exact4publicfields for CommandMediator/Task3. SetContext(Guid actor,string? access,Guid recordId) derives private/internal context; body fields only required ExpectedRevision/Envelope/EditedAt.

- [ ] Write RED tests for strict validinput/context/hash/ctforwarding, nativeformat/binary/saferevision/epoch/timeboundaries, no relationship/contextbodyexposure; pre-storemissingidentity/missingaccess/badhandle/zeroid; allowlistvalidation_failed/not_found/vault_access_denied/revision_conflict/persistence_unavailable only; nullresult/data/errorwithdata/unknownerrors/targetmismatch/wrongnextrevision/unsafesequence/wrongtime/defaultoffset/submicro all503; callerct. Run focused, expected RED missing UpdateRecordHandler/Command/Validator.
- [ ] Implement command/validator/handler/output/messages; run focused then wholeCommand GREEN with zero failures/skips; commit `feat: validate encrypted record replacement`.

Task command: `dotnet test tests/Application/ArturRios.Cerberus.Command.Tests --logger "console;verbosity=minimal"`; Expected exit0/allCommand pass/zero failures-skips.

### Task 3: Actual HTTP replacement and delivery

**Files:** Modify `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/RecordsController.cs`, `src/Presentation/ArturRios.Cerberus.WebApi/Startup.cs`, `docs/security/record-api.md`, `docs/requirements/Operations & Infrastructure Document.md`, `README.md`, `CHANGELOG.md`, `docs/contracts/openapi.json`; Create `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/RecordUpdateHttpTests.cs`.
**Interfaces:** consumes Task1+2; PUT/api/records/{id} strict threefieldbody/currentidentity/rawsingleheader, VaultProofBody; 200record_updated exact4metadatafields; statuses400/401/403/404/409/empty413/503; allno-store. Register store+validator+mediatorhandler explicitly.

- [ ] Write actual HTTP RED before route/DI: createactualUC16records, nativeUC15Master+PerProfilehandles, currentnativeRO/RWcollectiongrants; ownedselected/foreigndirect+descendant/dynamichidden/RO403; currentowner/grant/session/selection/linkchanges; hidden404beforenative/conflict; relevantsignature/quotedepoch corruption503; exact4fieldresponse/currentcipher/immutablegraph; sameexpectedrevision409winningstate/retry; nativeepoch/salt/nonce/time invalid400; strictcanonicalID/header/query/UTF8/BOM/compression/unknown/duplicate/case/numericstring/null/missing/forgedcontextbody; missing/badidentity401/upstreamoutage503/dboutage503; empty413/no-store. Run focused, expected missingPUT404/405 before product.
- [ ] Add route+DI; focusedGREEN; updateAPI/operators/backlogUC19Done/M03eleven/changelog; generate/checkOpenAPI actualthreebodyfields/fourresponsefields/eightstatuses/nativefields; preserveknownsharedgeneratorMinors. Lockedrestore/NuGet/nativeOSV153/corpus322/36nativehelpers/23Pythonhelpers/specs107FR55UC29BR/diff/fullunfiltered sixfamilycoverage>=90linezero failure-skip/reportbranch; commit `feat: expose encrypted record replacement`. Onefreshreview/oneblockingTDDfixpass/fullGREEN/allrulings+minors PRbody. PushONEPRcloses20/projectTesting; latestexacthead3CISUCCESS/MERGEABLE/CLEAN/freshunchangedtestedbase normalmatchheadsquash; DoDallnine, issueclosed/projectDone, remoteabsent/developtestedtree/primarySHA/archiveonlyownscratch. ContinueUC20.

Task command: focused `dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests --filter FullyQualifiedName~RecordUpdateHttpTests --logger "console;verbosity=minimal"`, then `python3 scripts/coverage.py` (fresh unfiltered allsix supplies wholeWeb); Expected exit0/zero failures-skips/sixproductionassemblies>=90line. Shared .NET commands serialized. Just-completed unchanged whole-family/full-suite logs can be audited for task-done without redundant reruns. Native cache /tmp/cerberus-protocol.fCQIq1; use existing approved cached helpers. Full baseline is fresh UC18testedmergedtree3328/3328/98.3line/91.5branch, no unchangedbaseline rerun.
