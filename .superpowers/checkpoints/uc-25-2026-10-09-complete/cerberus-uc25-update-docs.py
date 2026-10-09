from pathlib import Path
w=Path('/tmp/cerberus-uc25-worktree')
p=w/'docs/security/folder-api.md';s=p.read_text();assert '## Get an encrypted folder (UC25)' not in s
s+='''
## Get an encrypted folder (UC25)

`GET /api/folders/{id}` returns one currently permitted encrypted folder. Send the current Heimdall bearer token and one `X-Cerberus-Vault-Access` header. The path uses a canonical lowercase dashed nonzero UUID. No query parameters or GET body are accepted, including unknown-length or chunked bodies. Oversized bodies receive the common empty 413. All responses are no-store.

Success is 200 with `folder_found`. The data contains exactly `folderId`, `revision`, `serverSequence`, `editedAt`, `envelope`, `profileIds`, `parentFolderId` and `collectionIds`. Profile and collection arrays are nonnull, unique, sorted public identifiers; the immediate parent is nullable. The five encrypted content fields follow the list contract. No owner/internal/grant identifiers, key wrappers or plaintext are returned. Child inventories remain available through permitted list/detail calls.

Account-wide owners can read active owned folders. Selected access admits direct profile-folder links and descendants, or member folders and descendants of a currently selected owned or granted collection. Read-only and read/write recipients see only current native collection-folder members and descendants. A record-only grant or profile-record link never exposes its containing folder; a member leaf cannot reveal an otherwise unshared parent or sibling.

`profileIds` shows only direct active owned associations, restricted to the selection when selected; inherited visibility does not invent a direct association. Foreign recipients never see the owner's profiles. `collectionIds` shows only currently permitted collections that actually include the folder through a member at that folder or an ancestor. `parentFolderId` is present only when the immediate parent independently has a current folder route; a direct target link or target-only membership is insufficient. Hidden organization stays absent.

One database statement captures current identity account/access policy and selection, target, complete same-owner ancestry, references and every contributing foreign grant. Hidden or incomplete ancestry returns nonrevealing 404 before native/content inspection. A fully active relevant cycle yields 503; unrelated corruption is not inspected. Overlapping foreign grants are each validated against current native pins and collection/grant/recipient binding, with pins parsed once per party. Necessary corrupt evidence fails the entire result, even when another valid route overlaps. A revoked grant or removed member after the snapshot takes effect on the next GET. Reads never mutate content, relationships, sessions or metadata and take no resource locks.

Malformed visible input is 400; missing identity/access is 401; revoked, expired, superseded or hidden selected access is 403; absent/inactive/terminal account or hidden target is 404. Required dependency failures and corrupt stored envelope/metadata/native binding are 503 with no partial payload or private dependency detail. Supported content uses positive safe counters and UTC microsecond timestamps. The shared OpenAPI requiredness/nullability and empty-413 metadata findings remain before-client-release follow-ups. Independent formal security/protocol, external-client key provisioning/decryption/operability and durable offline visibility qualification remain mandatory before release.
''';p.write_text(s)
p=w/'README.md';s=p.read_text();lines=s.splitlines();matches=[i for i,x in enumerate(lines) if 'UC-25 — Get folder' in x];assert len(matches)==1 and lines[matches[0]].endswith('Todo |');lines[matches[0]]=lines[matches[0]][:-len('Todo |')]+'Done |';s='\n'.join(lines)+'\n';assert '16 / 26 closed' in s;s=s.replace('16 / 26 closed','17 / 26 closed');p.write_text(s)
p=w/'CHANGELOG.md';s=p.read_text();needle='### Added\n';assert needle in s;s=s.replace(needle,needle+'\n- Encrypted folder detail (UC-25), with current owner or native recipient scope,\n  selected folder ancestry and only visible direct profile, effective collection\n  and independently permitted parent references from one permission snapshot.\n  See the [folder detail API](docs/security/folder-api.md#get-an-encrypted-folder-uc25).\n',1);p.write_text(s)
p=w/'docs/requirements/Operations & Infrastructure Document.md';s=p.read_text();s+='''
### Folder detail (UC25)

GET `/api/folders/{id}` requires current identity and vault access, canonical public ID,
no query override or GET body, and returns only eight encrypted metadata/visible-reference
fields. One target-first SQL snapshot revalidates current selection, complete owned
ancestry and all relevant foreign native bindings before exposing references; record-only
authority never exposes a folder. Reads are no-store, take no resource locks and make
no mutation. Hidden/incomplete targets stay404; needed corruption/dependencies fail503.
See [folder detail API](../security/folder-api.md#get-an-encrypted-folder-uc25).
Shared OpenAPI metadata, independent formal security/protocol and real-client/production
qualification remain outstanding release gates.
''';p.write_text(s)
p=w/'docs/operations/foundation-status.md';s=p.read_text();s+='''
UC25 adds [encrypted folder detail](../security/folder-api.md#get-an-encrypted-folder-uc25)
with current owned/native recipient scope and only permitted profile/collection/parent
references. A target-first database snapshot checks complete ancestry and all native
contributors, with no locks or mutations. Record-only access cannot expose its container.
Formal security/client and production release qualification remain outstanding.
''';p.write_text(s)
print('UC25 detail/API/operations/changelog/feature README17of26 updated; generated contract/full verification remain')
