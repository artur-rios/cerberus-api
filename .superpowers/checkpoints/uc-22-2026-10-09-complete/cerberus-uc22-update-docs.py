from pathlib import Path
p=Path('docs/security/record-api.md');s=p.read_text().replace('# Encrypted record API (UC16–21)','# Encrypted record API (UC16–22)')
s=s.replace('retained trash. The existing global terminal-ID reservation is conservative across\nresource kinds until the separately required typed-erasure repair before physical\npurge. This endpoint does not perform physical purge or repair that reservation.', 'recoverable trash. UC22 reserves terminal identifiers by resource kind and public\nID; a terminal record never hides or reserves a folder, collection, profile, account\nor grant with the same UUID. Terminal records awaiting purge are excluded from\nprotection rotation. Creation itself does not perform physical purge.')
s=s.replace('The pending independent protocol/client approvals and typed-erasure obligation\nbefore physical purge still apply.','The pending independent protocol/client release approvals still apply; UC22\nprovides typed record erasure and record backup replay.')
s=s.replace('The existing conservative terminal-ID behavior and pending independent security/\nclient approvals remain as described above; this endpoint grants no release approval.', 'The typed terminal-ID behavior and pending independent security/client approvals\nremain as described above; this endpoint grants no release approval.')
s=s.replace('The conservative cross-kind\nterminal-ID behavior and mandatory independent security/client release approvals\nremain as described above.','Typed terminal identifiers and mandatory independent security/client release\napprovals remain as described above.')
s=s.replace('Physical purge also\nrequires the pending resource-kind-aware terminal repair and must never turn a\nselected profile into account-wide access.', 'UC22 implements typed permanent record erasure and preserves nonnull selected\nprofile IDs. A profile erasure must still revoke or retain a denied selection; it\nmust never turn selected access into account-wide access.')
# Match the current final paragraph accurately if its line wrapping differs.
s=s.replace('Physical purge also\nrequires the pending resource-kind-aware terminal repair and must never turn a', 'Typed permanent record erasure is implemented by UC22 and must never turn a')
s+='''

## Permanently delete a record (UC22)

`DELETE /api/records/{id}/permanent` requires current Heimdall identity and exactly
one canonical `X-Cerberus-Vault-Access` handle. The path ID is a nonzero lowercase
hyphenated UUID. Send exactly `{"expectedRevision": 1}` with the current positive
safe integer revision. Strict UTF-8 JSON, header and request-limit rules above apply.
All responses are `no-store`.

Only the owner can permanently erase a currently visible active or trashed record.
Current native read-only and read/write recipients both receive `403`; hidden or
foreign trashed records return `404`. Every contributing native grant is checked
before a visible ownership denial. The owner's encrypted content and client edit
time need not be valid to erase it. Required revision/sequence and affected live
parent counters must still be valid. A matching maximum safe target revision is
accepted, because permanent deletion does not advance a live record revision.

Account-wide owners may erase retained trash even after its restore window ends,
without relying on historical associations. Selected access requires a current
active owned profile and the restricted typed trash membership: its original
profile link, a retained owned collection still active and attached to the current
selection, or a retained folder whose complete current active owned ancestry is
within the selection. A valid direct historical route does not require an unused
old folder path. Unretained live links and foreign grants cannot widen access.
Missing history or a hidden route returns `404`; malformed necessary history is
`503`. Historical snapshots are never returned or reactivated.

Success is `200`, `record_permanently_deleted`, with exactly `recordId` and
`deletedAt`, the terminal-intent time at UTC microsecond precision. Success means
the external erasure ledger has been durably flushed, the exact record ciphertext
and direct associations have been removed, and its purge work has completed. It
returns no content, key, live revision, sequence or trash snapshot. Live immediate
parents and direct owned profiles/collections advance structural metadata once;
parents already updated by recoverable deletion do not advance a second time.
Other resources sharing the record UUID remain intact. The typed record ID stays
reserved permanently, so recreating it returns `409`.

| Status | Meaning |
| --- | --- |
| 400 | Invalid body, revision, UUID, header or query |
| 401 | Missing/invalid current identity or missing vault access |
| 403 | Invalid current access/selection or visible foreign ownership |
| 404 | Missing, hidden, terminal or outside current permitted scope |
| 409 | Stale revision, new unheld authority or exhausted live parent capacity |
| 413 | Common byte limit exceeded; empty response body |
| 503 | Identity/persistence/ledger unavailable, lost purge claim or corrupt required authority/metadata |

**A `503`, cancellation or lost response after the intent commits does not undo
permanent deletion.** The committed terminal marker immediately denies all reads,
edits, moves, recoverable deletions and identifier reuse. Ciphertext may remain
physically pending while the durable worker retries. Retrying the public operation
returns `404` and does not create another intent or extend a deadline. Recoverable
trash deletion uses the separate route without `/permanent`.

The request and worker use the configured `RetentionInterval` as their claim lease.
Every physical mutation and completion requires the exact persisted work ID, token,
operation key, unfinished state and exclusive database-time expiry. External ledger
flush precedes physical removal. Failed intent transactions roll back; failures
after committed intent retain durable work for recovery. Restore replay removes
terminal record ciphertext before traffic opens; see the [restore runbook](../operations/restore.md).
Durable offline terminal ordering and independent protocol/client qualification
remain required before release.
'''
p.write_text(s)
p=Path('docs/operations/restore.md');s=p.read_text()+'''

## Typed permanent record erasure (UC22)

Apply `20261009035540_TypedTerminalErasure` with ingress disabled before serving
permanent-delete requests. The unique terminal identity is `(resource_kind,
resource_id)`, with account, profile, record, folder, collection and grant kinds.
Legitimate existing markers retain their kinds and times; invalid existing kinds
fail migration closed and require operator repair. Do not merge distinct kinds
sharing a UUID. Reverting to the old global UUID uniqueness can fail once typed
collisions exist; use a reviewed forward migration rather than deleting markers.

Preserve both new `kind-GUIDN.json` files and legacy `GUIDN.json` files in the
external ledger. Legacy contents determine their resource kind. Reads validate
kind/ID/filename, privacy, symlinks and corruption; do not rename files, replace
this ledger with a database backup or remove it to recover traffic. Repeated same
kind/ID events retain the original terminal time, and replay preserves the earliest
known time. The ledger contains minimal identity/kind/time, no content or keys.

Permanent deletion commits a typed terminal intent and `record-purge/{UUID}` work
before external ledger I/O. A pending record is already inaccessible and its ID
reserved even if the public request returns `503` or is cancelled. After the
configured lease expires, a fresh worker claim retries the same intent, flushes
the ledger, physically removes only that record and completes under database-time
claim fencing. Monitor existing retention failure signals. Correct the ledger or
database outage and restore the worker; do not clear terminal intent or reuse the
record ID. Recoverable trash remains in key rotation; terminal pending records do
not. Short worker intervals can expire during filesystem I/O and cause safe retries.

Record ledger replay upserts the typed marker and removes restored record
ciphertext, direct links and typed record trash membership in one transaction
before the traffic gate opens. Empty root-record trash operations and their trash
work are removed; nonempty cascades, other kinds, profiles, folders, collections
and grants survive. Other resource kinds retain fail-closed typed tombstones until
their physical replay handlers ship. Selected profile IDs are never cleared.

`RecordPurgeHandlerTests.GivenBackupBeforePermanentDeletion_WhenRestoringAndReplayingLedger_ThenRemoveCiphertextBeforeTrafficAndKeepOtherKinds`
uses an actual PostgreSQL dump taken before erasure, restores its ciphertext,
replays the preserved external ledger and checks physical record absence before
opening traffic while a same-UUID folder survives. Claim-expiry/theft, rollback,
ledger outage and corrupt-reconciliation tests cover failed recovery. These server
fixtures do not certify a production restore drill or external client behavior.
''';p.write_text(s)
p=Path('docs/operations/profile-trash.md');s=p.read_text().replace('Before any physical purge, repair cross-kind terminal-erasure identifier collisions and prove that unrelated content survives.','UC22 repairs terminal identities by resource kind and UUID and proves record-only erasure with unrelated same-UUID content preserved. Physical profile deletion and replay still require their own later handlers; selected profile IDs must never be cleared into account-wide access.');p.write_text(s)
p=Path('README.md');s=p.read_text().replace('| 26 | 13 / 26 closed |','| 26 | 14 / 26 closed |');lines=s.splitlines();assert sum('UC-22 — Permanently delete record' in x for x in lines)==1;lines=[x[:-6]+'Done |' if 'UC-22 — Permanently delete record' in x and x.endswith('Todo |') else x for x in lines];p.write_text('\n'.join(lines)+'\n')
p=Path('CHANGELOG.md');s=p.read_text().replace('### Added\n','''### Added

- Permanent owned record erasure (UC-22), with current active/trash scope, typed
  terminal IDs, durable intent and fenced purge recovery. Success follows ledger
  flush and physical removal; failures after intent keep deletion irreversible.
  Backup replay removes record ciphertext before traffic and preserves other kinds
  sharing its UUID. Terminal records leave rotation inventory while recoverable
  trash remains. See the [record API](docs/security/record-api.md#permanently-delete-a-record-uc22)
  and [restore runbook](docs/operations/restore.md#typed-permanent-record-erasure-uc22).
''',1);p.write_text(s)
