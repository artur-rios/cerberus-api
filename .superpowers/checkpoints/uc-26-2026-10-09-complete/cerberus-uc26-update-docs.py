from pathlib import Path
w=Path('/tmp/cerberus-uc26-worktree');p=w/'docs/security/folder-api.md';s=p.read_text();assert '## Update encrypted folder content (UC26)' not in s
s+='''
## Update encrypted folder content (UC26)

`PUT /api/folders/{id}` replaces one currently writable encrypted folder. Send current Heimdall authentication, one `X-Cerberus-Vault-Access` header, a canonical lowercase dashed nonzero UUID and bounded UTF8 JSON. No query parameters are accepted. The body has exactly three required fields: `expectedRevision`, `envelope`, `editedAt`. Unknown, duplicate or case-forged properties and numeric strings are rejected. Parent, ownership, profiles, collections and descendant content are preserved; organization changes use their own operations. All responses are no-store.

The replacement must use a supported native envelope and retain the stored key epoch. When ciphertext changes, both key salt and nonce must be fresh. An identical envelope can update the client edit time. Expected revision is a positive safe integer; UTC edit time must be representable at microsecond precision and is floored before storage. An older or equal client time is accepted when the revision matches; revision checks determine concurrency, rather than timestamp ordering.

Success is 200 `folder_updated`, with exactly `folderId`, `revision`, `serverSequence`, `editedAt`. Only target envelope/time/revision/sequence/concurrency metadata advance; parent, children, records, profiles, collections, membership, ownership and access state remain unchanged. The sequence advances for later synchronization; durable ordered offline visibility delivery still requires the synchronization use cases.

Owners may edit current active owned folders within their current selection. Native read/write recipients may edit current collection-folder members and descendants within selected collection scope. Read-only recipients receive 403. Record-only grants or selected profile-record links never authorize a folder edit; member-leaf scope cannot authorize an otherwise unshared parent or sibling. Every relevant overlapping foreign contributor must pass native binding, even alongside a valid write route. Unselected/unrelated corrupt evidence is ignored; corrupt contributing evidence fails the whole operation.

The transaction locks own account/session/selection, eligible collections/grants in public-identifier order and only the target folder. Complete current same-owner ancestry and write authority are recomputed in the guarded update after waits. Expiry, removal, permission downgrade, ancestor changes and protection rotation cannot silently authorize a stale write. Newly appearing unheld native authority requires reload/retry. No foreign account/profile or ancestor lock chain is acquired. Failed writes and post-write verification faults roll back row changes; global sequence gaps may remain.

Malformed input/envelope/context is 400, missing identity/access 401, current denied access or visible read-only scope 403, hidden/missing/terminal resources 404. A stale or exhausted revision, competing winning state, new unheld authority or exhausted global sequence is 409 `revision_conflict`; reload the current permitted detail before retrying. Lost-success retries cannot overwrite the winning edit. Necessary persisted/native/ancestry corruption or dependency failure is 503 `persistence_unavailable`, without partial output or private exception details. Common oversized transport returns actual empty413. Shared OpenAPI metadata findings and independent security/protocol/client qualification remain release follow-ups.
''';p.write_text(s)
p=w/'README.md';s=p.read_text();lines=s.splitlines();matches=[i for i,x in enumerate(lines) if 'UC-26 — Update folder' in x];assert len(matches)==1 and lines[matches[0]].endswith('Todo |');lines[matches[0]]=lines[matches[0]][:-len('Todo |')]+'Done |';s='\n'.join(lines)+'\n';assert '17 / 26 closed' in s;s=s.replace('17 / 26 closed','18 / 26 closed');p.write_text(s)
p=w/'CHANGELOG.md';s=p.read_text();needle='### Added\n';assert needle in s;s=s.replace(needle,needle+'\n- Encrypted folder content updates (UC-26), with current owner/native read-write\n  scope, revision conflict protection, guarded permission revalidation and rollback.\n  Folder organization and descendant content stay unchanged.\n  See the [folder update API](docs/security/folder-api.md#update-encrypted-folder-content-uc26).\n',1);p.write_text(s)
p=w/'docs/requirements/Operations & Infrastructure Document.md';s=p.read_text();s+='''
### Folder content update (UC26)

PUT `/api/folders/{id}` uses current identity/vault access and strict replacement
JSON with expected revision. Own account/session/selection, ordered eligible
collections/grants and target-folder locks precede a final current recursive
write-authority guard. No foreign account/profile or ancestor lock chain is taken.
Read-only scope denies403, hidden targets404, stale/new unheld authority409 and
necessary corruption/dependency failures503. Post-write faults roll back row changes;
sequence gaps are allowed. Only target content and metadata change; organization,
children, records and associations remain unchanged. Record-only authority never
permits a folder edit. See [folder update API](../security/folder-api.md#update-encrypted-folder-content-uc26).
Shared OpenAPI metadata, durable synchronization and independent formal/client
qualification remain mandatory release follow-ups.
''';p.write_text(s)
p=w/'docs/operations/foundation-status.md';s=p.read_text();s+='''
UC26 adds [encrypted folder content replacement](../security/folder-api.md#update-encrypted-folder-content-uc26)
for current owner/native read-write scope. Expected revision and a final current
permission guard preserve winning edits; read-only recipients cannot write.
Only target ciphertext/metadata advance, with no parent/child/association changes.
Formal security/client and durable offline release qualification remain outstanding.
''';p.write_text(s)
print('UC26 update docs and intended merged README18of26 written; generated contract/full verification remain.')
