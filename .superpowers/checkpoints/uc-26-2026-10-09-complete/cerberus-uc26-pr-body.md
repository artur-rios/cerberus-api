Folder content replacement now accepts a strict encrypted envelope, edit time and expected revision. `PUT /api/folders/{id}` permits current owners and native read/write recipients in current folder scope, returning exactly folderId/revision/serverSequence/editedAt. Read-only access is denied; record-only grants cannot authorize folder edits. Parent, children, records, profiles, collections, membership and ownership stay unchanged.

The transaction reloads current authority after lock waits and embeds complete current folder ancestry/permission in the guarded write. Every eligible overlapping foreign contributor is native-verified; new unheld authority and competing edits require reload/retry. Post-write faults roll back row changes. Locks follow existing cross-owner order and never form a foreign account/profile or upward folder chain. Key epoch is preserved; changed ciphertext requires fresh salt and nonce. No schema, dependency or cryptographic algorithm change.

Closes #27.

## Validation

- Fresh unfiltered suite: **5,780 passed, zero failed/skipped**: Domain361, Shared125, Query411, Command912, Data2098, WebApi1873.
- Actual production coverage: **98.4% line / 92% aggregate branch**, all six assemblies at least 90% line.
- Focused Command71, real PostgreSQL127 and native HTTP108 passed. Genuine missing-type/store RED and 99 missing-route HTTP failures precede implementation; syntax/installation mistakes and later confidence-only additions are distinguished in the ledger.
- Locked restore, NuGet direct/transitive audit, OSV153/no findings, native corpus322 in four directions, native helpers36, Python helpers23, specs and OpenAPI write/drift/exact-shape passed.
- Complete branch-range whitespace passed including all added files.
- ONE fresh whole-branch review found no blockers; all 17 declined judgments have final reason/cost rulings and both Minors are retained below.
- Exact-head branch-policy/test/docker CI must pass before normal develop merge.

## Rulings I made

- Ruling: Reuse freshly verified merged UC25 baseline — merged tree must equal tested36f93bc with5474 unfiltered passes and exact-head CI; new checkout starts at that exact normal merge — cost if wrong: environmental differences must be caught by whole-family and final fresh six-family tests.

- Ruling: Update body has three fields and response four metadata fields — mirrors established record replacement and folder creation metadata; no new challenge required beyond current vault access — cost if wrong: clients must reload encrypted content separately and independent client/protocol qualification remains before release.

- Ruling: Content replacement never changes parent, links, ownership or descendant metadata — UC28 handles movement and association writers handle organization; same envelope may update client time — cost if wrong: clients need separate operations and unchanged ancestors do not signal content edits through their own metadata.

- Ruling: Folder authority uses only actual PF/CF target-or-ancestor membership, never PR/CR — record-only access cannot grant container edits; current read-only visibility denies403 after necessary native checks — cost if wrong: record-only clients cannot edit container metadata and corrupt relevant RO evidence can yield503.

- Ruling: Lock own account NO KEY UPDATE/session/selection, ordered collections/grants SHARE then only target folder NO KEY UPDATE — reuse record update cross-owner order without foreign account/profile/upward locks; actual creation target wait is legitimate — cost if wrong: a future writer with incompatible order can deadlock and must pass its own lock audit, including implicit recipient FK audit beforeUC35.

- Ruling: Verify every eligible foreign contributor with one owner/recipient pin parse — valid RW scope does not excuse corrupt overlapping RO/native routes; unselected/unrelated excluded — cost if wrong: one bad contributor denies whole update503 and overlaps add signature work.

- Ruling: Final UPDATE recomputes full current authority and requires expected revision, original envelope/sequence and held verified contributor subsets — lock waits and ancestry changes cannot authorize stale edits; growth409 requires reload — cost if wrong: callers may need retry after harmless new authority rather than accepting an unverified grant.

- Ruling: Synchronization change uses existing monotonic target serverSequence foundation; durable offline delivery remains UC48 gate — existing content writers follow this model and update does not implement the later synchronization protocol — cost if wrong: offline clients remain incomplete until durable visibility integration is implemented before release.

- Ruling: Hidden/incomplete target ancestry404 precedes native/content/stale checks; relevant active cycle503 — preserve nonrevealing current folder scope; normal FK fixtures do not claim broken-FK corruption coverage — cost if wrong: corrupt visible hierarchy needs repair and defensive incomplete-ancestry regression confidence is narrower without integrity-bypass fixture.

- Ruling: Independent formal security/protocol and real external-client checks remain deferred only for develop — explicit user instruction permits development batch while ordinary tests/native checks/ONEreview/exactCI and release gates remain — cost if wrong: latent security/client defects still require independent qualification before release.

- Final: Ruling: Formal security/protocol/crypto approval remains development-only deferred — explicit owner instruction permits develop but keeps release qualification — cost if wrong: latent security defects require independent qualification before release.

- Final: Ruling: External-client provisioning/decryption/interoperability remains a release gate — native fixtures assess the server contract without certifying a real client — cost if wrong: clients may lack keys or fail decryption.

- Final: Ruling: Remote baseline provenance and exact-head CI/rules/mergeability/issue/project completion remain executor delivery gates — sole reviewer performed no remote actions and executor must verify these before completion — cost if wrong: premature delivery can integrate unverified work.

- Final: Ruling: Durable ordered offline/inherited visibility events remain UC48 work — target sequence is the accepted existing foundation — cost if wrong: offline clients can retain stale visibility until synchronization is qualified.

- Final: Ruling: Organization propagation and returning ciphertext remain excluded — content-only contract changes exactly target content metadata; detail reload and separate operations handle navigation — cost if wrong: clients need additional detail/organization requests.

- Final: Ruling: No new signed replacement challenge is introduced — current identity/vault-access follows existing content writers; independent protocol qualification remains — cost if wrong: an independently discovered protocol weakness requires correction before release.

- Final: Ruling: Future move/trash/restore/purge/physical-erasure writers must pass concurrency/lifecycle gates — no such writer is introduced in this branch — cost if wrong: future writers can violate retention or lock ordering.

- Final: Ruling: Implicit recipient-account FK lock audit remains mandatory BEFORE UC35 — current reciprocal edits/rotation do not qualify future grant insertion/deletion — cost if wrong: future sharing can deadlock with older account FOR UPDATE writers.

- Final: Ruling: Integrity-bypass broken-FK/cross-owner ancestry evidence remains source/schema only — same-owner recursion and required null-parent terminus fail closed; no deliberate broken-FK fixture exists — cost if wrong: defensive regression confidence remains narrower until a dedicated fixture.

- Final: Ruling: Production workload/latency certification remains before release — one candidate/three ancestry rows is a bounded plan check; eligible collection/grant locks can exceed contributors — cost if wrong: growing inventories or signature work can increase contention and latency.

- Final: Ruling: Arbitrary out-of-band writes after the final snapshot are not absolutely serialized — current guarantee is final-statement authority plus compatible existing writer locks/guards — cost if wrong: future lifecycle writers can break the boundary unless separately audited.

- Final: Ruling: Historical UC25 query constructor naming Minor remains deferred — unchanged outside this diff; current UpdateFolder context uses folderId correctly — cost if wrong: misleading historical named argument remains maintenance debt.

- Final: Ruling: Historical UC23 narrowed fixtures remain before-release follow-ups — current real creation contention/record-only scope evidence does not rewrite those tests — cost if wrong: historical regression confidence remains narrower.

- Final: Ruling: Historical UC22 permission-outage 500 Minor remains deferred — unchanged outside this diff; current UC26 dependency mapping is assessed — cost if wrong: operators may receive misleading deletion outage classification.

- Final: Ruling: Historical UC21 formatting blank remains deferred — unchanged outside this diff with no runtime effect — cost if wrong: formatting debt remains.

- Final: Ruling: Sixteen Data/four HTTP confidence additions have no independent RED claim — ledger separates later confidence coverage from original genuine contract/store/route RED — cost if wrong: these additions alone do not establish test-first implementation history.

- Final: Ruling: Coverage gate is six-assembly 90% line, not 90% branch or Python example percentages — actual production Summary and six family logs establish 98.4 line/92 aggregate branch — cost if wrong: misreading fixture percentages or branch requirements would misstate evidence.

## Deferred minors

- Final: minor (deferred): Shared OpenAPI lacks required vault-access header/body/success-field flags and wrapper failure/null distinction, while all three inner inputs are required and runtime enforces the exact four-field success. Correct shared generation before external-client release. Cost: generated clients can omit input locally or mishandle optional/error metadata; authorization and persistence remain safe.

- Final: minor (deferred): Shared OpenAPI advertises ProblemDetails for actual empty 413. Correct shared metadata before client release. Cost: generated decoders may try to deserialize an absent body; bounded rejection remains correct.

Independent formal security/protocol and external-client qualification remain development-only deferred under the owner's instruction; release gates remain mandatory. Monotonic target sequence is the synchronization foundation; durable offline visibility delivery is still a later release obligation.
