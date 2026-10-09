# UC27 whole-branch review

Reviewed `a03bf90a99bc6310e5a8b11841203363fa0a7c8b..5bff5e8fd68ba90596588f2d326ca360f8f2910c` in `/tmp/cerberus-uc27-worktree`, against the complete review prompt, binding spec, completed plan, ledger and retained validation evidence. This is the single fresh whole-branch review. Review was read-only; no build/test execution, checkout/index/branch changes, GitHub actions or additional reviewers.

## Strengths

- `FolderTrashStore.cs:31` takes the own-account lock before structural locks; profiles/collections precede folders/records, matching the relevant current owner and recipient writers. The tests exercise actual owner create/move/association/rotation stores and actual recipient folder/record update stores, including both recipient winner orders.
- `FolderTrashStore.cs:152` admits only the active same-owner subtree and its active records. Actual previous record/folder deletion tests establish detached independent trash, preserve earlier operations/deadlines, and reload the surviving root revision. Typed terminal branches and cross-kind aliases are handled separately.
- `FolderTrashStore.cs:42` rechecks current authority after waiting phases. The guarded root statement at line 69 includes current actor/session/selection/ancestry and the captured complete inventory. Folder-only PF/CF authority excludes record-only container access; all relevant native contributors are verified before a visible foreign-owner denial.
- `FolderTrashStore.cs:69` through line 104 perform one transactional cascade, preserving opaque bytes and edit times, using the actual root database timestamps for every member, and creating one operation, typed snapshots and one queue item. Member and parent sequences are checked for safe strict advancement. Snapshots include inactive direct associations and original public parents.
- The strict command/controller integration follows the existing recoverable record contract, with exact input and success metadata, hidden-resource precedence, safe error mapping, no-store transport and explicit DI. Native HTTP tests establish real permitted reads before record-only/foreign denials.
- Retained full-suite output reports 6,110 passing tests, zero failures/skips: Domain 361, Shared 125, Query 411, Data 2,245, Command 987, WebApi 1,981. `/tmp/cerberus-uc27-validation.json` reports 98.4% aggregate line and 91.9% branch coverage, all six production assemblies above 90% line. Focused retained results are Command 75, Data 147 and HTTP 108. I inspected the evidence rather than rerunning it.

## Issues

### Critical (Must Fix)

None identified.

### Important (Should Fix)

None identified for the current development implementation.

### Minor (Nice to Have)

**M1 — External parents omit the specified purge-state consistency check.**

- File: `src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderTrashStore.cs:63` (also the `External` shape at line 131 and projections at lines 168–170).
- Admitted folder/record members reject a non-null `PurgeAt`, but active external profiles, collections and the root's surviving parent are captured without that field and validated only for revision/sequence/edit time. An external parent with `DeletedAt = null` and `PurgeAt != null` therefore gets its structural bump and allows the cascade to commit. The schema does not prohibit that inconsistent state.
- This is a narrow deviation from the spec's structural purge-consistency requirement for affected external metadata parents. It does not introduce an authorization bypass or alter that parent's existing deadline, and I found no normal current writer producing this state, so it is not a merge blocker.
- Include `PurgeAt` in `External` and its JSON projections; reject non-null values for parents being bumped. Extend the existing real-store corrupt-external-parent cases for profile/collection/folder purge inconsistency and unchanged full-graph snapshots.

**M2 — DELETE contract metadata understates required input and success fields.**

- File: `docs/contracts/openapi.json:781`, `:788`, `:6426`.
- The vault-access header and whole request body are optional in OpenAPI, although runtime requires both; `DeleteFolderOutput` has no required-field list despite every successful response containing all six fields. The property-level `expectedRevision` requirement is present.
- Generated clients can expose calls that necessarily fail with 400/401 and unnecessarily optional success metadata. The runtime validation remains correct. This repeats the documented shared schema-generation limitation and remains a client-release follow-up.
- Correct the shared metadata generation, regenerate the contract and preserve a shape assertion for header/body requiredness and the six success fields.

**M3 — Advertised 413 body differs from the actual empty response.**

- File: `docs/contracts/openapi.json:928` (endpoint annotation: `src/Presentation/ArturRios.Cerberus.WebApi/Controllers/FoldersController.cs:26`).
- The contract advertises `ProblemDetails` for 413, while the retained HTTP test and shared transport behavior return an empty body. A client generated to deserialize that error body can fail parsing and obscure the useful HTTP status.
- This is the inherited transport/metadata mismatch, not a cascade failure. Correct the shared 413 metadata to declare no body, or deliberately change the shared transport and its tests together, before client release.

## Review Focus assessment

1. **Selected owner cascade / independent trash:** Met. Root admission retains the selected profile; descendants need not have separate links. Traversal excludes independently trashed and typed terminal branches, and shared records are included by containment. Store tests at `FolderTrashStoreTests.cs:27`, `:36`, `:52`, `:228` and HTTP tests at `FolderTrashHttpTests.cs:92`, `:104`, `:117` substantiate the intended effect.
2. **Competing owner/recipient writers:** Met for the current writers reviewed. Strong own-account serialization and collection-before-content ordering are consistent with create/update/move/association/rotation code. Actual store races at `FolderTrashStoreTests.cs:165`, `:175`, `:238` preserve winner state. No foreign account/profile or upward-chain lock is introduced.
3. **Expiry / visibility / selection:** Met at the specified waiting and final-write boundaries. Current statement-time authority is recomputed after phases and within the root update. Tests at `FolderTrashStoreTests.cs:138` and `:145` cover natural expiry across all named structural waits and expiry/typed terminal changes immediately before root write. Hidden/record-only errors precede stale ciphertext/revision inspection; selected access is never nulled.
4. **Inventory / native contributor changes:** Met. Complete ordered captured data is reloaded and compared, then compared again in the root statement. Existing-row reparent/link growth tests at `FolderTrashStoreTests.cs:150` force 409 without consuming winning changes. All relevant foreign contributors are checked in the same authority snapshot before denial; selected routes exclude unrelated contributors. New owned entity inserts are serialized through the strong account lock and current writers' account-first order.
5. **Rollback / deadline / restore evidence:** Met, with M1's external-parent defensive limitation. Guards and post-write checks enforce counters and common root timestamps. Real transaction tests inject failures after root/child/record writes and before later link/parent/operation/entry/queue commands, thereby exercising rollback of previously executed stages. The queue-failure case rolls back already-created operations/entries; these tests should not be described as an injected failure after a successful queue insert. Restore snapshots retain relationships without authorizing their later restoration.

## Initial ruling assessment

- R1, verified UC26 baseline reuse: accepted as local development evidence; final fresh full-suite evidence exists. Remote delivery qualification is separate.
- R2, one revision input/six metadata outputs: implemented and validated; M2/M3 qualify generated documentation, not runtime shape.
- R3, owner-visible root admits active owned subtree: implemented consistently with the explicit cascade requirement and shared-record behavior.
- R4, strong own account serialization: justified for the current writer set; future grant-creation FK audit remains mandatory.
- R5, PF/CF-only authority and all relevant native contributors: implemented; record-only access never admits containers.
- R6, complete active inventory/all stored direct links: implemented with stable ordering and both reload/root-statement comparison.
- R7, preserve independent trash/terminal identities and reject old active typed entries: implemented; actual prior deletions support the claim.
- R8, root guarded write linearizes authorization: coherent with the held-lock transaction; natural expiry afterward does not undo an authorized cascade.
- R9, retain damaged opaque owned envelopes: implemented without plaintext parsing/decryption; required member counters/time/purge still validate.
- R10, one distinct active external-parent bump: implemented; M1 is the limited missing external-parent purge-consistency check.
- R11, one operation/many entries/one 720-hour queue, later restore/purge/sync: implementation and documentation agree; this is not completed release retention functionality.
- R12, development-only independent qualification deferral: respected. Ordinary authorization, lifecycle, transaction, native boundary and strict transport behavior were reviewed here.

## Recommendations

Record M1 as a defensive consistency follow-up and retain M2/M3 in the shared contract release backlog. Keep the existing future-writer lock-order audit and restore/purge/sync gates explicit. Preserve the precise test-evidence wording: missing-type RED and missing-route RED are distinct from test-helper/analyzer and EXPLAIN-fixture corrections; the latter are not product regression fixes.

## Declined to judge

Each line identifies a behavior or qualification considered and set aside, with the reason; the executor must rule explicitly on each.

- Independent formal security, protocol and cryptographic certification — explicitly deferred for development by the user; this review judges current concrete implementation effects and is not that independent certification.
- Real external-client operability, including a deployed client's handling of DELETE bodies and generated SDKs — explicitly deferred; native in-repository HTTP tests are evidence for this server boundary, not external-client approval. M2/M3 remain actual reviewed metadata findings.
- Completed trash listing, restoration, emptying and physical purge after the 720-hour deadline — UC50–53 are expressly later work; current queue insertion and retry-on-missing-handler behavior must not be represented as completed physical retention enforcement.
- Authorization and conflict policy of a future restore implementation — retained snapshots are evidence only; membership must be revalidated and old typed entries consumed under UC51. No restore endpoint is implemented here.
- Durable offline/inherited-visibility synchronization and recipient copies already cached outside the service — UC48 is a later gate; active server reads/lists hide the cascade, but this change cannot retract already-held copies.
- Future grant-creation implicit recipient-account FK behavior, especially UC35 — no such new writer is added in this diff; the explicit before-UC35 audit is required before extending current lock-order conclusions.
- Arbitrary future or out-of-band writers that ignore the account/structural locking contract, including terminal lifecycle changes after authorization linearization — current guarded behavior and injected races were reviewed; universal safety for unaudited writers cannot be certified by this branch.
- Cancellation of an already authorized atomic cascade solely because its access handle expires after the guarded root write — the explicitly chosen transaction authorization boundary permits completion; this is not stale authority at the root write.
- Owner cascade effects on descendants lacking individual profile links and records shared elsewhere — explicitly intended and documented owner operation; I did not redefine selected root admission as a per-descendant permission requirement.
- Repair or decryption of damaged owned ciphertext — intentionally outside recoverable deletion; byte preservation is checked, and unreadable content does not need to become decryptable to be trashed.
- Rejection of a foreign deletion request because one relevant overlapping native contributor is corrupt even if another is valid — explicit fail-closed policy, with selected-scope isolation verified; not downgraded to successful visibility proof through the other route.
- Automatic repair of active resources with old typed trash entries — deliberate 409 prevents reusing retention identity/deadlines; reconciliation belongs to restore, not this deletion transaction.
- Production-scale latency, memory consumption and contention for very large cascades — per-item writes and repeated complete inventory materialization are visible costs; retained EXPLAIN evidence bounds unrelated traversal/locks only and is not a production load qualification. No measured failure at supported production scale was supplied.
- Corrupt ancestry created by disabling relational integrity or arbitrary database tampering beyond the supplied fixtures — SQL/schema defensive guards were inspected; no integrity-bypass runtime fixture or universal corruption-repair claim is made.
- PostgreSQL sequence gaps after a rolled-back operation — expected nontransactional sequence behavior; safety requires each committed changed row to advance, not gap-free global numbering.
- Injected failure after successful retention queue insertion or during commit acknowledgement — the supplied fault helper does not establish that particular injection point. Its queue-command failure still demonstrates rollback of earlier graph/operation/entry writes; ordinary transaction semantics are present, with no concrete incomplete-commit defect identified.
- Missing nullable metadata on the 200 success wrapper's `data` reference — considered separately from M2; this handler only returns 200 after non-null metadata validation, so I found no null-success behavior that makes that annotation independently wrong for this endpoint. Required success fields remain M2.
- Prior outside-diff UC25 query constructor naming, UC23 narrowed grant/post-parent fault fixtures, UC22 permission-outage 500 and UC21 formatting follow-ups — these are not introduced or modified by this branch; no new finding is inferred merely from their presence in the review prompt.
- Locked-restore/audit/native corpus/helper/spec tooling as independent proof of dependency or native runtime correctness — retained results provide ordinary development evidence; a version-based OSV result and local suite do not certify native OS/runtime security.
- Remote exact-head CI, approvals/rules, mergeability, issue/project closure and final integration state — delivery remains the executor's later gate; this read-only review intentionally made no GitHub calls and does not certify those states.

## Assessment

**Ready to merge? Yes — into the authorized development branch, with the listed Minor follow-ups recorded.**

The reviewed implementation has no identified Critical or Important defect in its current authorization, cascade, concurrency or rollback behavior, and the retained fresh full-suite evidence supports integration. This verdict does not waive the explicit release qualification, restore/purge/sync, shared contract or future-writer audit gates.
