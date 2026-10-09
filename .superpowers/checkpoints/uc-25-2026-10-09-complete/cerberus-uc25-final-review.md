# UC-25 ordinary whole-branch review

Reviewed base `955ca9db39753d27fda5cf2637f6e3970cfd6a1b` through tested head `36f93bc185fac8c8e31d47956216146101ad9c1d` in `/tmp/cerberus-uc25-worktree`. This is the single ordinary review requested by the filled reviewer prompt. I read the binding spec, completed plan, ledger, production changes, tests, public documentation/schema changes, surrounding projection/native binding/schema code, and actual verification artifacts. Review was read-only; no builds, test reruns, branch/index/HEAD edits, remote actions or subagents. File references below are relative to that worktree.

### Strengths

- `FolderReadStore.cs:28` captures session, target, ancestry, organization references and native evidence in one statement. Database statement time governs expiry; selected-profile disappearance fails access rather than widening scope. The materialized public-ID candidate precedes recursion, and schema uniqueness supports the one-target assumption.
- `FolderReadStore.cs:70` uses actual folder membership only. Direct profile references are owned/current and direct; collection references are effective contributors. The parent checks at lines 101–106 explicitly exclude the target itself. Every independently permitted parent route also contributes to target authority and therefore falls within native verification.
- Hidden and incomplete ancestry cannot reach `visible`; scoped active cycles fail safely. Grant parsing follows the absent-item check, preserving hidden-target behavior. Unselected foreign evidence is excluded before native parsing. Owner and recipient pins are parsed once and reused for every relevant grant at lines 128–142.
- `GetFolderHandler.cs:20` validates the full result before attaching data, including target identity, arrays, parent and the reused strict folder projection. Controller transport checks and explicit DI integrate the endpoint without changing existing POST/list actions.
- Tests exercise actual PostgreSQL/native bindings and HTTP flows. Record-only negatives first demonstrate successful record reads. Current-selection/expiry, overlapping grants, typed identities, member removal after SQL, no mutation, and bounded candidate/ancestry plan assertions are meaningful behavioral checks.

### Plan and review-focus assessment

All three implementation contracts are present. The eight design rulings are consistent with the implementation: (1) merged UC24 baseline reuse is supported by fresh UC25 full-suite evidence; (2) the eight-field detail is implemented without child inventories; (3) direct owned profiles/effective collections are preserved; (4) immediate-parent authority excludes target-only routes; (5) record membership never grants folder scope; (6) every eligible foreign contributor is verified with reused party pins; (7) hidden/incomplete ancestry is nonrevealing and active scoped cycles fail safely; (8) development-only independent qualification deferral does not replace concrete checks or release gates. I found no significant implementation deviation requiring a design decision.

The five supplied focus lines were reviewed verbatim:

- Direct target profile links or member-leaf grants must not expose an otherwise unshared immediate parent, sibling or owner profile; record-only routes must never expose the folder even when actual record access succeeds.
- Hidden or incomplete target ancestry must return404 before corrupt native/content inspection, relevant fully active cycles503 and unrelated deep/corrupt inventories remain uninspected.
- Every overlapping foreign collection contributing to target or visible parent authority must be native-verified; corrupt unselected/unrelated routes cannot deny permitted content and pins are parsed once per party.
- A selected profile becoming dangling/trashed/terminal or current policy/expiry/grant/member removal must never widen access; after-SQL changes preserve one snapshot and next GET observes removal.
- Public detail arrays and parent must be complete, nonnull/unique/nonzero and correctly typed, and corrupt native envelope/time/revision/sequence or extra GET transport selectors must fail safely without partial payload or mutations.

Code and recorded tests support these boundaries. Incomplete ancestry is guarded by the same-owner recursion plus the required null-parent terminus; the normal schema additionally prevents dangling/cross-owner parents. I did not find a dedicated deliberately broken-FK incomplete-ancestry fixture in the new tests, so this particular defensive case is assessed from the SQL rather than claimed as separately demonstrated integration coverage.

### Verification evidence

The actual `/tmp/cerberus-uc25-coverage.log` records 5,474 passed, zero failed/skipped: Domain 361, Shared 125, Command 841, Query 411, Data 1,971, WebApi 1,765. Actual `docs/coverage-report/Summary.json` reports 98.4% line and 92.1% branch; the validation artifact lists all six production assemblies above 90% line. Focused logs show Query 55, PostgreSQL 127 and HTTP 83 passes, followed by whole Query 411 and Data 1,971. RED logs show missing named query/store types and 82 missing-route HTTP failures with one middleware pass.

I inspected locked-restore, direct/transitive NuGet audit, native OSV (153 packages, no findings), corpus (322 cases in four directions), native helpers (36), Python helpers (23), specification verification, OpenAPI drift/shape and whitespace evidence. The Python helper's sample coverage percentages are fixture output, not production coverage. The temporary installer alias assertion is recorded as a pre-install helper correction, not a runtime product fix. The checkout reported clean at the stated head.

### Issues

#### Critical (Must Fix)

None found.

#### Important (Should Fix)

None found for the authorized develop merge.

#### Minor (Nice to Have)

1. **Shared generated requiredness/nullability still understates this endpoint's contract.** `docs/contracts/openapi.json:781`, `:6183`, `:6207`, `:6220`. The access header is optional in the schema, only `envelope` is required on detail, and both identifier arrays are nullable. Generated clients can omit required access and receive 401, and must unnecessarily handle missing/null fields that successful runtime responses always provide. This does not bypass authorization or cause the server to emit partial payloads. Correct the shared schema generation/annotations and regenerate before client release, retaining contract assertions. This is the already recorded release follow-up, now also applicable to UC25.

2. **The generated 413 body does not match the actual empty response.** `docs/contracts/openapi.json:889`. It advertises `ProblemDetails` content, while the shared limit middleware returns an empty body and the new HTTP test asserts that behavior. Clients that attempt to deserialize the advertised error object can fail while handling oversized requests. Represent the actual bodyless response in the shared generator before client release. The status and rejection behavior are correct; no content is disclosed.

3. **The folder query constructor retains a record-oriented parameter name.** `src/Application/ArturRios.Cerberus.Query/Folders/GetFolderQuery.cs:4`. `recordId` is assigned to `FolderId`. Current positional callers behave correctly, but named callers and maintainers receive a misleading contract. Rename the parameter to `folderId` in a later cleanup; this has no current HTTP or authorization effect.

### Recommendations

Carry the two OpenAPI issues into the shared before-client-release work and retain their actual runtime effects in the disposition. Keep the constructor rename as a small maintenance follow-up. A future corruption-fixture improvement can directly exercise missing/cross-owner ancestry with integrity constraints deliberately bypassed, while preserving the current hidden-before-native assertions; this is additional confidence in an already explicit defensive SQL guard, not an observed production defect.

### Declined to judge

- Independent formal security/protocol/cryptographic approval: explicitly deferred by the user for develop; ordinary ownership, native binding, safe output and snapshot behavior were reviewed here.
- Real external-client key provisioning, decryption and end-to-end operability: this review has native server fixtures, not a separately qualified external client; mandatory release qualification remains.
- Main/tag/release/deployment readiness: the authorization and review target are develop; this verdict does not approve release gates.
- Durable ordered offline inherited-visibility delivery: a current GET snapshot does not implement durable synchronization; assigned to later synchronization work.
- Future folder update/trash/move/restore/purge implementations: outside this read endpoint; direct database mutations in tests demonstrate current reads re-evaluate state, not correctness of future writers.
- Other physical-erasure paths and the implicit recipient-account FK audit: unchanged by this diff; the mandatory audit before UC35 remains an explicit later gate.
- Production workload/load certification: no production workload target is specified; candidate/ancestry EXPLAIN assertions demonstrate bounded traversal only, not throughput or overlap cost under production load.
- Historical UC24 merged-tree/tested-head/CI provenance: accepted as recorded executor evidence; fresh UC25 verification was inspected, but remote history/CI was not independently queried under the read-only task.
- Exact-head remote CI, mergeability, branch-policy satisfaction, issue/project updates and final merge/archive cleanup: post-review executor delivery gates, not actions authorized for this reviewer.
- Inherited UC23 narrowed foreign-grant/post-parent-failure fixtures: unchanged outside this diff with recorded later dispositions; UC25's own real folder-membership and native-access fixtures were reviewed.
- Inherited UC22 permission-outage 500 classification: unchanged outside this diff with a recorded later disposition; UC25 required-dependency 503 behavior is independently implemented/tested.
- Inherited UC21 formatting blank: unchanged outside this diff with a recorded later disposition and no UC25 runtime effect.
- Child inventories in the detail payload: explicitly excluded by the eight-field ruling; clients must use permitted list/detail calls, accepting the extra navigation requests.
- Exposure of private ancestry or inherited-as-direct profile links for client convenience: explicitly excluded by the visibility rulings; clients must tolerate a null parent and distinguish inherited visibility from direct organization.
- Expanding record-only authority into container navigation: explicitly prohibited by the folder authority ruling; clients with only record scope must navigate without private container metadata.
- Availability despite a corrupt eligible overlapping contributor: the binding ruling deliberately requires whole-response 503; this review does not replace that with best-effort success. The cost is repair and additional signature work for overlapping routes.

### Assessment

**Ready to merge? Yes — to develop, subject to the executor's remaining exact-head CI and delivery gates.**

**Reasoning:** The implementation preserves concrete target/parent ownership boundaries, verifies all relevant native evidence, and returns a completely validated detail from one current snapshot; recorded fresh verification is substantial and passing. No blocking defect was found; the three Minor findings have limited schema-consumer or maintenance effects and do not constitute independent release qualification.
