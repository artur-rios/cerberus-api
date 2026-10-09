# UC-19 whole-branch ordinary code review

Reviewed base `15cf4fac7bfa7db2552df5618e0f4a236c0fd5b1` through head `0fc261bedce02663454e2dae163ff9c63c7cd1b2`, the supplied whole-branch diff, binding design, three-task plan, and all 11 ledger rulings. The checkout, index, HEAD and branch remained unchanged. This is the requested ordinary implementation review, not formal independent cryptographic or actual-client qualification.

## Strengths

- `src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordUpdateStore.cs:26`: the actor/session/selection, sorted collections, sorted grants, then target ordering is explicit. The account and record `NO KEY UPDATE` correction allows FK reference checks while retaining serialization of content/policy changes. No foreign account/profile or folder row locks are introduced.
- `src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordUpdateStore.cs:60`: after waits, the store takes a fresh targeted snapshot, checks held authority sets, validates every contributing foreign native grant, and checks writable permission before revision conflict. Hidden scope is rejected before native decoding or target corruption can disclose information.
- `src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordUpdateStore.cs:77`: the final UPDATE uses the same complete recursive authority query, current statement time, and both collection/grant validated-set guards. It does not trust the previously discovered ancestor path. Newly introduced permission cannot authorize the update through an unvalidated native route.
- `src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordUpdateStore.cs:67`: stored envelope and metadata checks, safe revisions/sequences, microsecond normalization, same-epoch/fresh-salt-and-nonce validation, postwrite verification and transaction disposal protect both success integrity and rollback behavior.
- `src/Application/ArturRios.Cerberus.Command/Records/UpdateRecordHandler.cs:21`: null results, error-with-data results, arbitrary store errors and mismatched metadata are rejected without partial output. The DTO and HTTP path retain exactly the three request fields and four success metadata fields.
- `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/RecordUpdateStoreTests.cs:93`: real PostgreSQL tests cover expiry across all six lock classes, committed permission changes during waits, final-statement changes, FK insertion contention, reciprocal edits, creation coexistence, and owner rotation. The HTTP suite exercises actual native Master/PerProfile sessions and native recipient grants.

## Issues

### Critical — must fix

None found.

### Important — should fix

None found. No blocking implementation deviation from the binding spec or the 11 recorded rulings was identified.

### Minor — existing tracked contract-generation issues

1. **Generated requiredness is incomplete.** `docs/contracts/openapi.json:2352`, `docs/contracts/openapi.json:2359`, and `docs/contracts/openapi.json:6270`: the vault-access header and body are not marked required, and output properties are not listed as required. A generated client can therefore treat mandatory inputs or guaranteed success metadata as optional. The three body members themselves are correctly required. Fix the shared OpenAPI generation/transform rules and regenerate before release, as already tracked; this is not a new UC-19 merge blocker.
2. **Generated 413 content differs from the actual response.** `docs/contracts/openapi.json:2499`: the schema advertises `ProblemDetails`, while the common request limit returns an empty body. A generated client can attempt to deserialize nonexistent content. Fix the common 413 response metadata/generation and regenerate before release. The implementation documentation and actual HTTP test correctly describe/assert the empty body.

## Focus analysis and rejected defect hypotheses

- **Larger overlapping grant combinations:** two valid RW contributors plus an invalid active RO contributor still fail `grants.All(...)` with 503; two valid RO contributors plus an invalid active RW contributor also fail native validation before permission. Valid RW plus malformed revoked, unselected or noncontributing grants ignores those irrelevant grants. Repeated direct/ancestor routes deduplicate relevant grant IDs. The schema allows only one grant per collection/recipient, so duplicate grants for that same pair are not a reachable stored combination. Structurally ineligible access/revision values cannot authorize a route. These conclusions follow from the SQL eligibility predicates, `relevant_grants`, and universal native validation; no additional combination test was run during review.
- **Authority added after discovery:** a newly included collection or a newly contributing grant is checked against the post-lock, native-validated sets, even when the row happened to be locked by the broader eligible-owner lock query. A held but previously irrelevant malformed route cannot silently become authority. Moving into a previously validated collection is safe because its current contribution is recomputed; moving into an unvalidated collection conflicts.
- **Removal and ancestry races:** a removed direct membership, removed inherited membership, reparent to a hidden/new ancestor, cycle, terminal marker or owner closure committed before the final statement is reevaluated. An already committed removal can yield hidden 404 or RO 403. Changes after the statement snapshot may linearize afterward. No captured-path shortcut was found.
- **Multiple resources and reciprocal rotation:** each UC-19 call locks collections for one target owner in the same public-ID order used by rotation, then grants, then one record. Different recipient writers use compatible SHARE collection/grant locks and serialize only at the target. Rotation reads its inventory before those locks but uses optimistic record revisions in SaveChanges, so a recipient winner prevents stale rotation replacement. Two owner rotations do not require foreign recipient account locks. Password/protection transitions preserve recipient/author public pins, and content rotation cannot alter a contributing locked collection/grant underneath native validation. The owner-rotation code and resource concurrency mapping were inspected; no foreign-account cycle or lost-update path was found.
- **Expiry and terminal checks:** account/session/selection/resource reloads do not substitute for the final statement predicate. Exclusive expiry and issued-at checks use `statement_timestamp()`. Independent terminal markers and foreign-owner lifecycle are included in final scope even though their rows are not locked.
- **Corrupt persistence and alternate JSON:** strict stored JSON prevents quoted-number, duplicate, unknown and case variants from becoming valid native evidence. The command independently checks result target, next revision, safe sequence and exact normalized UTC time. Known failure codes are allowlisted. Postwrite invalid sequence/time or a provider failure exits without commit.
- **Timestamp and retry semantics:** an identical envelope or older client edit time still advances a matching revision by design; lost-success retry with the old revision conflicts and preserves the winner. No accidental offline last-writer-wins behavior was found.

## Declined to judge or intentionally deferred

Each item below was explicitly considered; none is silently dropped.

- Formal independent cryptographic/protocol qualification: deferred by the user's development-only authorization; this ordinary review assessed current native binding and authorization effects but does not provide that qualification.
- Actual external-client interoperability approval: deferred by the same authorization; native fixture/HTTP coverage is useful evidence but is not that approval.
- Repair of global cross-kind terminal GUID false denials: existing conservative behavior is retained by an explicit ruling and cannot grant unauthorized access here; typed repair remains mandatory before any physical purge, with selected profiles never reset to accountwide access by purge.
- Public grant-creation writers and their implicit recipient FK locks: no such writer is added here; the documented audit before UC-35 remains required. Existing rotation and new UC-19 interactions were reviewed rather than deferred.
- Fixing shared OpenAPI requiredness and empty-413 metadata now: existing recorded release work, reproduced above as Minors, with runtime behavior tested correctly; it remains required before release.
- Plaintext field/template validation or ciphertext authenticity by decryption: the server's opaque-content contract does not provide the keys or plaintext needed to perform these checks; envelope shape and native authority binding are enforced here.
- Offline timestamp resolution and content-key rotation through this endpoint: intentionally separate use cases; current expected-revision and same-epoch semantics are documented and implemented.
- Relationship mutation and parent/profile revision bumps: expressly excluded from a content-only write; inspection confirms only target content/metadata changes.
- Additional folder row locks or acquiring newly discovered earlier-order authority locks late: intentionally rejected by the concurrency design; complete final ancestry and safe 409/reload replace those operations and avoid existing creation lock-order conflicts.
- Eliminating eligible target-owner collection contention or replacing the purpose-built query with a permission framework: accepted design cost rather than a correctness defect; recursive record discovery remains targeted to one record.
- Treating the committed README Done row/task checkboxes as proof of completed remote integration: the explicit ledger ruling makes this branch content anticipatory; exact-head CI, merge, issue/project closure and all DoD checks still have to finish before claiming UC-19 integrated.

## Verification evidence

Read and checked the fresh completed full-suite log: Domain 313, Query 259, Shared 125, Command 567, Data 1188, Web 1137 — **3589 passed, zero failed, zero skipped**. Read the generated coverage summary: **98.3% line, 91.5% branch**, with all six production assemblies above 90% line coverage (Command 100, Data 98.3, Domain 99.2, Query 100, Shared 97.2, Web 95.2).

The focused logs confirm Data 104, Command 71 and actual HTTP 86 passing with zero failures/skips. I inspected their behavioral test bodies and the final SQL instead of duplicating a completed full suite. `git diff --check 15cf4fa 0fc261b` passed. OpenAPI shape was inspected directly. The parent's native, dependency, helper and specification audit results remain recorded delivery evidence; I did not rerun those audits or claim new qualification from them.

## Recommendations

Proceed with the existing exact-head CI and integration gates. Carry the two existing OpenAPI Minors, typed-terminal repair prerequisite, future recipient-FK audit and independent release qualifications forward explicitly. No blocking code fix pass is required from this review.

## Assessment

**Ready to merge? Yes — for development, after the existing CI/integration gates.**

The branch implements the current-authority content writer with effective lock ordering, final-statement ancestry/permission checks and fail-closed native/result validation. No new Critical or Important issue was found; the tracked release qualifications and two shared OpenAPI Minors remain outstanding.
