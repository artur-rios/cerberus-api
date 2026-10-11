# UC-30 whole-branch review

Reviewed range: `ef869fd..8ad4e39` (three commits; 21 files). This is the one broad independent whole-branch review. Requirements: `docs/superpowers/specs/2026-10-11-uc-30-list-collections.md` and its implementation plan. Production implementation is unchanged since `2c4bca6`.

## Strengths

- The authorization boundary is coherent across HTTP, handler and persistence. `CollectionsController.cs:29` rejects unknown/case-forged or repeated queries, noncanonical sizes, duplicate access values and body framing. `ListCollectionsHandler.cs:16` validates identity, access, bounds and continuation context before persistence.
- `src/Infrastructure/ArturRios.Cerberus.Data/Collections/CollectionListStore.cs:29` captures account/session/profile authority, collection visibility, ordering, native evidence and page in one PostgreSQL statement. Active ownership or native RO/RW admission and exact selected ProfileCollection links precede pagination. No folder/record traversal widens access. Typed terminal markers preserve cross-kind UUID aliases.
- `CollectionListStore.cs:63` selects native evidence from the entire visible foreign inventory, including rows outside the page and carried boundary. Validation at `:91` precedes returning any page. The adapter passes collection/grant identity, epoch/revision, owner identity, recipient Heimdall identity and current protection pins into the existing binding validator. Hidden/out-of-selection evidence stays outside that set.
- The SQL is read-only and contains no explicit row locks or subsequent authority reads. The PostgreSQL interceptor test at `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionListStoreTests.cs:59` checks that post-statement revocation does not trigger a second read; later requests reapply revocation.
- `CollectionListCursor.cs:12` uses a collection-specific derivation/AAD purpose and authenticated encryption. Actor, access verifier, size, last sequence and initial boundary are bound together. `ListCollectionsHandler.cs:32` rejects inconsistent store results, unsafe boundaries, excess/short-more pages, invalid projections and ordering before output. `CollectionProjection.cs:12` validates opaque envelope, stored epoch, safe counters and timestamp constraints.
- Tests cover all four alternate flows at appropriate layers, with actual collection POSTs and actual selected-profile access in HTTP tests, native signed grants and real PostgreSQL in functional tests, raw JSON field checks, no-store assertions, context/cross-purpose cursor rejection and before/after state snapshots. The five-field response excludes ownership, grant and protection inventories.
- Documentation explains current permission reapplication, highwater limitations, native fail-closed behavior, replica cursor-key configuration and release deferrals. The generated OpenAPI diff adds the GET operation and the expected page/item schema.

## Issues

### Critical (must fix)

None found.

### Important (should fix)

None found.

### Minor (nice to have)

1. **Security scenarios are difficult to maintain because of compressed formatting.** `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionListStoreTests.cs:35` and `:200`; `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionListHttpTests.cs:267`. Several complete arrange/mutate/assert sequences and snapshots occupy a single very long line. When extending a native corruption case, it is unnecessarily hard to verify which branch changes which authority field or which assertions apply. Format these tests and helpers into ordinary blocks in a follow-up. This independently confirms the task review's readability finding; no current behavior failure is inferred.

2. **Add an explicit carried-highwater native-corruption regression.** `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/CollectionListStoreTests.cs:131`; `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/CollectionListHttpTests.cs:187`. Existing tests reject corrupt foreign evidence beyond the returned first page. They do not explicitly take a valid first page, then create/advance a still-visible granted collection beyond its carried boundary, corrupt that grant evidence, and continue. The current implementation correctly selects evidence from `visible`, so this is nonblocking regression coverage for a future accidental change to select from `page`. Expect503 without partial data in that scenario. This independently confirms the task review's suggestion.

3. **Reconcile stale checkpoint statements during closeout.** `.superpowers/checkpoints/open-issues-2026-10-08.json:27` still says “pause before UC30 issue31”; `:1893` still describes UC30 as “not_started; preliminary read-only exploration only”, while the updated current state records Testing and an implementation commit. A resume reader following these nested fields could unnecessarily repeat preparation or infer the old pause. Update or explicitly mark them historical in the planned final documentation/checkpoint change. The current top-level scope is clear, so this is not a runtime or merge blocker by itself.

## Evidence and review scope

- Read the supplied full commit/stat/diff package in one review pass. The initial large tool response was truncated; retrieved the omitted range in smaller chunks. No second broad review or subagent was dispatched.
- Read the spec, plan, task report, task review and ledger. Inspected the complete new production and test code in the diff, including controller registration and all changed documentation/OpenAPI metadata.
- Named integration risk: snapshot joins or owner/recipient pin caching could multiply evidence or select inconsistent authority. Focused unchanged-code inspection of `AppDbContext.cs:66` and `Configuration/ResourceModel.cs:39` confirmed unique Heimdall identities, access verifiers, protection per account, public collection/grant IDs and collection/recipient grant pairs; exact ProfileCollection links have a composite primary key.
- Named integration risk: the new evidence adapter could omit signature/context validation. Read `Resources/CollectionGrantBinding.cs:11` and `:22`: it validates protection material/revision/epochs/generation and delegates verification with owner, resource kind, collection ID/epoch, grant ID/revision, recipient identity and current keys. `EncryptedEnvelope.cs:5` provides the strict unknown-property rejection relied on by projection/binding JSON parsing.
- Named integration risk: the new GET could bypass fresh authentication or lose no-store on error. Focused checks of startup middleware ordering, `ProtectedEndpointMiddleware.cs:9`, `NoStoreMiddleware.cs:7` and `SafeFailureMiddleware.cs:9` confirmed the new nonanonymous endpoint goes through current identity revalidation and no-store is applied before dispatch and again when the response starts. Caller cancellation is preserved by the existing failure boundary.
- Read `/tmp/uc30-focused-all-green.log`: Query101, Store91 and HTTP71 pass, zero failures/skips. The three unrelated assemblies' “No test matches” lines are benign filter-selection diagnostics, not a defect in the added code and not skipped UC30 coverage. Recommend project-local focused commands in future; no rerun is warranted solely to remove those lines. This independently triages the task review's third minor as informational.
- Read `/tmp/uc30-openapi-check.log`: generated contract is current. Did not rerun builds, tests or fixtures while the full verification run was active. Focused results are evidence, not a substitute for that outstanding full gate.
- Source, index, HEAD and branch state were not changed. Only this requested review artifact was written.

## Recommendations

Keep the production source stable while its full fresh verification completes. Preserve the first two nonblocking follow-ups in the delivery ledger, reconcile the checkpoint wording in the planned documentation closeout, and check that final documentation-only diff against the actual test/coverage/CI evidence. No targeted experiment or source fix is required by this review.

## Declined to judge

- Shared OpenAPI requiredness, wrapper/nullability and advertised ProblemDetails for the actual empty413 response: explicitly inherited/deferred for development; the new GET runtime shape and added metadata were inspected, but external generated-client readiness is not certified.
- Independent cryptographic/protocol security qualification and real external-client key provisioning/decryption: explicitly deferred under the development-only authorization. This review checks the new binding integration and tests, not the cryptographic primitives or client ecosystem as a whole.
- Production-scale throughput, inventory size limits and denial-of-service capacity: no workload/SLA or performance qualification is supplied. The required full-visible evidence validation inherently scales with visible foreign grants; no performance certification is implied.
- Full unfiltered coverage/category/helper/native/audit gate completion and exact-head branch-policy/test/docker CI: pending or controller-owned evidence at review time. I did not duplicate the active run and do not treat the focused suite as completion of those gates.
- Later README, final verification counts, checkpoint/archive and delivery lifecycle changes after `8ad4e39`: not in this reviewed range; the requested scoped follow-up must assess those changes before final delivery claims.

## Assessment

**Ready to merge? Yes, conditional on the required full fresh verification/coverage gates and exact delivered-head CI passing, plus the planned final scoped documentation/evidence check.**

The implementation meets the UC30 main flow and alternate-flow requirements without a blocking cross-layer, snapshot, cursor, visibility or disclosure defect found. The three Minor findings are maintainability, regression coverage and checkpoint consistency items; this approval does not certify pending gates or authorize release.
