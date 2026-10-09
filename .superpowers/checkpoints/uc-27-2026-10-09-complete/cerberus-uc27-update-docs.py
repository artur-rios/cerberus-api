from pathlib import Path
w=Path('/tmp/cerberus-uc27-worktree')
p=w/'docs/security/folder-api.md';s=p.read_text();assert '## Recoverably delete a folder (UC27)' not in s
s+='''
## Recoverably delete a folder (UC27)

`DELETE /api/folders/{id}` trashes a currently visible owned folder and its active owned subtree. Send current Heimdall authentication, one `X-Cerberus-Vault-Access` header, a canonical lowercase dashed nonzero UUID and bounded UTF8 JSON containing exactly required `expectedRevision`. No query or cascade overrides are accepted. All responses are no-store.

Success is 200 `folder_deleted` with exactly `folderId`, `trashOperationId`, `revision`, `serverSequence`, `deletedAt`, `purgeAt`. The root, active descendants and their active contained records share the actual database root deletion time and a deadline exactly 720 hours later. Each member advances revision and server sequence once. Ciphertext, key wrappers, client edit times and ownership are retained. Parent references and direct folder/record profile/collection links are detached atomically; typed private snapshots retain original parent and sorted public association IDs, including inactive associations. Each distinct active external linked profile/collection and surviving immediate root parent advances structural metadata once; unrelated and recipient metadata stay unchanged.

Selected access admits the visible owned root and its complete active owned subtree, including descendants with other profile links and records shared elsewhere. Owners must account for this cascade effect. Native read-only and read/write recipients receive 403 after all relevant folder contributors are verified. Record-only grants or selected profile-record links never admit a container. Hidden resources return 404 before stale revision or opaque-content inspection. Corrupt relevant native evidence returns 503 even with another valid route. Damaged owned ciphertext can still be retained in trash without parsing or decryption.

Previously independently trashed records/folders are detached and excluded, retaining their original operation and deadline. Terminal typed identities are never revived or conflated with another resource kind. Lost-success retries return 404 and cannot extend retention. A stale root, exhausted member/parent counters or a changed captured inventory returns 409 `revision_conflict`; reload the current permitted state before retrying. Required structural corruption and dependency failures return 503 `persistence_unavailable`, without partial output. Malformed input is 400, missing identity/access 401, and denied current access 403. Oversized transport receives the common empty 413.

The transaction serializes current owner writers through the own account lock, then locks selection/profiles, collections, captured folders/records and typed links in defined order. Current database-time authority is rechecked after waiting phases. The guarded root write revalidates the entire captured inventory and is the authorization boundary for the atomic cascade; expiry after this point does not cancel an already authorized operation. Child, record, parent, association, operation, entry and queue failures roll back the whole graph. Sequence gaps can remain. No foreign account/profile/grant or upward ancestor lock chain is acquired; future grant creation requires the separate recipient-FK lock audit before UC35.

One folder trash operation, typed entries and `trash/{operationId}` retention item commit together. Active folder and record reads/lists then omit these members even with other direct shares; existing grants remain active. This cannot retract copies already held by recipients. The existing retention executor retries a missing trash handler: physical expiry/purge (UC53), trash listing/restore/empty (UC50–52) and durable inherited visibility synchronization (UC48) remain mandatory later work. Restore must revalidate saved membership and consume old typed entries before re-trash; it must not revive revoked grants. Retained folders and records remain in complete protection-rotation inventory, including their original trash deadline and membership. Shared OpenAPI metadata and independent security/protocol/external-client qualification remain release follow-ups.
''';p.write_text(s)
p=w/'README.md';s=p.read_text();old=next(x for x in s.splitlines() if 'UC-27 — Delete folder' in x);assert old.endswith('Todo |');s=s.replace(old,old.removesuffix('Todo |')+'Done |');assert '18 / 26 closed' in s;s=s.replace('18 / 26 closed','19 / 26 closed');p.write_text(s)
p=w/'CHANGELOG.md';s=p.read_text();a='- Encrypted folder content updates (UC-26),';assert a in s;s=s.replace(a,'''- Recoverable owned folder deletion (UC-27), with atomic active subtree/record
  cascades, one 30-day deadline, retained opaque ciphertext and typed restore
  snapshots. Earlier independent trash retains its deadline; recipient reads
  disappear while grants remain active. Restore, physical expiry and durable
  synchronization remain later work. See the
  [folder deletion API](docs/security/folder-api.md#recoverably-delete-a-folder-uc27).

'''+a);p.write_text(s)
p=w/'docs/operations/foundation-status.md';s=p.read_text();s+='''
UC27 adds [recoverable owned folder cascades](../security/folder-api.md#recoverably-delete-a-folder-uc27)
with retained opaque folders/records, typed association snapshots and one720hour
operation/queue. Independent trash keeps its deadline; active recipient reads
hide deleted content while grants remain active. Complete protection rotation
includes retained members. UC48 durable inherited visibility and UC50–53 trash
listing/restore/empty/physical expiry remain release obligations; missing trash
handlers currently retry. Before UC35, audit implicit recipient-account FK locks
against strong owner account writers. Shared metadata and independent formal
security/protocol/client qualifications remain development-only deferred.
''';p.write_text(s)
p=w/'docs/requirements/Operations & Infrastructure Document.md';s=p.read_text();s+='''
### Recoverable owned folder cascade (UC27)

DELETE `/api/folders/{id}` accepts strict required `expectedRevision` and returns
six public deletion metadata fields. A visible owned root admits its active owned
subtree and active contained records even when selected access does not link every
member independently. Native RO/RW recipients cannot delete; all relevant folder
contributors are verified before403, while record-only routes remain404.

Own account UPDATE serializes current owner creation/move/association/rotation;
ordered profiles/collections precede captured folders/records/direct links. Fresh
authority after every waiting phase and a complete root inventory guard reject
stale scope, expiry and uncaptured growth. The root guarded write linearizes the
atomic cascade. All members retain ciphertext/edit times/ownership, share the
root's database UTC microsecond deletion time and exact720hour purge deadline,
and advance counters once. Typed parent/profile/collection snapshots include
inactive links; distinct active external parents advance once. Independent prior
trash and typed terminal branches remain untouched. All stages roll back together,
including operation/entries/queue; global sequence gaps are allowed.

Active reads/lists hide members and remove direct associations without revoking
grants or retracting recipient copies. One folder trash operation/many typed
entries/one `trash/{operationId}` queue commit atomically. Current missing trash
handlers retry; UC50–53 must implement listing/revalidated restore/empty/physical
expiry, and UC48 durable visibility remains required. Retained members stay in
complete native protection rotation; restore must consume old typed entries before
re-trash and must not revive revoked grants. Strong account locking requires the
separate implicit recipient-FK audit before UC35. Independent qualification and
shared OpenAPI metadata remain release gates. See the
[folder deletion API](../security/folder-api.md#recoverably-delete-a-folder-uc27).
''';p.write_text(s)
print('UC27 docs, README Done/M03 19 of26 and explicit retention/restore/sync/qualification limits updated.')
