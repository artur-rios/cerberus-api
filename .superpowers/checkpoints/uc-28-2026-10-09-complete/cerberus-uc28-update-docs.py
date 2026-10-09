from pathlib import Path
w=Path('/tmp/cerberus-uc28-worktree')
p=w/'docs/security/folder-api.md';s=p.read_text();assert '## Move a folder (UC28)' not in s
s+='''
## Move a folder (UC28)

`PUT /api/folders/{id}/parent` changes a currently visible owned folder's parent. Send current Heimdall authentication, one `X-Cerberus-Vault-Access` header, a canonical lowercase dashed nonzero folder UUID and bounded UTF8 JSON with exactly two required fields:

```json
{"expectedRevision": 3, "parentFolderId": null}
```

Explicit null moves the folder to the account root. A nonnull parent must be an independently visible active folder belonging to the same account. Omitting `parentFolderId` is invalid. No query, envelope, edit time, association, owner or cascade override is accepted. All responses are no-store.

Success is 200 `folder_moved` with exactly `folderId`, `parentFolderId`, `revision`, `serverSequence`; the nullable parent is always present. The root revision advances once. Each distinct changed immediate old/new parent advances structural metadata once. Same-parent and null-to-null requests advance only the root and skip unchanged-parent counter validation. Ciphertext, client edit times, identity, ownership, descendant metadata, direct associations, profiles, collections, grant envelopes, protection and recipient metadata remain unchanged. Independently trashed or typed terminal branches keep their existing operations and deadlines.

Selected access must admit both source and destination without being widened to account-wide access. The store computes effective active profile and collection sets separately for every active affected folder and contained record. Each resulting set must be a subset of that same member's prior set. A visible destination that introduces another profile or collection returns404; use current account-wide access when intentional expansion is required. Existing direct and nested routes remain. Native read-only and read/write recipients cannot move someone else's folder: a valid visible native folder route returns403 after every relevant binding is verified. Record-only routes never expose a containing folder. Hidden resources return404 before stale revisions or ciphertext are inspected.

Moving a folder changes inherited current GET/list access to descendants. Every active recipient grant on every resulting effective collection must have a valid current native envelope and exact owner/collection/grant/recipient/epoch/revision/pin binding, including collections reached only by a contained record. Missing or malformed persisted bindings return503 without mutation. The server retains opaque envelopes and cannot create usable keys or certify client decryption. External provisioning and authenticated client decryption remain release qualification requirements. Old-only inherited recipient routes disappear; remaining direct shares and new valid inherited routes remain available. Recipient-held copies cannot be retracted. UC48 must provide durable inherited visibility events and offline removal delivery.

Self or visible descendant destinations return400 `validation_failed`, hidden/missing/trashed/terminal destinations404 `not_found`, and relevant active stored cycles503 `persistence_unavailable`. Missing identity/access is401; denied current access is403 `vault_access_denied`. A stale root, changed captured inventory or native evidence, or exhausted changed revision/sequence is409 `revision_conflict`. A lost-success retry with the old revision conflicts rather than applying another move. Reload current permitted metadata before retrying. Structural or dependency corruption is503 without partial output; oversized requests receive the common empty413.

The transaction locks the own account/session, selected profile, ordered current/result collections and native grants before the captured folders/records/direct links. It acquires no foreign account/profile or upward ancestor lock chain. Authority is rechecked after waits, and the final guarded root statement compares the complete current inventory and native evidence. That statement linearizes authorization; expiry after it does not cancel an already authorized atomic move. Root and changed-parent writes commit once or roll back together; global sequence gaps are permitted. Current owner create/move/trash/association/rotation and native recipient content edits are exercised in controlled races. The separate implicit recipient-account FK audit remains required before UC35 adds grant creation. Shared OpenAPI requiredness/empty413 metadata and independent security/protocol/client qualification remain release follow-ups.
''';p.write_text(s)
p=w/'README.md';s=p.read_text();old=next(x for x in s.splitlines() if 'UC-28 — Move folder' in x);assert old.endswith('Todo |');s=s.replace(old,old.removesuffix('Todo |')+'Done |');assert '19 / 26 closed' in s;s=s.replace('19 / 26 closed','20 / 26 closed');p.write_text(s)
p=w/'CHANGELOG.md';s=p.read_text();anchor='- Recoverable owned folder deletion (UC-27),';assert anchor in s;s=s.replace(anchor,'''- Owned folder moves (UC-28) with atomic root/immediate-parent metadata,
  per-member selected scope checks, cycle rejection and current native recipient
  bindings. Encrypted subtree content and direct links remain unchanged; current
  GET/list inheritance changes immediately. Durable synchronization remains later
  work. See the [folder move API](docs/security/folder-api.md#move-a-folder-uc28).

'''+anchor);p.write_text(s)
p=w/'docs/operations/foundation-status.md';s=p.read_text();s+='''
UC28 adds [owned folder parent moves](../security/folder-api.md#move-a-folder-uc28).
Every active affected folder/record retains its direct associations and opaque
content while selected access rejects new effective profile/collection scope.
Current result-native grants, including contained direct records, require exact
fresh bindings; the server does not fabricate keys or certify client decryption.
Only root and distinct changed immediate parents advance metadata. Independent
trash/deadlines stay unchanged. Current GET/list inheritance recalculates, while
UC48 durable inherited visibility and independent client qualification remain
release obligations. Collection-before-content locking aligns existing recipient
editors; the implicit recipient-account FK audit remains mandatory before UC35.
''';p.write_text(s)
p=w/'docs/requirements/Operations & Infrastructure Document.md';s=p.read_text();s+='''
### Owned folder parent moves (UC28)

PUT `/api/folders/{id}/parent` requires `expectedRevision` and explicitly nullable
`parentFolderId`. Current owned source and destination folder authority is checked
without broadening selection; native RO/RW foreign sources return403 after current
binding validation, and record-only grants do not admit a container. Visible
self/descendant destinations400, hidden targets404 and relevant stored cycles503.

Capture active owned subtree folders/records, typed direct links, each member's
before/result effective profile/collection sets and current native result evidence.
Selected access requires both result sets to be subsets for each member; account-wide
access can expand intentionally. Every active resulting recipient envelope binds
current owner/recipient pins, identity, collection epoch and grant revision. Missing
persisted evidence503; changed or uncaptured valid evidence409. No usable server key
or client decryption claim is introduced.

Own account UPDATE/session UPDATE, selection NO KEY UPDATE, ordered own collections
NO KEY UPDATE and result grants SHARE precede captured folders/records/links UPDATE.
Current authority is checked after each wait. Complete inventory/native evidence is
recomputed inside the guarded root statement, the authorization linearization point.
Root parent/revision/sequence/stamp and distinct changed immediate parents commit
atomically or roll back completely. Descendant metadata, ciphertext/client times,
keys, direct associations, recipient metadata and independent trash operations and
deadlines remain unchanged. Same-parent moves change only root; lost-success retry409.
Global sequence gaps are allowed. No foreign account/profile or upward-chain lock.
Current create/move/trash/association/rotation and native recipient edit races are
covered; before UC35 audit implicit recipient-account FK locks separately.

Current GET/list inheritance updates immediately. UC48 must add durable inherited
visibility and recipient offline removal delivery; recipients can retain prior copies.
External key provisioning/decryption, shared OpenAPI metadata and independent
security/protocol/client approval remain release gates. See the
[folder move API](../security/folder-api.md#move-a-folder-uc28).
''';p.write_text(s)
print('UC28 docs and README Done/M03 20 of26 updated with current sharing/rollback and explicit release limits.')
