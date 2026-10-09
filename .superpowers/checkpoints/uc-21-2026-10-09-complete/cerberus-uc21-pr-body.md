Owners can now change an existing encrypted record's parent with `PUT /api/records/{id}/folder`. A required nullable `folderId` assigns an owned active folder or removes the parent; `expectedRevision` preserves concurrent winners. Success returns only record ID, nullable folder ID, revision and sequence, retaining identity, ciphertext and client edit time.

Selected access may retain or narrow the record's effective owned profile **and** collection sets. An already-visible destination still returns 404 if moving there would introduce another effective profile or collection. Account-wide access can intentionally broaden scope. Inherited get/list visibility follows the new ancestry while direct typed links, current native wrappers and recipient profiles remain unchanged.

The transaction rechecks complete source/destination ancestry, session expiry, current native resulting grant evidence and held authority. It locks own account/session/selection, ordered owned collections/grants/immediate folders, then record, with no foreign account/profile locks. Changed immediate folders advance once; a matching-revision no-op advances only the record. Stale retries, changed earlier authority, exhausted counters and partial-write faults fail closed or roll back atomically. Native-visible foreign RO/RW records deny 403 before destination, stale revision or content probing; hidden targets deny 404 before native corruption.

The full suite exposed a cached **test-fixture** service JWT expiring after 60 seconds before registration tests ran. Only that shared fixture's credential lifetime becomes one hour. Production token lifetime validation and per-request identity token lifetimes are unchanged. The nine existing registration failures were observed before this correction and the complete suite was rerun.

Validation:

- Fresh unfiltered full suite: **4,235 passed, zero failed/skipped** across six families (Domain313, Shared125, Query259, Command717, Data1446, WebApi1375).
- Coverage: **98.3% line / 91.7% branch**, all six production assemblies above90% line.
- New focused move coverage:142 Data,76 Command,139 actual native HTTP cases; observed RED before implementation, then GREEN. Nine cached-service-JWT expiry failures observed before test-only fixture correction, then full suite GREEN.
- Locked restore, NuGet direct/transitive vulnerability audit,153 native OSV entries,322 native corpus cases in four directions,36 native helpers,23 Python helpers, requirements verification and OpenAPI write/drift/shape checks passed.
- Unstaged whitespace check passed; **full branch range whitespace check exits2** for one trailing-space blank line. Retained formatting Minor; no claim that the branch range is whitespace-clean.
- Single fresh ordinary whole-branch review: no Critical/Important runtime findings; three Minors retained. Exact-head branch-policy/test/docker CI is still required before merge.

Main and AF01–05 are mapped in the plan and tested at Data, Command and actual native HTTP layers. Controlled real edit/trash/move/create/profile-association/rotation races, all seven lock-class expiry checks, final statement evidence changes, analyzed one-source/one-destination plans and faults after each mutation are covered. README marks UC21 Done and M03 thirteen of 26; API/operators/changelog/OpenAPI are updated. Required exact-head branch-policy, test and docker checks gate the normal merge into develop.

Development exception and later integration obligations: independent protocol/security and real-client qualification remain explicitly deferred for **develop only**, with main/tag/release gates intact. Native signature validation does not prove client root availability or authenticated decryption; any additional opaque client provisioning needed must ship before release qualification. BEFORE ANY UC22 or later physical purge, repair terminal IDs by resource kind and prove same-GUID cross-kind survival; never null a selected profile reference. BEFORE UC35, audit implicit recipient-account FK locks. Later synchronization must publish inherited visibility changes; shared OpenAPI corrections remain release prerequisites.

Rulings I made (all decisions, including every behavior the reviewer declined; each includes its cost if wrong):

- Ruling: Purpose-built atomic source/destination transaction instead of separate preflight or generalized authority framework — current scope, immutable record and parent metadata must agree at write — cost if wrong: targeted recursive SQL duplication and related-collection contention.

- Ruling: Only current owner changes parent; native-visible foreign RO/RW403 before destination/revision/content, hidden404 before native corruption — collection write permission does not grant parent administration; all contributing native evidence still validated before visible denial — cost if wrong: bad relevant native evidence can503 even though deletion/move never allowed.

- Ruling: Both source and nonnull same-owner destination require complete current active ancestry; selected destination must already be visible through folder/ownedcollection routes — direct target visibility does not expose its parent — cost if wrong: selected users need account-wide access for hidden destinations.

- Ruling: Selected moves may narrow but resulting active owner profile ANDcollection sets must be subsets of original effective sets — preserve existing-content non-expansion invariant, including new inheritance through an already-visible folder — cost if wrong: legitimate broadening needs account-wide access and potentially extra inventory queries.

- Ruling: Preserve native ciphertext and current valid required collection recipient wrappers while recomputing resulting required set — protocol explicitly binds immutable owner/kind/resource/epoch, not folder; no new identity/epoch/grant or server-generated key — cost if wrong: native signature checks do not prove actual client root availability; any additional opaque client-supplied provisioning and authenticated client decryption must be completed before release qualification, never falsely claimed here.

- Ruling: OwnaccountNoKey→sessionUpdate→selectionNoKey→old/new/directowncollectionsGUIDNoKey→resultinggrantsGUIDShare→distinctold/newimmediatefoldersGUIDNoKey→recordNoKey — owner serialization and collection-before-content match current writers, non-key locks permit implicit FK KEY SHARE; no association restoration snapshot needed — cost if wrong: newly inserted authority can coexist, so final held-set/current-native/scope guards and actual writer races are mandatory; future writers must follow protocol.

- Ruling: Compare validated required native evidence with final statement current evidence without foreign-account/profile locks — owner pins serialize, recipient pins may rotate independently; stale evidence must not introduce inherited access — cost if wrong: legitimate concurrent recipient changes force retry503/409 and exact evidence generation adds complexity.

- Ruling: Changed parent bumps distinct old/new immediate owned folders only; preserve all direct links, other parent metadata, wrappers and client times — direct folder membership changes, inherited profile/collection scope remains dynamic — cost if wrong: later durable sync must publish inherited visibility removals/additions beyond structural revisions.

- Ruling: Same-parent/null-null matching expected revision succeeds and advances record metadata only; old-revision lost-success retry409 — consistent with existing optimistic replacement semantics, no unnecessary parent capacity required — cost if wrong: no-op still consumes revision/sequence and clients must reload after lost success.

- Ruling: Move requires valid visible native content shape and safe structural metadata, unlike damaged-content trash — a move can expose content in a different inherited scope and does not claim to repair corruption — cost if wrong: corrupt existing records need repair or trash before move, though original bytes are preserved.

- Ruling: Current identity/scope/hidden/native authority precedes stale revision, and new unheld earlier collection/grant or changed source parent conflicts instead of late lock acquisition — nonrevealing failure precedence and fixed lock order — cost if wrong: extra reload/retry and statement-snapshot linearization, not commit-time expiry.

- Ruling: Keep conservative global terminal GUID exclusions only until mandatory typed repair BEFORE UC22 ANYphysicalpurge; never null selected profile to broaden scope — this endpoint adds no purge or session changes — cost if wrong: sameGUID across kinds can falsely hide until repair; physicalpurge must remain blocked until repaired.

- Ruling: Independent protocol/security and actualclient approvals remain development-onlydeferred under explicit user instruction — ordinary native/fullsuite/review/CI and main/tag/release gates remain — cost if wrong: latent protocol/client defects await mandatory release qualification.

- Ruling: Extend only shared RegistrationApiFixture service JWT lifetime from60seconds toonehour — the growing actualHTTPsuite now reaches registration afteritscachedservicecredentialexpires; real zero-skew validation must remain unchanged — cost if wrong: fixture cannot exercise short-lived service renewal, which requires separate targeted expiry/adapter tests; no productionvalidationbypass or tokenpolicychange.

- Final: Ruling: Independent formal protocol/cryptographic/security qualification remains development-only deferred — user explicitly deferred that approval; ordinary ownership/native binding/concurrency review and all release gates remain — cost if wrong: latent protocol or security defects await mandatory release qualification.

- Final: Ruling: Real-client authenticated decryption, trusted root availability and any additional opaque client provisioning remain mandatory before release qualification — server/native fixtures cannot demonstrate external-client decryption and no usable keys are introduced — cost if wrong: clients may be unable to decrypt moved content until required provisioning is implemented and qualified; Important release limitation.

- Final: Ruling: Durable offline inherited-visibility synchronization remains later integration before release — current get/list recomputes ancestry and scope immediately; this operation does not add the separate sync protocol — cost if wrong: offline clients may retain stale visibility until synchronization integration ships; Important release limitation.

- Final: Ruling: Typed terminal-GUID repair and same-GUID/different-kind survival verification MUST precede ANY UC22 physical purge, and selected ProfileId must never be nulled — UC21 adds no purge; inherited global exclusions conservatively fail closed — cost if wrong: live cross-kind resources can be falsely hidden or selected access accidentally widened if the prerequisite is ignored; Important integration prerequisite.

- Final: Ruling: Future grant-management deadlock audit remains mandatory BEFORE UC35, including implicit recipient-account FK locks — current create/association/edit/trash/rotation interactions were reviewed and tested, but nonexistent writers cannot be certified — cost if wrong: future reciprocal sharing writers may deadlock if the established lock protocol or prerequisite is ignored.

- Final: Ruling: Cached test service-token lifetime correction does not certify production renewal behavior — nine real expiry failures justified extending only fixture lifetime while production zero-skew validation remains intact — cost if wrong: renewal defects require dedicated expiry/adapter coverage rather than this move fixture.

Deferred minors:

- Final: minor (deferred): Shared OpenAPI omits mandatory vault-access header, request body and guaranteed four-field output requiredness; runtime rejects omissions safely and actual HTTP tests pass, but generated clients may produce rejected requests or optional response models. Correct shared generator and contract checks before client/release qualification.

- Final: minor (deferred): Shared OpenAPI advertises ProblemDetails content for an actually empty413; runtime correctly rejects oversized input but generated clients may attempt invalid deserialization. Correct shared response generator before client/release qualification.

- Final: minor (deferred): RecordMoveHttpTests.cs:143 has four trailing spaces on an added blank line; full branch diff --check exits2. No runtime impact; retain for authorized formatting cleanup and report actual verification result, never blanket diffPASS.

Closes #22.
