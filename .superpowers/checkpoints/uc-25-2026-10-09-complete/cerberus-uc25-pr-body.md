Folder detail now returns one currently permitted encrypted folder and only visible organization references. `GET /api/folders/{id}` requires current identity and vault access, a canonical public ID and no query or GET body. Success contains exactly five encrypted metadata fields plus direct profile IDs, effective collection IDs and an independently permitted immediate parent ID.

Owned and native RO/RW recipient scope includes selected direct/member folders and descendants. Record-only access cannot reveal the folder; a target-only association or member-leaf grant cannot reveal an unshared parent. One target-first SQL statement captures current permissions, complete active ancestry, references and every contributing native grant, with pins parsed once per party. Hidden targets stay nonrevealing; required corruption fails without a partial payload. Reads make no locks or mutations. No schema, dependency or cryptographic algorithm changes.

Closes #26.

## Validation

- Fresh unfiltered suite: **5,474 passed, zero failed/skipped** across Domain 361, Shared 125, Query 411, Data 1,971, Command 841, WebApi 1,765.
- Actual production coverage: **98.4% line / 92.1% branch**; all six assemblies at least 90% line.
- Focused Query 55, PostgreSQL 127 and native HTTP 83 passed. Missing-contract/store RED and 82 missing-GET HTTP failures precede implementation; the temporary installer assertion correction is excluded from product RED evidence.
- Locked restore, NuGet direct/transitive audit, native OSV 153 packages/no findings, native corpus 322 cases in four directions, 36 native helpers, 23 Python helpers, specs and OpenAPI write/drift/shape passed.
- Complete branch-range whitespace passed including all added files.
- One fresh whole-branch review found no blockers. All 16 declined judgments and the narrower source/schema-only incomplete-ancestry evidence have explicit final rulings; all three Minors retained below.
- Exact-head branch-policy/test/docker CI must pass before normal develop merge.

## Rulings I made

- Ruling: Reuse the freshly verified merged UC24 baseline — actual merged tree equals tested HEAD with5209 unfiltered passes and exact-head CI; this worktree begins at that exact merge — cost if wrong: environmental differences must be caught by whole-family and final fresh six-family tests.

- Ruling: Folder detail uses eight fields mirroring existing record detail, without child inventories — five encrypted metadata fields plus current profile/parent/collection references meet the detail contract while existing list/detail endpoints provide inventories — cost if wrong: navigation needs additional permitted list/detail requests rather than one unbounded child payload.

- Ruling: Only current direct owned profile references and effective permitted collection references are returned — inherited folder visibility does not invent a direct profile association and foreign grants never expose owner profiles — cost if wrong: clients must distinguish direct organization from inherited visibility.

- Ruling: Immediate parent is returned only through independently permitted ancestor routes excluding the target itself — a direct target link/member-leaf grant cannot disclose otherwise unshared ancestry — cost if wrong: clients cannot reconstruct private hierarchy above their permitted leaf.

- Ruling: ProfileRecord and CollectionRecord routes never expose folders — folder authority requires actual ProfileFolder or CollectionFolder routes, even if native record access succeeds — cost if wrong: record-only clients must navigate records without their container metadata.

- Ruling: Validate every eligible foreign target contributor with owner/recipient pins parsed once per party — overlapping valid authority does not excuse a corrupt relevant binding; unselected/unrelated grants are excluded before verification — cost if wrong: one corrupt contributing route yields whole-response503 and large overlap entails signature work.

- Ruling: Hidden/incomplete ancestry returns404 before native/content inspection; fully active actually scoped cycles503 — preserve nonrevealing hidden-target behavior while failing safe on needed structural corruption — cost if wrong: corrupt visible hierarchies require repair and incomplete structures cannot be navigated.

- Ruling: Independent formal security/protocol and real external-client approval remain deferred only for develop — explicit user authorization permits this development batch while ordinary native/ownership/snapshot checks, ONE review and release/main/tag gates remain — cost if wrong: latent security or client integration defects still require independent qualification before release.

- Final: Ruling: Independent formal security/protocol/cryptographic approval remains development-only deferred — explicit user instruction permits develop while ordinary ownership/native/output/snapshot checks remain required — cost if wrong: latent defects require independent qualification before production.

- Final: Ruling: Real external-client key provisioning/decryption/end-to-end operability remains mandatory release qualification — native server fixtures do not independently qualify an external client — cost if wrong: clients may lack keys or fail decryption despite server fixtures passing.

- Final: Ruling: Main/tag/release/deployment readiness remains outside this develop verdict — batch authorization targets develop and preserves release gates — cost if wrong: premature release exposes unqualified behavior.

- Final: Ruling: Durable ordered offline inherited-visibility delivery remains later synchronization work — a current GET snapshot cannot certify durable synchronization — cost if wrong: offline clients can retain stale visibility until synchronization is implemented.

- Final: Ruling: Future folder update/trash/move/restore/purge writers require their own gates — direct database mutations prove current read re-evaluation only — cost if wrong: future writers can violate scope, lifecycle or lock ordering if their gates are skipped.

- Final: Ruling: Other physical erasure paths and implicit recipient-account FK audit BEFORE UC35 remain mandatory later gates — this read endpoint changes neither physical handlers nor sharing writers — cost if wrong: retained ciphertext or future sharing deadlocks remain possible until those gates.

- Final: Ruling: Production workload/load qualification remains before release — bounded target/ancestry EXPLAIN checks are narrower than unspecified production throughput targets — cost if wrong: overlapping contributor signature work can affect latency and availability.

- Final: Ruling: Historical UC24 provenance remains executor-verified rather than independently re-certified — review was read-only while fresh UC25 full-suite evidence was inspected — cost if wrong: incorrect historical/environment assumptions need separate diagnosis.

- Final: Ruling: Executor must verify exact-head CI/rules/mergeability, normal merge, issue/project closure and archive cleanup — these are post-review delivery gates excluded from reviewer actions — cost if wrong: premature completion can leave unverified or unintegrated work.

- Final: Ruling: Inherited UC23 narrowed grant/post-parent-failure fixtures retain recorded follow-ups — unchanged fixtures are outside this diff and UC25 real membership/native fixtures were reviewed — cost if wrong: UC23 regression confidence remains narrower until fixture correction.

- Final: Ruling: Inherited UC22 permission-outage 500 classification retains its deferred release Minor — unchanged deletion mapping is outside this diff and UC25 necessary dependencies have tested 503 mapping — cost if wrong: operators can receive misleading deletion outage classification.

- Final: Ruling: Inherited UC21 formatting blank retains its recorded Minor — unchanged line has no UC25 runtime effect and this complete branch range passes whitespace checks — cost if wrong: formatting debt remains.

- Final: Ruling: Child inventories remain excluded from the eight-field detail payload — binding ruling uses permitted list/detail calls for navigation — cost if wrong: clients incur extra navigation requests.

- Final: Ruling: Private ancestry and inherited-as-direct profile links remain excluded — independent parent authority and direct organization protect private owner metadata — cost if wrong: clients must tolerate null parent and distinguish inherited visibility.

- Final: Ruling: Record-only authority never expands into folder navigation — binding folder authority uses actual folder membership only — cost if wrong: record-only clients navigate without private container metadata.

- Final: Ruling: Corrupt eligible overlapping contributors deliberately cause whole-response 503 — every contributor must pass native validation under ruling 6 — cost if wrong: repair is required and overlapping routes add signature work.

- Final: Ruling: Incomplete/cross-owner ancestry defensive coverage is source/schema assessed; a future corruption fixture may bypass integrity constraints explicitly — same-owner recursion requires a null-parent terminus and normal FK constraints prevent these invalid states, but no dedicated broken-FK integration fixture exists — cost if wrong: regression confidence in this defensive guard remains narrower until a dedicated fixture.

## Deferred minors

- Final: minor (deferred): Shared OpenAPI omits required vault-access header/output metadata and advertises nullable profileIds/collectionIds although runtime requires access and always returns all eight fields with nonnull arrays. Correct shared generation before external-client release. Cost: clients can omit access and receive 401 or unnecessarily model missing/null fields; runtime authorization/output are correct.

- Final: minor (deferred): Shared OpenAPI advertises ProblemDetails for actual empty 413. Correct shared metadata before client release. Cost: generated decoders may attempt an absent body; bounded rejection remains correct.

- Final: minor (deferred): GetFolderQuery constructor parameter recordId populates FolderId. Rename to folderId in later maintenance. Cost: named callers/maintainers see a misleading name; current positional calls and HTTP behavior are correct.

Independent formal security/protocol and external-client qualification remain development-only deferred under the owner instruction; release gates remain mandatory.
