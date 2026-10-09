# UC-21 final ordinary whole-branch review

Reviewed `a59310da31ea48e3561d8bd2507f6ca62b5ae44d..1c6e9adfddc075716dc0fdda9a01cb58fa6e7746` in `/tmp/cerberus-uc21-worktree`. This is the single final ordinary review. Checkout, index, HEAD and branches were left unchanged; no subagents or test reruns were used.

## Strengths

- The implementation matches UC-21 main flow and AF-01 through AF-05, FR-RC-06, BR-07/28, and the binding design and three-task plan. I read the generated review package, implementation/history, tests, delivery documentation and ledger, including all 14 rulings and their costs.
- `RecordMoveStore.cs:78` recomputes current source/destination ancestry, selected scope, held collection/grant sets and exact native evidence inside the guarded write. Its profile **and** collection subset checks handle the important case where a selected user can already see a destination but moving there would expose existing content through another profile or collection. Direct routes survive; proper narrowing is allowed.
- The collection-before-content order agrees with the current recipient edit writer. Own-account serialization protects current owner create, association, rotation and trash writers; non-key immediate-folder locks permit the parent FK check. There is no foreign-account/profile lock added by this operation. Newly contributing unheld authority produces a retry instead of out-of-order lock acquisition.
- Resulting outgoing native evidence includes every contributing grant and current owner/recipient protection material. Full evidence equality prevents a recipient-pin change after validation from being accepted by the final statement. Native-visible foreign RO and RW both deny parent administration before destination/content inspection.
- Record and immediate-parent updates share one transaction. The implementation preserves exact ciphertext/client time and direct associations, verifies safe advancing counters, handles no-op/lost-response semantics explicitly, and rolls back on post-record and post-parent failures.
- Tests exercise real PostgreSQL locks, controlled actual-store races, overlapping native contributors, current ancestry changes, sequence failures and actual HTTP contracts. The selected pure-folder-to-null HTTP cases also assert subsequent get/list disappearance while preserving the selected handle.
- The test-only cached service JWT lifetime adjustment is consistent with the documented expiry failure and does not alter production validation.

## Issues

### Critical (Must Fix)

None found.

### Important (Should Fix)

None found for this development merge.

### Minor (Nice to Have)

1. **Generated requiredness does not fully describe the runtime contract.**
   - Files: `docs/contracts/openapi.json:2352`, `:2359`, `:5531`.
   - The vault-access header and request body are not marked required, and the four success fields have no required list. The command's two input fields themselves are correctly required and nullable `folderId` is represented correctly.
   - Generated clients can construct requests that runtime rejects and treat guaranteed response fields as optional. The handwritten API documentation and HTTP tests are accurate, and runtime rejects omission safely; this is a contract-generation deficiency rather than an authorization bypass or lost write.
   - Correct the shared OpenAPI generation before client/release qualification and pin these details in its contract checks.

2. **Generated 413 advertises a body that runtime intentionally omits.**
   - File: `docs/contracts/openapi.json:2499`.
   - The response declares `ProblemDetails` content, whereas the middleware and HTTP test require an empty 413. A generated client can attempt to parse a nonexistent error payload, obscuring the oversized-request diagnosis.
   - Remove the 413 content schema through the shared generator correction. This remains a release/client integration obligation; the actual HTTP endpoint rejects oversized input correctly.

3. **The full branch whitespace check is not clean.**
   - File: `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/RecordMoveHttpTests.cs:143`.
   - Fresh `git diff --check a59310d..1c6e9ad` exits 2 for trailing whitespace on the added blank line. This has no runtime impact, but a blanket full-branch `diffPASS` assertion is inaccurate.
   - Remove the spaces in an authorized cleanup, or explicitly retain this Minor and record the actual range-check result. A clean working-tree-only diff check does not check already committed changes.

## Review focus and evidence

I examined mixed direct/inherited profile and collection routes, source/destination paths sharing ancestors, destination reparenting and cycles, newly contributing collections/grants, current owner/recipient writers, foreign pin changes, no-ops, stale retries and partial-mutation rollback. The SQL set comparisons apply across all effective active owned profiles/collections, not just the selected profile or the immediate parents. The final evidence comparison and held-set predicates remain effective when multiple collection routes contribute. I found no concrete missing guard in these combinations.

The final coverage log reports Domain 313, Shared 125, Query 259, Command 717, Data 1446 and WebApi 1375: **4,235 passed, zero failures/skips**. `/tmp/cerberus-uc21-validation.json` records **98.3% line and 91.7% branch coverage**, with all six production assemblies above 90% line coverage. I inspected the log, validation artifact, test assertions and flow map; these are supplied execution evidence, not tests rerun by this reviewer. HEAD remained `1c6e9ad`, and `git status --porcelain=v1` was empty.

## Recommendations

Retain the three Minors with their real effects and correct the branch-range whitespace verification record. No blocking implementation correction pass is requested. Preserve exact-head CI, normal development-merge and delivery/issue bookkeeping gates; the README's Done status is contingent on completing those delivery steps.

## Declined to judge

- Independent formal protocol/cryptographic/security qualification — explicitly deferred by the user for development only; ordinary runtime ownership, signature-binding and concurrency correctness were reviewed here.
- Real-client authenticated decryption and trusted root availability after scope changes, including any additional opaque client provisioning — the fixtures prove server/native-envelope behavior, not real-client operability; qualification and any required implementation remain mandatory before release.
- Durable offline synchronization of inherited visibility additions/removals — this endpoint dynamically updates current get/list scope; the separate synchronization integration remains required before release.
- Physical-purge correctness and the inherited cross-kind terminal-GUID false-hiding behavior — UC-21 performs no physical purge and retains the conservative existing exclusions; mandatory typed repair and same-GUID/different-kind survival verification must precede **any UC-22 physical purge**. This review does not certify that known behavior as correct or permit clearing selected ProfileId to null.
- Deadlock freedom of future grant-management writers — current create/association/edit/trash/rotation interactions were reviewed; unimplemented writers still require the documented implicit recipient-account FK audit before UC-35.
- Long-running service-credential renewal behavior — changing the shared fixture lifetime does not test renewal; dedicated expiry/adapter coverage remains separate from this move feature and production token policy is unchanged.

No other considered behavior was set aside as outside the plan/spec. Spec silence was not used to downgrade runtime findings.

## Assessment

**Ready to merge? Yes, into develop after the existing exact-head CI and delivery gates.**

The ordinary review found no blocking authorization, native-evidence, concurrency, atomicity or HTTP implementation defect. The recorded Minors do not prevent the development merge; this verdict grants no main/tag/release qualification and does not waive any of the listed release or pre-purge obligations.
