Owners can now change an existing encrypted record's parent with `PUT /api/records/{id}/folder`. A required nullable `folderId` assigns an owned active folder or removes the parent; `expectedRevision` preserves concurrent winners. Success returns only record ID, nullable folder ID, revision and sequence, retaining identity, ciphertext and client edit time.

Selected access may retain or narrow the record's effective owned profile **and** collection sets. An already-visible destination still returns404 if moving there would introduce another effective profile or collection. Account-wide access can intentionally broaden scope. Inherited get/list visibility follows the new ancestry while direct typed links, current native wrappers and recipient profiles remain unchanged.

The transaction rechecks complete source/destination ancestry, session expiry, current native resulting grant evidence and held authority. It locks own account/session/selection, ordered owned collections/grants/immediate folders, then record, with no foreign account/profile locks. Changed immediate folders advance once; a matching-revision no-op advances only the record. Stale retries, changed earlier authority, exhausted counters and partial-write faults fail closed or roll back atomically. Native-visible foreign RO/RW records deny403 before destination, stale revision or content probing; hidden targets deny404 before native corruption.

The full suite exposed a cached **test-fixture** service JWT expiring after60seconds before registration tests ran. Only that shared fixture's credential lifetime becomes one hour. Production token lifetime validation and per-request identity token lifetimes are unchanged. The nine existing registration failures were observed before this correction and the complete suite was rerun.

Validation:

{{VALIDATION}}

Main and AF01–05 are mapped in the plan and tested at Data, Command and actual native HTTP layers. Controlled real edit/trash/move/create/profile-association/rotation races, all seven lock-class expiry checks, final statement evidence changes, analyzed one-source/one-destination plans and faults after each mutation are covered. README marks UC21 Done and M03 thirteen of26; API/operators/changelog/OpenAPI are updated. Required exact-head branch-policy, test and docker checks gate the normal merge into develop.

Development exception and later integration obligations: independent protocol/security and real-client qualification remain explicitly deferred for **develop only**, with main/tag/release gates intact. Native signature validation does not prove client root availability or authenticated decryption; any additional opaque client provisioning needed must ship before release qualification. BEFORE ANY UC22 or later physical purge, repair terminal IDs by resource kind and prove same-GUID cross-kind survival; never null a selected profile reference. BEFORE UC35, audit implicit recipient-account FK locks. Later synchronization must publish inherited visibility changes; shared OpenAPI corrections remain release prerequisites.

Rulings I made (all decisions, including every behavior the reviewer declined; each includes its cost if wrong):

{{RULINGS}}

Deferred minors:

{{MINORS}}

Closes #22.
