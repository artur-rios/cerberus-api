Ruling: Purpose-built atomic source/destination transaction instead of separate preflight or generalized authority framework — current scope, immutable record and parent metadata must agree at write — cost if wrong: targeted recursive SQL duplication and related-collection contention.

Ruling: Only current owner changes parent; native-visible foreign RO/RW403 before destination/revision/content, hidden404 before native corruption — collection write permission does not grant parent administration; all contributing native evidence still validated before visible denial — cost if wrong: bad relevant native evidence can503 even though deletion/move never allowed.

Ruling: Both source and nonnull same-owner destination require complete current active ancestry; selected destination must already be visible through folder/ownedcollection routes — direct target visibility does not expose its parent — cost if wrong: selected users need account-wide access for hidden destinations.

Ruling: Selected moves may narrow but resulting active owner profile ANDcollection sets must be subsets of original effective sets — preserve existing-content non-expansion invariant, including new inheritance through an already-visible folder — cost if wrong: legitimate broadening needs account-wide access and potentially extra inventory queries.

Ruling: Preserve native ciphertext and current valid required collection recipient wrappers while recomputing resulting required set — protocol explicitly binds immutable owner/kind/resource/epoch, not folder; no new identity/epoch/grant or server-generated key — cost if wrong: native signature checks do not prove actual client root availability; any additional opaque client-supplied provisioning and authenticated client decryption must be completed before release qualification, never falsely claimed here.

Ruling: OwnaccountNoKey→sessionUpdate→selectionNoKey→old/new/directowncollectionsGUIDNoKey→resultinggrantsGUIDShare→distinctold/newimmediatefoldersGUIDNoKey→recordNoKey — owner serialization and collection-before-content match current writers, non-key locks permit implicit FK KEY SHARE; no association restoration snapshot needed — cost if wrong: newly inserted authority can coexist, so final held-set/current-native/scope guards and actual writer races are mandatory; future writers must follow protocol.

Ruling: Compare validated required native evidence with final statement current evidence without foreign-account/profile locks — owner pins serialize, recipient pins may rotate independently; stale evidence must not introduce inherited access — cost if wrong: legitimate concurrent recipient changes force retry503/409 and exact evidence generation adds complexity.

Ruling: Changed parent bumps distinct old/new immediate owned folders only; preserve all direct links, other parent metadata, wrappers and client times — direct folder membership changes, inherited profile/collection scope remains dynamic — cost if wrong: later durable sync must publish inherited visibility removals/additions beyond structural revisions.

Ruling: Same-parent/null-null matching expected revision succeeds and advances record metadata only; old-revision lost-success retry409 — consistent with existing optimistic replacement semantics, no unnecessary parent capacity required — cost if wrong: no-op still consumes revision/sequence and clients must reload after lost success.

Ruling: Move requires valid visible native content shape and safe structural metadata, unlike damaged-content trash — a move can expose content in a different inherited scope and does not claim to repair corruption — cost if wrong: corrupt existing records need repair or trash before move, though original bytes are preserved.

Ruling: Current identity/scope/hidden/native authority precedes stale revision, and new unheld earlier collection/grant or changed source parent conflicts instead of late lock acquisition — nonrevealing failure precedence and fixed lock order — cost if wrong: extra reload/retry and statement-snapshot linearization, not commit-time expiry.

Ruling: Keep conservative global terminal GUID exclusions only until mandatory typed repair BEFORE UC22 ANYphysicalpurge; never null selected profile to broaden scope — this endpoint adds no purge or session changes — cost if wrong: sameGUID across kinds can falsely hide until repair; physicalpurge must remain blocked until repaired.

Ruling: Independent protocol/security and actualclient approvals remain development-onlydeferred under explicit user instruction — ordinary native/fullsuite/review/CI and main/tag/release gates remain — cost if wrong: latent protocol/client defects await mandatory release qualification.

Ruling: Extend only shared RegistrationApiFixture service JWT lifetime from60seconds toonehour — the growing actualHTTPsuite now reaches registration afteritscachedservicecredentialexpires; real zero-skew validation must remain unchanged — cost if wrong: fixture cannot exercise short-lived service renewal, which requires separate targeted expiry/adapter tests; no productionvalidationbypass or tokenpolicychange.
