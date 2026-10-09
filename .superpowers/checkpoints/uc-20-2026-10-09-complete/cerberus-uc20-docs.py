from pathlib import Path
import json
r=Path('/tmp/cerberus-uc20-worktree')
p=r/'README.md';s=p.read_text();s=s.replace('26 | 11 / 26 closed','26 | 12 / 26 closed');lines=s.splitlines();lines=[x[:-6]+'Done |' if 'UC-20 — Delete record' in x and x.endswith('Todo |') else x for x in lines];p.write_text('\n'.join(lines)+'\n')
p=r/'docs/security/record-api.md';p.write_text(p.read_text()+'''

## Delete an encrypted record (UC20)

`DELETE /api/records/{id}` requires a canonical lowercase nonzero UUID, current
Heimdall identity and one canonical `X-Cerberus-Vault-Access` handle. Send exactly
`{"expectedRevision": 1}` with the current positive safe integer revision. The
strict UTF-8 JSON rules above apply; query parameters, ciphertext, timestamps,
relationships and server context are rejected.

Only the current owner may delete a currently visible record. Current native
read-only and read/write recipients both receive `403`, before revision or target
content checks. Every relevant current foreign grant is still validated. Hidden,
unselected, revoked or trashed records return nonrevealing `404`. Complete active
ancestry is required even for a direct link. Damaged owned opaque ciphertext can
be trashed without parsing or decrypting it; the bytes and original client edit
time remain unchanged for later recovery.

Success is `200`, `record_deleted`, with exactly `recordId`, `trashOperationId`,
`revision`, `serverSequence`, `deletedAt` and `purgeAt`. The record advances one
revision and global sequence, receives a fresh concurrency stamp, and is retained
for exactly 720 hours from database statement time. Both timestamps use UTC
microseconds. No ciphertext, owner graph, grants, keys or access handle is returned.

The transaction snapshots all stored direct owned profile and collection links,
including inactive links, and the original immediate folder. It detaches the folder
and removes direct record links atomically. Only affected active owned direct
profiles, direct collections and the immediate folder advance structural metadata;
their ciphertext, client edit time, key wrappers and epochs are preserved. Ancestors,
recipient profiles, other graph links and unrelated records remain unchanged.
Selected profile access stays valid, while get/list exclude the deleted record.

| Status | Meaning |
| --- | --- |
| 400 | Invalid ID/header/query/body or missing/invalid expected revision |
| 401 | Missing/invalid current identity or missing vault access |
| 403 | Invalid/expired/revoked/stale access or selection, or visible foreign record |
| 404 | Missing/closing/terminal actor or absent/hidden/out-of-scope/trashed target |
| 409 | Revision/sequence conflict or exhaustion, new unheld parent, or conflicting old typed trash entry |
| 413 | Common request byte limit exceeded; response body is empty |
| 503 | Dependency unavailable or relevant native/ancestry/structural metadata is corrupt |

All responses are `no-store`. Own account and session locks precede ordered owned
profile and collection locks, the immediate folder, the target record and existing
typed association rows. Parent locks permit foreign-key references; the target lock
stabilizes new references. Current scope is reloaded after waits, and the final
statement reevaluates complete ancestry, lifecycle and exclusive expiry. New unheld
parents require reload. No foreign account/profile/resource locks are acquired.
All content, association, parent, trash operation, entry and queue writes roll back
together on failure. After a lost success, retry returns `404` without creating a
second operation or extending retention. Sequence gaps are valid.

Restoration and the typed expiry handler belong to UC51 and UC53 and must ship
before release. The existing fail-closed executor retries unavailable handlers;
it does not claim physical purge. Restore must revalidate every retained association
and consume or reconcile the old typed entry before a later deletion. Retained
records remain in complete protection-rotation inventory. Physical purge also
requires the pending resource-kind-aware terminal repair and must never turn a
selected profile handle into account-wide access by clearing its selection. Existing
independent protocol/client release approvals remain mandatory.
''')
p=r/'docs/requirements/Operations & Infrastructure Document.md';p.write_text(p.read_text()+'''

### Recoverable record deletion (UC20)

`DELETE /api/records/{id}` accepts only the current expected revision under current
owned scope and returns six public trash metadata fields. Current native foreign
RO and RW access cannot delete. The atomic transaction retains unchanged opaque
ciphertext and client edit time for 720 hours, snapshots all stored direct typed
associations including inactive ones, detaches the folder and direct record links,
and advances affected active immediate owned parent structural metadata. Selected
profile handles stay valid. Lost-success retry returns404 without extending expiry.

Own account NO KEY UPDATE and session UPDATE precede ordered owned profiles and
collections NO KEY UPDATE, immediate folder NO KEY UPDATE, record UPDATE and typed
association row UPDATE. Current scope is reloaded after waits and complete ancestry,
lifecycle and exclusive expiry are guarded at database statement time. New unheld
parents return409; failures roll back record, links, parents, trash and queue writes.
No foreign account/profile/resource locks, schema or dependency changes are added.
Damaged opaque owned content can be trashed without decryption; corrupt required
structural or relevant native authority metadata fails closed.

One typed trash operation/entry and `trash/{operationId}` retention item commit with
the mutation. The existing executor fails closed and retries an unavailable handler.
UC51 must implement association-revalidated restoration and consume/reconcile the
old typed entry before re-trash; UC53 must implement typed expiry before release.
Retained records remain mandatory in protection-rotation inventory. Before ANY
physical purge, repair cross-kind terminal GUID handling and preserve selected
session isolation: never clear selected ProfileId to null. See the
[record API](../security/record-api.md#delete-an-encrypted-record-uc20). Independent
protocol/client qualification remains a release prerequisite.
''')
p=r/'CHANGELOG.md';s=p.read_text();s=s.replace('### Added\n','''### Added

- Recoverable owned record deletion (UC-20), with exact revision checks, unchanged
  retained ciphertext, restricted association snapshots and a 720-hour deadline.
  Atomic link removal and immediate parent metadata updates hide the record while
  preserving selected access. Native recipients cannot delete, and retries cannot
  extend retention. Restoration and typed expiry remain later release prerequisites.
  See the [record API](docs/security/record-api.md#delete-an-encrypted-record-uc20).
''',1);p.write_text(s)
p=r/'.superpowers/sdd/2026-10-09-uc-20-delete-record/progress.md';p.write_text(p.read_text()+'\nTask3 focusedHTTP99/99PASS zero failure-skip (http-green-1.log) after observed missingrouteRED; no product corrections. API/operators/changelog/READMEUC20DoneM03twelve updated; OpenAPI/NuGet/full3878 next.\n')
p=Path('/root/repositories/cerberus-api/.superpowers/checkpoints/open-issues-2026-10-08.json');d=json.loads(p.read_text());v='UC20 Task3 HTTP99PASS after missingrouteRED; docs updated, OpenAPI/NuGet/full3878 pending; Tasks1+2 complete f3b1dba. ONE fresh final review, blocking TDD pass if needed, PR/CI/normal merge/DoD then UC21.20UCs merged.';d['currentTask']=v;d['resumeInstruction']=v;p.write_text(json.dumps(d,indent=2)+'\n')
