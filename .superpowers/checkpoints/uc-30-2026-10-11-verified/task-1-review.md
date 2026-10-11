# UC-30 task 1 review

## Spec Compliance

- ✅ Spec compliant for the task-scoped source at `1d01abd..2c4bca6`. No missing implementation, scope widening, or blocking correctness issue found.
- ✅ All 15 files listed in the task brief have corresponding changes. Domain request/row/page contracts and store interface match the required signatures (`src/Domain/ArturRios.Cerberus.Domain/Collections/CollectionListContracts.cs:5`); query, output, cursor, projection, messages, store, controller, DI, all three test classes, endpoint documentation and generated OpenAPI are present.
- ✅ Strict GET transport rejects unknown/noncanonical keys, repeated values, body framing and repeated access headers; the handler supplies the bounded default and validates access before persistence (`src/Presentation/ArturRios.Cerberus.WebApi/Controllers/CollectionsController.cs:29`; `src/Application/ArturRios.Cerberus.Query/Collections/ListCollectionsHandler.cs:16`). Raw HTTP success checks enforce exactly the requested page/item properties (`tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionListHttpTests.cs:35`, and its `Success` helper).
- ✅ Current actor/session/account policy, revocation generation, statement-time expiry and selected active owned profile are captured in one SQL statement; owned or active native RO/RW collection admission and exact selected ProfileCollection links precede ordering (`src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionListStore.cs:29`). There is no content traversal, later authority read, lock acquisition or mutation in the added store.
- ✅ Native evidence is selected from the entire visible foreign inventory, independently of page and boundary, with current owner/recipient pins; validation completes before any page is returned (`src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionListStore.cs:63`, `:91`, `:108`). Hidden/out-of-selection grants never enter this evidence set.
- ✅ Positive safe unique sequence checks cover visible inventory; highwater remains bound to the original cursor, while current permissions are recalculated (`src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionListStore.cs:58`; `src/Application/ArturRios.Cerberus.Query/Collections/ListCollectionsHandler.cs:32`). AES-GCM and a collection-specific derivation/AAD purpose bind actor, verifier, size, after and boundary (`src/Application/ArturRios.Cerberus.Query/Collections/CollectionListCursor.cs:12`).
- ✅ Projection and handler reject invalid public IDs, safe counters, time precision/offset, envelopes/epochs, item count, ordering, repeated IDs, changed boundary and corrupt result/error contracts before publishing data (`src/Application/ArturRios.Cerberus.Query/Collections/CollectionProjection.cs:12`; `src/Application/ArturRios.Cerberus.Query/Collections/ListCollectionsHandler.cs:28`).
- ✅ All four alternate flows have behavior checks: malformed transport (`tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionListHttpTests.cs:56`), omitted hidden rows/empty scope and invalid accounts (same class), fresh identity failures (`:83`), and native corruption/dependency failure (`:187`; `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionListStoreTests.cs:35`). Caller/internal cancellation paths are covered in the new store tests.
- ⚠️ Full unfiltered suite, fresh coverage, category/protocol/audit gates, later exact-head CI and delivery lifecycle are controller gates still pending at dispatch. They are not absent-code findings and this task approval does not establish their results.

## Strengths

- The SQL makes the authorization boundary explicit and keeps native evidence within the same snapshot. A real PostgreSQL interceptor test proves that revocation immediately after the read does not cause a second authority read (`tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionListStoreTests.cs:59`).
- Native RO/RW access and exact profile selection are exercised against real persistence, including direct folder/record links that must not imply collection visibility (`tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionListStoreTests.cs:76`, `:95`). Continuation revocation tests cover grants, profile links and sessions (`:111`).
- HTTP tests create collections through the existing POST route, compare opaque envelope values, inspect raw JSON field sets, check no-store responses and compare database snapshots (`tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionListHttpTests.cs:35`).

## Issues

### Critical

- None found.

### Important

- None found.

### Minor

1. `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionListStoreTests.cs:35` and `:200`; `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionListHttpTests.cs:267`: several newly added tests, mutation branches and snapshot helpers are compressed into extremely long single lines. This makes security scenarios and fixture mutations unnecessarily difficult to inspect and maintain. Format the added code into normal statement blocks; keep each scenario's arrange/action/assert sequence readable. No behavior change is needed.
2. `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionListStoreTests.cs:131`; `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionListHttpTests.cs:187`: the corrupt-evidence tests cover rows beyond the returned page on an initial request, but do not explicitly cover an otherwise-visible foreign row beyond an already-carried highwater boundary. The implementation correctly gathers evidence from `visible`, so this is a regression-coverage suggestion, not a current functional defect. Add a continuation scenario that creates or advances a granted collection beyond the boundary, corrupts its binding evidence, and expects503 without partial data.
3. `/tmp/uc30-focused-all-green.log:8`, `:18`, `:35`: the solution-wide focused command emits three “No test matches” messages from unrelated assemblies. This is benign selection noise, not a failed or skipped UC-30 test, but the reported output is not completely pristine. Future focused commands can target the three relevant test projects. Do not rerun the existing passing suite merely to remove this noise.

## Focused checks and evidence

- Read the supplied brief, report, binding spec, Global Constraints and complete supplied diff. The diff contained complete added implementations and sufficient controller/DI context; no changed source file was separately reopened.
- Named risk: new store adapters might omit the native signature, identity, epoch or current-pin binding. One focused unchanged-code check read `src/Infrastructure/ArturRios.Cerberus.Data/Resources/CollectionGrantBinding.cs:11` and `:22`. It validates safe protection revision, material/pinned epochs and recovery generation, native envelope epoch, and calls recipient-envelope verification with owner, collection, grant/revision, recipient identity and current keys. The new adapter supplies these values from its captured SQL evidence.
- Named risk: projection serializer options omit a global unknown-property rejection setting. One focused declaration lookup found `JsonUnmappedMemberHandling.Disallow` on `src/Domain/ArturRios.Cerberus.Domain/Accounts/EncryptedEnvelope.cs:5`; the existing type supplies the strict unknown-field rule. The new handler tests include an unknown-envelope-field rejection case.
- Read `/tmp/uc30-query-green.log`, `/tmp/uc30-store-green.log`, `/tmp/uc30-http-green.log`, `/tmp/uc30-focused-all-green.log`, and `/tmp/uc30-openapi-check.log`. Final focused evidence reports Query101, Store91 and HTTP71 passing, zero failures/skips (`/tmp/uc30-focused-all-green.log:28`, `:45`, `:54`); OpenAPI reports current. No build warnings/errors were present in these logs.
- No tests, builds, fixture operations or git commands were run. Only the requested review artifact was written; source, index and branch state were untouched.

## Cannot verify / declined to judge

- The diff does not establish the internals of the existing authentication/no-store middleware or cryptographic primitives. The new HTTP/native-fixture tests exercise those integration contracts; no broader middleware or primitive audit was attempted.
- The shared pre-existing OpenAPI requiredness/wrapper/empty413 metadata limitation is explicitly deferred by the binding task context; this review does not certify that wider client contract or expand this task to repair it.
- Independent security/client release qualification, whole-branch review, CI, branch policy, release readiness and merge/issue closeout remain outside this task-scoped review. No claim is made about pending full coverage.

## Assessment

**Task quality: Approved.**

The added operation follows the specified authority, visibility, snapshot, cursor and fail-closed projection rules, with meaningful unit, PostgreSQL and HTTP evidence. The findings above are nonblocking readability and verification refinements; final delivery remains conditional on the controller's outstanding gates.
