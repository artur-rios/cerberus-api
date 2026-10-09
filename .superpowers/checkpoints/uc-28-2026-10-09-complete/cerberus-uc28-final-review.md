# UC28 Move folder — sole ordinary whole-branch review

Reviewed base `5c5615935786da2ecc8d82b447fbb6d99d85c481` through tested head `06d433eb75438e721bf27bfda02c33522d3d9dfa` in `/tmp/cerberus-uc28-worktree`. HEAD matched the requested head. This review inspected all 21 changed files, the design, plan, complete ledger and supplied review prompt, relevant UC28 requirements, and the existing content-writer/native-binding code. No checkout, source, index or branch mutations, additional reviewer, subagent, or redundant test-suite run were performed. This report is the only file written.

## Strengths

- The public command has exactly two required members, strict JSON attributes, canonical GUID handling, trusted context, safe persistence-result validation, and an explicitly serialized nullable parent. Controller registration adds the intended endpoint and exactly three services.
- `FolderMoveStore.cs:294`–`365` builds the active mixed subtree and correlates scopes by `(kind, member_id)`. Collection and profile subsets are checked separately; direct record associations and profiles reached through collections are included. This is substantive per-member enforcement, not merely a root check.
- `FolderMoveStore.cs:367`–`409` includes grants from the union of every member's resulting collections, captures current pin/envelope evidence, and freezes members, paths, sets, parents and direct links. The final guarded UPDATE (`:92`) compares that evidence inside the authorization statement. Descendant-only required grants are covered by actual native Data tests (`FolderMoveStoreTests.cs:270`) and HTTP tests (`FolderMoveHttpTests.cs:167`).
- The own-account lock and collection-before-content order agree with the existing owner and recipient writers inspected. The tests exercise actual owner writers, reciprocal moves, native recipient root/child/record edits in both orders, seven expiry waits, final-guard lifecycle/native changes, and captured member/link/grant growth.
- Atomicity tests inject failures after actual root/first-parent/second-parent mutations and assert the injector fired. Root/parent sequence faults, no-op semantics, real prior trash operations, retained graph snapshots and current recipient GET/list behavior receive useful coverage.
- Release limits are explicit: current online visibility is implemented, while durable synchronization, external client key provisioning/decryption and later trash behavior are not misrepresented as complete.

## Issues

### Critical (Must Fix)

None found.

### Important (Should Fix)

1. **Most strict-body HTTP cases pass for an unrelated misspelled field.**
   - File: `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/FolderMoveHttpTests.cs:127` (and raw duplicate bodies at `:128`).
   - The base body, mutation lookups and duplicate JSON literals use `parentParentFolderId`, whereas the real contract requires `parentFolderId`. For example, the numeric-string case actually sends `{"expectedRevision":"1","parentParentFolderId":null}`. Even if numeric-string acceptance, canonical-parent validation or forged-field handling regressed, an unrelated unknown field/missing required parent can still produce the asserted 400. Most of this 38-case theory therefore does not demonstrate its named HTTP behavior.
   - Practical effect: the native boundary's advertised regression coverage can remain green while accepting malformed or forged inputs. Command-level tests and inspection support the current production implementation, so this is an important verification defect rather than a demonstrated runtime authorization bug.
   - Fix every unintended `parentParentFolderId` in this theory, preserving all cases, so each body varies the intended condition from the actual valid two-field body. Add a small fixture sanity assertion/control if useful, then run the corrected focused HTTP tests and the required post-fix verification gates. Do not claim a production fix or count these prior passes as evidence for the intended cases.

### Minor (Nice to Have)

1. **The new OpenAPI operation inherits incomplete requiredness/response metadata.**
   - Files: `docs/contracts/openapi.json:1353`, `:1360`, `:7196`, `:7244`.
   - The mandatory access header and request body are not marked required; the four always-present result properties lack a required list; the generic data wrapper does not express its nullable behavior consistently with the established runtime wrapper contract.
   - Practical effect: generated clients can permit invalid omissions and offer weaker/misleading response types. The runtime rejects invalid omissions, and the hand-written API documentation states the actual contract. This remains Minor for the explicitly development-only merge, but is a real release/client-generation follow-up.
   - Fix through the shared OpenAPI generation conventions and regenerate/drift-test all affected operations, rather than hand-editing only this generated endpoint.

2. **OpenAPI advertises a ProblemDetails body for the actual empty 413 response.**
   - File: `docs/contracts/openapi.json:1500`.
   - The generated operation declares three media types containing `ProblemDetails`, while the real HTTP test's 413 branch explicitly verifies an empty body (`FolderMoveHttpTests.cs:209`).
   - Practical effect: a client generated from the contract can attempt to deserialize a nonexistent error document on oversized requests. The status itself remains useful and the ordinary valid move path is unaffected. Retain the existing Minor release follow-up and correct shared response metadata.

## Independent scope-algebra challenge

I accepted the final algebra ruling after tracing the actual SQL, rather than relying on the author's statement. For a stable active tree, write each member's collection set as the root's inherited collection set union its unchanged collection routes below the root (plus direct CR for a record). The analogous profile set is root profiles union unchanged below-root PF/PR routes and PC profiles from those member-specific collections. Every root set is consequently contained in every active descendant's corresponding set. Changing only the root parent replaces a common inherited component; it does not change those member-specific direct components.

If the resulting root sets are subsets of their original sets, any destination contribution is already in each descendant's original sets. A root-passes/descendant-only inherited expansion therefore has no counterexample under the implemented PF/PR/CF/CR/PC rules. I specifically considered direct descendant and record overlaps, profile membership via collections, inactive/terminal routes, and typed folder/record IDs. Overlap can make a descendant pass when the root fails, but not the reverse. Concurrent topology, membership or lifecycle changes invalidate the stable premise and are separately captured and compared by the implementation.

The production SQL nevertheless retains the independent typed per-member EXCEPT checks (`FolderMoveStore.cs:364`–`365`) and repeats them in the final write's authority CTE. This is appropriate defense against changing assumptions. The tested descendant-only native requirement is materially different: a descendant can have an additional direct collection/grant absent from the root, so validating root-only native evidence would be wrong. The current store and both Data/HTTP negative tests cover that case.

## Review Focus assessment

1. Selected-scope non-expansion: both sets are compared for each typed member; real expansion, overlap, narrowing and descendant-only native contributors are covered. No implementation defect found.
2. Cycles, hidden destinations and lock order: visibility precedes stored topology rejection; own-account serialization prevents reciprocal owner cycles; collection-before-content coordinates existing recipient edits. No demonstrated current-writer deadlock found.
3. Current authority/native state during waits and at the root write: statement-time session/lifecycle predicates and frozen current evidence prevent stale authorization and writes; the tests cover waits and final guard changes.
4. Captured growth and independent trash: complete recapture detects growth instead of acquiring late unheld resources; actual earlier trash operations/deadlines remain unchanged.
5. Rollback and no-op: writes are one transaction, mutated graph faults are checked, counters/sequences are guarded, and no-op bumps only the root. No runtime defect found.

## All 18 ledger rulings weighed

1. Baseline reuse: accepted for preparation only; the supplied final fresh run exists and its counts agree with the actual log.
2. Required nullable parent/four outputs: accepted; implemented consistently. The newly found HTTP fixture defect must still be corrected.
3. Folder-only owner admission/native foreign denial: accepted; source authority is resolved before destination/stale/cipher checks, with relevant native foreign validation.
4. Owner-visible root plus each member's two subsets: accepted; independently checked in SQL and against the algebra above.
5. Same-owner visible destination/null/cycle precedence: accepted; current SQL and Data/HTTP cases agree.
6. Persisted current native envelopes as server AF06 binding evidence: accepted for this server boundary, without certification of usable client keys.
7. Strong own-account and ordered captured locks: accepted for the existing writers inspected; the explicit BEFOREUC35 audit remains necessary.
8. Full captured inventory/native comparison: accepted; equality and held-resource checks reject changed/unheld evidence before mutation.
9. Root/distinct changed immediate-parent counters only: accepted for current online behavior; UC48 remains a release obligation.
10. No-op root-only bump: accepted; unchanged parent counter validation is intentionally skipped and has a maximum-revision case.
11. Independent trash and typed terminals excluded: accepted; actual prior operations and snapshots are tested.
12. Target envelope/member structural validation and opaque descendants: accepted; structural metadata is validated while descendant bytes remain opaque.
13. Final root statement as authorization boundary: accepted for current held-lock/current-writer model; no claim about unaudited out-of-band writers.
14. Atomic writes and allowable sequence gaps: accepted; concrete post-mutation fault and sequence tests support the behavior.
15. Independent formal/client approval development-only deferral: retained as explicit user scope, not construed as formal certification from this ordinary review.
16. Source-link actual canonical GUID converter in Command tests: accepted; avoids a copied converter and matches the real boundary without a production dependency.
17. Detached-record and old-recipient-list fixture corrections: accepted after reading the final fixtures. The record begins detached for growth; the old route legitimately still exposes its unmoved source parent. This does not excuse the separate new HTTP fixture finding.
18. Replace impossible descendant-only inherited expansion fixture with real algebra/overlap/native cases: accepted after the independent challenge above. Per-member production guards remain intact.

## Verification evidence

The actual `/tmp/cerberus-uc28-coverage.log` contains six success summaries, zero failures and zero skips: Domain 361, Shared 125, Query 411, Command 1076, Data 2456, WebApi 2129; sum 6558. `docs/coverage-report/Summary.json` reports 98.4% line and 92% branch coverage, with all six production assemblies at least 90% line coverage (Command 100, Data 98.5, Domain 99.3, Query 100, Shared 97.2, WebApi 94.9). These match the supplied evidence JSON. `git diff --check` passed for the reviewed range. No tests were rerun in this read-only review. Green execution does not cure Important issue 1's ineffective assertions.

## Recommendations

Correct Important issue 1 in the single permitted fix pass, keep all strict-body cases, and record the actual corrected test results. Retain both OpenAPI Minors in the release ledger. No additional ordinary review seat is requested; subsequent completion should use executor verification under the existing plan.

## Declined to judge

Every behavior set aside during this review is listed here for the executor's explicit ruling:

1. Independent formal cryptographic/security/protocol qualification — expressly development-only deferred by the user; this was ordinary code review, not independent certification.
2. External-client usable-key provisioning and authenticated decryption after inherited sharing changes — native fixtures establish server binding/visibility, not an independently qualified external client; explicitly retained release obligation.
3. Durable descendant inherited-visibility events and recipient offline removal delivery — UC48 future scope is explicitly documented; current online GET/list behavior was reviewed.
4. Future restore membership/parent revalidation and prevention of revival of revoked sharing — UC50–52 are not implemented in this diff; preservation of current independent trash was reviewed.
5. Physical retained-trash expiration/purge and missing worker-handler completion — UC53 future scope, explicitly a release obligation rather than implemented behavior here.
6. Future grant-creation implicit recipient-account FK lock compatibility — the BEFOREUC35 audit remains mandatory; no grant-creation writer is introduced here.
7. Safety of arbitrary unaudited out-of-band writers after the authorization statement — outside the documented current-writer protocol; current supported writers and final guard were reviewed.
8. Production-scale latency, memory and contention limits for very large/deep vaults — the bounded EXPLAIN/control fixtures establish query/lock shape, not production load qualification.
9. Prior UC27 external-parent active PurgeAt inventory concern — in unchanged prior deletion code; the new move store explicitly validates changed parents and skips unchanged-parent validation for no-op.
10. Prior UC25 constructor recordId naming concern — unchanged code outside this branch; no finding on unread prior behavior is inferred.
11. Prior UC23 narrow-fixture concern — unchanged prior fixtures outside this branch; current UC28 fixtures were reviewed independently and the new strict-body defect is reported above.
12. Prior UC22 permission-outage 500 concern — unchanged prior endpoint outside this diff; current move dependency failure behavior was inspected and covered.
13. Prior UC21 blank-input concern — unchanged prior endpoint outside this diff; no claim that it is fixed by UC28.
14. Remote exact-head CI, mergeability, release/main/tag approval and final issue/project/archive delivery state — executor delivery gates occurring outside this read-only local code review; supplied local tests do not certify those remote gates.

The two inherited OpenAPI issues were judged and graded above, not silently excluded.

## Assessment

**Ready to merge? With fixes.**

The runtime move implementation is aligned with the design and no concrete scope-expansion, stale-native-write or atomicity defect was found. Correct the Important strict-body HTTP fixture defect and complete the prescribed verification before develop delivery; the two inherited OpenAPI Minors and explicitly listed release qualifications remain tracked.
