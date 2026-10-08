# UC15 Open Profile Access Implementation Plan
> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [x]`) syntax for tracking.
Goal: native profile-scoped challenge/proof → atomic selected handle and current opaque profile context, never account-wide access.
Spec: docs/superpowers/specs/2026-10-08-uc-15-open-profile-access.md.
Global: .NET10/EF10/PG18.6; original user files untouched; original strict native protocol, public canonical GUIDs, bounded UTF8, safe positive counters, statement_timestamp after waits; no plaintext/private keys or crypto implementation; pending independent approvals/release gates unchanged; oneissue16/branch/PR into develop; fullunfiltered>=90line with0fail0skip, exacthead latestCI/MERGEABLE/CLEAN normalmerge.
Review Focus: duplicated profile verifier cannot open another selection, including retained trash; account/purpose/scope/body-transplanted proof rejected; native corrupt dependency never emits handle or consumes proof; expiry after lock waits and immediately before consumption; rollback/single-winner/lost-response; fresh identity bootstrap cannot disclose another owner or content outside selection.


### Task 1: Contracts and verifier isolation

create Domain/Profiles/ProfileAccessContracts.cs and Data/Profiles/ProfileVerifierIsolation.cs (internal fingerprint uniqueness helper); modify ProfileCreateStore to reject same scoped public verifier as another retained owned profile after reserved-ID conflict check, account lock protects check+insert. No schema change needed. RED domain required IDs/revisions/digest/body contract; realPG create isolation same/other account, trash, corruptstoredpublickey and existing duplicate conflict precedence. Existing multi-profile fixtures must generate distinct scoped pairs, retaining explicit verifier values in tests that inspect them; never weaken assertions. GREEN wholeDomain/Data; commit.
Interfaces: ProfileChallengeRequest(actor,profileId,expectedRevision,requestHash); ProfileAccessRequest(actor,profileId,expectedRevision,challengeId,proof,rawBody); IProfileAccessStore.ChallengeAsync/OpenAsync; native ProfileUnlockMaterial.Read validates account protection counters/pins, target content/native owner wrappers/current profile counters and all retained owned verifier isolation.

- [x] Write the specified failing tests, run the focused project and read the expected RED output.
- [x] Implement only this task, run the named whole-family tests, read zero failures/skips and commit.

### Task 2: Native bootstrap challenge

Data/Profiles/ProfileAccessStore initial ChallengeAsync; realPG RED Master/PerProfile valid, hidden404 before conflict/native, current counter/protection corruption503, stale409, duplicate legacy scoped key503, cleanup expired/consumed only, challenge60seconds operationunlock-profile bound ownprofile/currentrevision+epoch/rawdigest/currentpolicy+generation; nohandle/contentmutation. Lockaccount→profile, freshdatabase time. Return only opaque selected profile bootstrap and challenge, no association/resource content. GREEN wholeData; commit.

- [x] Write the specified failing tests, run the focused project and read the expected RED output.
- [x] Implement only this task, run the named whole-family tests, read zero failures/skips and commit.

### Task 3: Atomic proof and selected issuance

implement OpenAsync. RED native correct Master/PerProfile success, wrongprofile/actor/account/purpose/body/key/epoch/rev/nonce/expiry/consumed proof denial and no session; expectedrevision conflict; policy/gen change409; renewalenabled/disabled expiry; expiry afteraccount/profilewait/justbeforeconsume; concurrentproof one winner; failureafterconsume/insertion rollsback andretryproofworks; existingprofile/account ciphertext/meta unchanged. Final conditional consumption and hashedsession.ProfileId insertion in oneTX; currenttypedassociation projection afterauthorization, all result validation beforecommit. GREEN wholeData; commit.

- [x] Write the specified failing tests, run the focused project and read the expected RED output.
- [ ] Implement only this task, run the named whole-family tests, read zero failures/skips and commit.

### Task 4: Strict commands and fail-closed outputs

create focused IssueProfileAccessChallenge/OpenProfileAccess command/validator/handler/output/messages files in Command/Profiles. Required JSON fields: challenge expectedRevision/requestHash; open expectedRevision. Internaltrusted actor/profile/proof/rawcontext only. TestsRED trust/requiredfields/hash/timestamps/nativecontext/selectedonly/outputscope/corruptdependency/null/error/cancel/statuses; mapping200open201challenge/400401404409503. Validation must never emit opaque handle alongside error. GREEN wholeCommand; commit.

- [ ] Write the specified failing tests, run the focused project and read the expected RED output.
- [ ] Implement only this task, run the named whole-family tests, read zero failures/skips and commit.

### Task 5: Actual HTTP and delivery

ProfilesController partial split ifsizewarrants, explicitDI in Startup. POST/{id}/access/challenges fresh signed iat/currentidentity, strictcanonicalroute/noquery/requiredinput; POST/{id}/access originalboundedVaultProofBody, singlecanonicalchallenge/signatureheaders. ActualPG/Heimdall/nativeHTTPRED independent mode/bootstrap/open, selectedget/list/update/association retain-remove/trash onlysameprofile; otherprofiles/accountwide management denial; identity/error/strictparser/header/query/body/corruptnative/expiry/replay/concurrency/rollback/no-store; legacykeyisolation and native crossprofileproof. Implement minimalroutes+DI, GREENfocused. Truthful profileaccessoperationsdocs/changelog/backlog M03seven+OpenAPI requiredfields/schema/statuses audited; helper23/spec/drift/diff +fullunfiltered allfamiliescoverage. Commit; ONEfreshordinarywholebranchreview, gradeALLscopes reason/cost; ONE TDDcorrectionpassforImportant/Critical thenfullsuite, no re-review. PushPRissueTesting; exactnewhead latestrequiredCI3SUCCESS MERGEABLE/CLEAN normalmerge; freshissueallcheckedClosed/Done/remoteabsent/fetchdeveloptestedtree/userSHA/archiveownworkspace thenUC16.

- [ ] Write the specified failing tests, run the focused project and read the expected RED output.
- [ ] Implement only this task, run the named whole-family tests, read zero failures/skips and commit.

Task1 exact contracts: Domain/Profiles/ProfileAccessContracts.cs carries ProfileChallengeRequest/AccessRequest/Input/Material/Context/ChallengeDetails/AccessDetails/IProfileAccessStore with signatures and response fields from the written spec. IsValid methods enforce positive safe revisions, canonical digest/signature size and trusted profile/actor binding; required strict ProfileAccessInput.expectedRevision JSON. Task1 helper IsUniqueAsync(db,accountId,candidate,excludedProfileId,ct) parses all retained owned scoped public keys, throws JsonException on corrupt state and returns false on the candidate's fingerprint collision. Native profile bootstrap reader belongs to Task2. Store create collision check follows existing reserved-ID conflict check; existing account lock serializes admission. Task1 tests ProfileAccessContractTests.cs / ProfileVerifierIsolationTests.cs; tests of changed multi-profile fixtures retain all previous assertions.
Verification commands: dotnet test tests/Domain/ArturRios.Cerberus.Domain.Tests; dotnet test tests/Infrastructure/ArturRios.Cerberus.Data.Tests; dotnet test tests/Application/ArturRios.Cerberus.Command.Tests; dotnet test tests/Presentation/ArturRios.Cerberus.WebApi.Tests --filter FullyQualifiedName~ProfileAccessHttpTests. Task5 complete run python3 scripts/coverage.py, Python helper tests, python3 scripts/verify_specs.py, python3 scripts/openapi.py --write and drift, git diff --check. Archive completed whole-family logs; unchanged log audit can be task-done command once the actual required run is complete.
