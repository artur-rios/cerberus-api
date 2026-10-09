# UC23 ordinary whole-branch review

Reviewed `/tmp/cerberus-uc23-worktree`, base `d72d92072eaba28509597c545f4833a8ef58faa6` through HEAD `3bf6b37c211447cfc74ca277defa75910bcae7ec`. This is the single ordinary review requested by the executing-plans workflow. Checkout, index, HEAD and branches were not modified; no builds/tests, remote actions or subagents were run. The report is the only written artifact.

### Strengths

- The focused Domain → Command → Data → HTTP implementation follows the four-task plan without schema, dependency or cryptographic changes. Required body members, canonical IDs, initial epoch, UTC precision, self-parent rejection, trusted identity/access context, safe error allowlisting and exact public success metadata are enforced at appropriate boundaries.
- `FolderCreateStore` obtains account/session locks and ordered owned profile locks before owned ancestry locks. Its final INSERT SELECT independently checks statement-time expiry, current policy/generation, selected profile, exact active owned ancestry and current owned collection routes. Neither parent lookup nor either selected-route predicate accepts foreign ownership or recipient grants. No foreign account/grant lock is introduced.
- All folder/link/direct-profile/immediate-parent mutations share one transaction. Only affected structural counters are parsed; ciphertext, wrappers and client timestamps stay opaque and unchanged. Complete ancestry is required; visible cycles fail closed while an unselected cycle stays hidden. Typed terminal reservation avoids cross-kind UUID interference.
- The tests exercise actual PostgreSQL and HTTP middleware, native profile proofs in both protection modes, owned collection ancestry, record creation/read using a new folder, and complete nested protection rotation. The controlled lock waits and final-statement expiry/terminal interceptors are materially stronger than ordinary mocked admission tests. The two test-fixture caveats below limit specific claims, not the entire matrix.
- I read the actual six-family coverage log, validation JSON and generated coverage summary: Domain 361, Shared 125, Query 259, Data 1740, Command 841 and WebApi 1593, totaling 4,919 passed with zero failures/skips. Coverage is 98.4% line / 91.7% branch; the six production assembly line values are 100%, 98.4%, 99.3%, 100%, 97.2%, and 95%. Focused results are 48 Domain, 106 Data, 58 Command and 111 HTTP passes. Missing-type RED logs precede each corresponding implementation; the installed HTTP RED is 110 failures / 1 pass. The earlier zero-match installation attempt is not counted as RED evidence.
- Actual logs also show current OpenAPI drift, native corpus 322 cases in four directions, 36 native helper tests, 23 Python helper tests, requirements consistency, locked restore, no known NuGet vulnerabilities and an OSV snapshot with 153 packages and no findings. These remain ordinary validation evidence, not an independent cryptographic audit.

### Issues

#### Critical (Must Fix)

None found.

#### Important (Should Fix)

None found for current development behavior. No current ownership bypass, partial-commit path, stale-access admission or broken native integration was identified in this review.

#### Minor (Nice to Have)

1. **The RO/RW foreign-grant tests do not connect the target folder to the granted collection.**
   - References: `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/FolderCreateStoreTests.cs:48`; `tests/Presentation/ArturRios.Cerberus.WebApi.Tests/FolderCreateHttpTests.cs:51`. The reused `AssociationSetup.Items` and `Grant` helpers also do not add collection membership (`ProfileAssociationSchemaTests.cs:41`).
   - Both cases currently create a grant to an unrelated collection and then test a foreign folder. They establish foreign-parent rejection with an unrelated grant present, but do not establish rejection of a folder actually visible through that grant as claimed by the review-focus evidence.
   - Actual effect: reduced regression confidence and an overstated test claim; the current owner-constrained folder query rejects foreign parents before any route handling, so this is not an observed authorization bug or a reason to block develop.
   - Add the actual `CollectionFolder` membership and assert the fixture grants the intended visibility before asserting folder creation remains 404, for both access modes. Retain unchanged owner/recipient persisted-state assertions.

2. **The `afterParent` failure fixture fires before the immediate folder parent is updated.**
   - Reference: `tests/Infrastructure/ArturRios.Cerberus.Data.Tests/FolderCreateStoreTests.cs:260` (case at line 93).
   - The interceptor watches `UPDATE cerberus.profile`, while the immediate `UPDATE cerberus.folder` occurs later. This proves rollback after a linked-profile mutation, but not rollback after an actual immediate-parent mutation.
   - Actual effect: one narrower-than-described rollback regression check. Inspection confirms the actual parent update remains within the same transaction and precedes commit, so no partial-write defect was found.
   - Keep the existing case under a precise name and add a fault after the successful folder UPDATE, asserting the old parent revision/sequence/stamp plus absence of the folder/links and same-ID retry success.

3. **Generated OpenAPI still understates requiredness and advertises nullable `profileIds`.**
   - References: `docs/contracts/openapi.json:772` (access parameter), `:780` (body), `:5416` (`profileIds`), `:5432` (output schema).
   - Header/body lack `required: true`; output fields have no required list; `profileIds` advertises `nullable: true` although runtime rejects null. The four required request property names and optional nullable parent are otherwise correct.
   - Actual effect: generated clients can present avoidable missing/null inputs and receive 400/401, or model guaranteed output fields as optional. Runtime enforcement is correct. The shared requiredness defects are inherited release Minors; nullable profileIds is an additional observed manifestation to carry with that contract cleanup.
   - Correct the shared schema generation/nullability metadata and regenerate the snapshot before client-release qualification.

4. **Generated 413 metadata promises a body that the actual endpoint does not return.**
   - Reference: `docs/contracts/openapi.json:920`.
   - The response advertises `ProblemDetails` content, while the actual bounded-body HTTP case correctly asserts an empty 413.
   - Actual effect: a generated error parser can attempt to deserialize an absent body. This is the explicitly inherited shared release Minor, not a new runtime failure.
   - Remove the generated 413 content declaration for the shared empty-body behavior before release.

### Plan alignment and all nine rulings

All four tasks are implemented and their interfaces compose as planned. The test mismatches above qualify two evidence claims. The following rulings are accepted for this development review, with their reasons and costs preserved:

1. **Reuse verified UC22 baseline:** reasonable because the baseline tree was reported equal to the freshly tested merged tree; the actual new unfiltered six-family run now provides UC23 regression evidence. Cost if baseline environment assumptions were wrong: the fresh whole-family and six-family checks had to detect that regression. This review does not independently certify previous remote CI.
2. **Selected creation links exactly the selection; account-wide accepts zero/multiple owned profiles:** permits usable new selected content while preventing arbitrary attachment to other selections. Cost: broader client association flows need their separately authorized operation.
3. **Selected owned collection/member ancestry is sufficient; foreign grants never grant administration:** matches current effective owned visibility without transferring ownership. Cost: real-client key provisioning and inherited visibility still need release qualification. Current code satisfies this rule; the specific foreign-grant fixtures should be corrected as Minor 1.
4. **Self-parent 400; existing/typed-terminal folder UUID 409; other kinds independent:** consistent with new-resource semantics and typed identity. Cost: accidental self-parent requests must be corrected rather than treated as revision retries.
5. **Complete active owned ancestry; invisible cycles 404, visible required cycles 503:** preserves hidden scope admission while failing closed on necessary corrupt structure. Cost: operators must repair visible corrupt ancestry before child creation succeeds.
6. **Parse/advance only affected profile/immediate-parent counters; preserve unrelated content and times:** avoids requiring unrelated decryptability or metadata repair for additive structure creation. Cost: unrelated corrupt content remains a separate repair condition for reads/edits/rotation.
7. **Independent security/protocol and external-client approval deferred only for development:** follows the explicit instruction while retaining ordinary ownership/native/race validation. Cost: latent defects must still be detected by mandatory release qualification; this review is not that approval.
8. **Document at `docs/security/folder-api.md`:** follows the established record API contract location and the new README link. Cost: consumers use that link rather than a nonexistent content-site route; runtime is unaffected.
9. **Separate implementation completion from review/remote integration:** satisfies the requirement for one fresh review after all implementation tasks. Cost: Task 4 and the feature-branch README Done entry alone do not establish UC23 Done; exact-head CI, normal merge and all nine DoD checks remain executor obligations.

### Recommendations

Carry the four Minors with explicit dispositions. Do not describe the unrelated-grant or pre-folder-update fixtures as proof of their stronger named scenarios. Preserve the documented development/release boundary, and finish the remaining exact-head remote integration and DoD verification before advancing the batch. This review introduces no second-review requirement.

### Declined to judge

- Independent formal security audit — explicitly deferred by the user for develop; current concrete ownership and safe handling were reviewed ordinarily.
- Independent protocol/cryptographic correctness approval — explicitly deferred; native proof usage and supplied ordinary corpus evidence were inspected, not independently certified.
- Real external-client content-key provisioning, encrypt/decrypt interoperability and usability — explicitly deferred release qualification; these fixtures use native proof/wrapper helpers and opaque envelope bytes, not a complete external client.
- Ordered durable offline inherited visibility and synchronization delivery — designated later synchronization/client integration prerequisite; present direct metadata changes were reviewed without claiming a sync protocol exists.
- Future folder list/get/update/trash/restore/move/purge writers and their lock interaction — outside this creation implementation and not certified by a currently nonexistent writer; those use cases retain their own gates.
- Other future physical-erasure paths — outside this diff and retain their explicit later gates.
- BEFORE UC35 implicit recipient-account foreign-key audit — explicit later gate, not discharged by reviewing creation's current owner-only locks.
- Inherited UC22 permission-outage 500 classification — unchanged outside the supplied diff; not re-audited or repaired in this review.
- Inherited UC21 formatting blank — unchanged outside the supplied diff and has no UC23 client effect.
- Independent verification of prior UC22 remote CI, new remote exact-head CI, mergeability, issue/project closure and nine remote DoD conditions — remote actions were prohibited; the executor must establish these after this local ordinary review.
- Deployment, main/tag/release authorization and production qualification — not requested and explicitly not implied by this development merge verdict.
- Production-scale deep-ancestry latency/load qualification — the implementation uses finite sequential traversal and detects cycles; no workload/latency target or load evidence was supplied for this creation task, so this review makes no throughput certification.
- Decryption/repair of unused ancestor/profile/collection ciphertext or unused client timestamps/counters — intentionally opaque and outside additive creation under ruling 6; necessary structural validation was reviewed.
- Broader selected-profile attachment or administration of foreign granted content — explicitly excluded product operations under rulings 2 and 3; their rejection here was reviewed rather than treated as missing functionality.
- Repair of shared OpenAPI generation in this UC — explicitly retained for release; its current consumer effects were judged and recorded as Minors 3 and 4 rather than silently excluded.

### Assessment

**Ready to merge? Yes — for develop, subject to the executor's remaining normal integration/CI/DoD gates.**

**Reasoning:** The current code implements the planned owned encrypted folder creation with statement-time authority checks and atomic structural metadata, and the actual unfiltered 4,919-pass evidence supports the ordinary regression assessment. No blocking current product defect was found; the four Minors concern narrower test fixtures and shared generated-contract fidelity, while independent security/protocol/client and release qualification remain explicitly outstanding.
