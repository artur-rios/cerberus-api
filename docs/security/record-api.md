# Encrypted record API (UC16–22)

`POST /api/records` requires a current Heimdall bearer identity and one canonical
`X-Cerberus-Vault-Access` opaque handle. The server derives ownership from that
identity. It accepts strict UTF-8 JSON without a BOM, compression, query parameters,
duplicate properties, unknown properties, numeric strings or alternate property
casing. Responses use `Cache-Control: no-store`.

The required body fields are `recordId`, `envelope`, `editedAt` and `profileIds`.
`folderId` is optional and may be null. Public IDs use canonical lowercase UUIDs;
profile IDs must be nonzero and distinct. The envelope must use the native
`cerberus-content-v1` format with initial content epoch 1 and canonical unpadded
base64url fields. Choose the public record ID before encrypting.

The encrypted payload contains the name and freeform or template-defined fields.
Clients enforce name and field requirements, including text, numeric, boolean and
hidden-text types. The server cannot inspect those values and accepts no plaintext
name, fields or template ID alongside the envelope. Collection membership is handled
by its separate use case; it is not a creation-body field.

Account-wide access permits no profile links or multiple owned active profile links,
and an optional owned active folder. A selected-profile handle permits creating new
owned content only when `profileIds` contains exactly the selected profile. An
optional folder must already be directly linked to that profile or descend from
one of its directly linked, owned active folders. All ancestors must remain owned,
active and nonterminal. A grant to another owner's collection, including a write
grant, does not permit creating content for that owner.

Success is `201` with `record_created` and exactly four data fields: `recordId`,
`revision` (1), `serverSequence` and `editedAt`. The edit time must have UTC offset
zero and be at least `0001-01-01T00:00:00.000001Z`; the server floors it to microseconds
before PostgreSQL binding, including dates before 2000. Sequence gaps are valid.

Creation atomically inserts the record and direct profile links. Each specified
parent profile and the optional parent folder advances its structural revision,
server sequence and concurrency stamp. Existing ciphertext, key wrappers, edit times
and links remain intact. Clients reload parent metadata after creation. No expected
parent revision is needed for this additive operation; an operation replacing an
existing association set must still submit its current expected revision.

| Status | Meaning |
| --- | --- |
| 400 | Invalid body, envelope, IDs, timestamp, header or query |
| 401 | Missing/invalid current identity or missing vault access |
| 403 | Expired/revoked/stale access, invalid selection, or an empty profile set under selected access |
| 404 | Missing, foreign, trashed, terminal or out-of-selection relationship/account |
| 409 | Record ID already retained/reserved, exhausted revision or sequence |
| 413 | Body exceeds the common configured limit; response body is empty |
| 503 | Identity/persistence unavailable or corrupt stored metadata/ancestry |

The transaction locks the account, session, owned target profiles in public-ID order
and then the owned folder ancestry. It rechecks access after lock waits and checks
exclusive expiry, current policy/generation and lifecycle at database statement time
in the final insert. Duplicate-ID races have one winner. A link or metadata failure
rolls back all record, link and parent changes; retrying the same ID after a failed
transaction can succeed. If a successful response is lost, a retry with that ID
conflicts and preserves the original record; do not silently invent a new ID.

New records participate in the complete protection-rotation inventory, including
recoverable trash. UC22 reserves terminal identifiers by resource kind and public
ID; a terminal record never hides or reserves a folder, collection, profile, account
or grant with the same UUID. Terminal records awaiting purge are excluded from
protection rotation. Creation itself does not perform physical purge.
Independent protocol-security and real-client approvals remain pending and required
before release; development tests do not constitute those approvals.


## List encrypted records (UC17)

`GET /api/records` requires a current Heimdall identity and exactly one canonical
`X-Cerberus-Vault-Access` handle. It accepts only optional, single `pageSize` and
`cursor` query parameters and no request body or transfer encoding. `pageSize` is a
positive canonical decimal within `MaxPageSize`; the default is the smaller of 50
and that configured maximum. Search text and profile overrides are not accepted.
Every response uses `Cache-Control: no-store`.

Success is `200` with `records_found`. Data contains exactly `items` and nullable
`nextCursor`; each item contains exactly `recordId`, `revision`, `serverSequence`,
`editedAt` and the native encrypted `envelope`. An empty visible inventory returns
an empty array and no cursor. Names and custom/template fields remain encrypted;
clients decrypt for display. The response contains no owner IDs, internal IDs,
profile/folder/collection links, grant wrappers or total counts.

Account-wide access lists active owned records, including unlinked records, and
active content included in collections shared with the actor under current native
read-only or read/write grants. Selected-profile access lists its direct owned
records, records beneath its directly linked owned folder roots, and included
content in owned or shared collections actually linked to that selection. Collection
memberships are typed record/folder links; folder inclusion dynamically includes
records in active descendants. Overlapping routes produce one item. A record's
complete folder ancestry must be active and nonterminal, even for a direct record
link. Trashed/terminal resources, hidden ancestors, closing owners and revoked or
irrelevant grants are omitted before pagination. A fully active relevant cycle
fails `503`; a hidden ancestor takes precedence and omits that content.

One PostgreSQL statement captures current account/session/selection authority,
folder ancestry, permitted inventory, native grant evidence, boundary and page.
Traversal starts from current permitted routes and then validates complete ancestry
for the candidate records.
Relevant foreign grants must bind the current collection envelope and epoch, grant
ID/revision, owner author pin and recipient identity/key pin. Corrupt relevant native
evidence fails the whole response `503`, including any otherwise valid owned items;
revoked, hidden or unselected unrelated grants are not parsed. Listing performs no
writes and does not expand a selection or grant.

Pages are ordered by unique positive safe-integer server sequence, with permitted
ordering integrity checked before keyset filtering. The first page fixes the current
maximum visible sequence as its highwater; later inserts beyond it are excluded.
Each continuation rechecks current permissions, so revocations can shorten or empty
later pages. Ordinary listing is not a durable synchronization snapshot: concurrent
edits may move content beyond the initial highwater; refresh to observe them. UC48
provides the separate synchronization contract.

Cursors are authenticated and encrypted, purpose-separated from profile-list
cursors, and bound to actor, hashed access handle, effective page size, last sequence
and highwater. Keep the same handle and page size when continuing. All replicas use
the same dedicated `RegistrationFingerprintKey`; rotating it invalidates outstanding
cursors with `400`, after which clients restart listing. The handle itself is never
inside a cursor. No authentication-signing key is reused.

Invalid query/header/body/cursor yields `400`; missing identity/handle yields `401`;
expired, revoked, stale or invalid selected access yields `403`; a missing, closing
or terminal actor account yields `404`; identity/persistence failure or corrupt
visible/native authority data yields `503`. The common oversized-body guard can
return `413` with an empty body before the GET body rejection. Errors carry no record
payload. Stored record envelopes and safe revision/sequence/UTC microsecond metadata
are validated before any successful response.

Apply the additive `20261008224646_CollectionMembership` migration before deploying
listing. It creates only typed collection-record and collection-folder membership
tables and indexes, with same-owner composite foreign keys; existing encrypted rows
and metadata remain intact. It does not introduce a collection mutation endpoint.
The pending independent protocol/client release approvals still apply; UC22
provides typed record erasure and record backup replay.

## Get an encrypted record (UC18)

`GET /api/records/{id}` requires current Heimdall identity and one canonical
`X-Cerberus-Vault-Access` handle. The path ID must be a nonzero lowercase UUID in
hyphenated D format. Query parameters, request bodies and transfer encoding are
rejected. Every response uses `Cache-Control: no-store`.

Success is `200` with `record_found`. Data contains exactly `recordId`, `revision`,
`serverSequence`, `editedAt`, the native encrypted `envelope`, `profileIds`, nullable
`folderId` and `collectionIds`. The two ID arrays are always present, distinct and
sorted. Names, custom fields and template information remain inside the ciphertext.
No owner/internal IDs, key material, grant blobs or private owner organization are
returned. The five content fields follow the same strict native, safe-integer and
UTC microsecond rules as list items.

Current account-wide ownership, selected direct-record/folder routes and selected
eligible collection routes follow the listing visibility rules. Read-only and
read/write recipients can get only content included directly or through current
collection-folder descendants. Every ancestor must remain active and nonterminal,
even when a direct record link admits the record. Scope and relationship changes
are re-evaluated on every get.

The returned relationships have their own visibility boundaries:

- `profileIds` contains only active, nonterminal, actor-owned **direct** profile
  links. A selected handle sees only its selected direct link; an account-wide
  handle sees all its direct links. A recipient always receives an empty array for
  another owner's record. Indirect folder or collection routes do not invent links.
- `folderId` exposes the actual parent only when that folder is itself visible.
  An account-wide owner sees the active parent. Selected owners need a selected
  profile-folder or linked collection-folder route covering it. Recipients need a
  collection-folder route covering it. A direct record link alone returns a null
  parent when the parent is outside scope.
- `collectionIds` contains only currently eligible collections that actually
  include the target directly or through a folder ancestor. Selected handles see
  only eligible collections linked to that profile. Foreign references require
  valid current native grants. Deduplication is within each kind; the same UUID
  across resource kinds remains valid.

The read uses one parameterized database statement for current account/session/
selection, at most one potential record, its complete upward ancestry, visible
relationships and native grant evidence. Owner and recipient protection pins are
captured and validated once for this target, then every contributing foreign grant
is checked against current identity, author, collection, epoch and exact grant
revision. There are no read mutations or foreign-owner locks. A change committed
after the statement appears on the next read.

| Status | Meaning |
| --- | --- |
| 400 | Noncanonical ID, malformed access header, query override or request body |
| 401 | Missing/invalid current identity or missing vault access |
| 403 | Invalid, expired, revoked or stale access, including hidden/dangling selection |
| 404 | Missing/closing/terminal actor or absent, foreign, unselected or hidden target |
| 413 | Common request limit exceeded; response body is empty |
| 503 | Identity/persistence unavailable, relevant active ancestry cycle, invalid current native evidence or corrupt visible content/metadata |

Hidden content or an ancestor produces nonrevealing `404` before native corruption
checks. Fully active cycles produce `503` only when the target has a current
permission route. A bad relevant grant fails the entire response; no partial
relationships or record are returned. Target lookup checks its own sequence and
metadata without inspecting other records; inventory ordering integrity belongs to
listing and synchronization. Clients must decrypt and validate content themselves.
The typed terminal-ID behavior and pending independent security/client approvals
remain as described above; this endpoint grants no release approval.


## Update an encrypted record (UC19)

`PUT /api/records/{id}` requires a canonical lowercase nonzero UUID, current Heimdall
identity and one canonical `X-Cerberus-Vault-Access` handle. Submit exactly the required
`expectedRevision`, native `envelope` and UTC `editedAt`. The strict UTF-8 JSON rules
above apply; query parameters, owner/context overrides and relationship fields are
rejected. The server cannot inspect names, custom fields or template requirements.

An owner may update currently selected or account-wide visible owned content. A
recipient needs at least one current native read/write collection grant that includes
the record directly or through an active folder ancestor. Selected handles also need
that collection linked to their profile. Visible read-only content returns `403`;
absent, foreign, unselected, revoked or hidden content returns nonrevealing `404`.
Complete ancestry must remain active even when the record has a direct link. Every
current contributing foreign native grant is validated; malformed relevant evidence
fails the whole operation `503`, including a malformed read-only contribution beside
a valid write grant. Unrelated or revoked grants are not inspected.

A matching revision permits replacement at the current content epoch. Identical
envelopes are accepted; changed envelopes require both a fresh key salt and nonce.
Key rotation belongs to the protection flow. Edit time must meet the same minimum
and UTC rules as creation and is floored to microseconds before storage. Older and
equal timestamps are accepted with a matching revision; this online endpoint does
not apply offline timestamp conflict resolution.

Success is `200`, `record_updated`, with exactly `recordId`, `revision`,
`serverSequence` and `editedAt`. The record advances one revision, receives a greater
safe server sequence and a fresh concurrency stamp. Ownership, parent, memberships,
profile links, parent metadata and protection material remain unchanged. No envelope,
owner graph, grants or keys are returned. Use get to reload current encrypted content.

| Status | Meaning |
| --- | --- |
| 400 | Invalid ID/header/query/body, unsupported native envelope, changed epoch, reused salt/nonce, or invalid revision/time |
| 401 | Missing/invalid current identity or missing vault access |
| 403 | Invalid/expired/revoked/stale access or selection, or visible read-only content |
| 404 | Missing/closing/terminal actor or absent/hidden/out-of-scope target |
| 409 | Revision mismatch/exhaustion, sequence exhaustion or new authority requiring reload |
| 413 | Common request byte limit exceeded; response body is empty |
| 503 | Dependency unavailable or relevant stored/native metadata or ancestry is corrupt |

The transaction locks its own actor account, session and selection, then eligible
collections and grants in public-ID order, then the target record. Account and record
locks permit foreign-key references without allowing concurrent content/policy
writes. It takes no foreign account/profile or folder locks. Current scope and native
evidence are reloaded after waits; the final mutation reevaluates complete ancestry,
current permission, lifecycle and exclusive expiry at database statement time. New
unheld authority requires a reload. Creation and reciprocal recipient edits do not
need each other's account locks. Owner rotation and recipient edits cannot overwrite
a winning content revision.

All responses are `no-store`. Failures roll back every content change. After a lost
successful response, retrying the old expected revision returns `409` and preserves
the winner; reload before retrying. Sequence gaps are valid. This endpoint neither
changes relationships nor performs physical purge. Typed terminal identifiers and mandatory independent security/client release
approvals remain as described above.


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
and consume or reconcile the old typed entry before a later deletion. Recoverable records remain in complete protection-rotation inventory. UC22 provides
typed permanent record erasure; terminal records awaiting purge are excluded.
Never clear a selected profile handle into account-wide access. Independent
protocol/client release approvals remain mandatory.

## Move an encrypted record (UC21)

`PUT /api/records/{id}/folder` requires the current owner, a current vault handle,
a canonical public record UUID, no query parameters and strict UTF-8 JSON. The
body has exactly two required properties:

```json
{"expectedRevision": 1, "folderId": "abcdefab-1234-4321-abcd-123456789abc"}
```

Use an explicit `"folderId": null` to remove the parent. Omitting `folderId` is
invalid. Nonnull folder IDs must be canonical lowercase, nonzero UUIDs, and
`expectedRevision` must be a positive safe integer. Envelope, client edit time,
owner, key wrappers and relationship arrays are not move inputs.

Success is 200 `record_moved`, with exactly `recordId`, `folderId` (always present,
including null), `revision` and `serverSequence`. The record advances one revision
and receives a greater safe sequence and fresh concurrency stamp. Its ID, owner,
original ciphertext bytes, content epoch and client `editedAt` stay unchanged.
Each distinct changed old/new immediate folder advances its structural revision
and sequence once; ancestors, profiles, collections and direct typed links do not
change. A same-parent or null-to-null move with the current revision succeeds and
advances only the record. A lost-success retry with the old revision returns409;
reload before retrying. Parent revision exhaustion does not block a no-op.

Account-wide access permits any active owned destination whose complete ancestry
is active. A selected handle requires the source and destination to be currently
visible. Destination visibility comes from selected profile-folder ancestors or
owned collection-folder ancestors linked to that profile; a direct record link
alone does not expose its parent. A selected move may retain or remove existing
inherited scope, but the resulting effective active owned profile and collection
sets must each be subsets of their original sets. A destination already visible
through the selected profile can still be rejected if it would introduce another
profile or collection. Use account-wide access for intentional scope expansion.

Get and list derive inherited visibility from the new ancestry immediately. Old
inherited routes disappear, new authorized routes appear and unchanged direct
links survive. The move validates every current native recipient envelope needed
by the resulting collection routes and compares that evidence in its final write.
Native content contexts bind immutable owner/kind/resource ID/epoch, not the parent
folder. Existing valid content and recipient envelope bytes are retained; the
server creates no usable key and does not claim successful client decryption.
Clients must confirm trusted roots and authenticated decryption. Additional opaque
client provisioning, if needed, and independent protocol and real-client release
qualification remain mandatory before release.

Native-visible foreign records return403 for both read-only and read/write
recipients before destination, stale revision or content probing. Hidden records
return404 before malformed native evidence is inspected. Current account, session,
selection and full source/destination ancestry are rechecked after waits and in
the guarded write. Newly contributing earlier collection/grant authority or a
changed source parent requires409 and reload; recipient pin changes cannot reuse
stale evidence. Expiry is exclusive at the database statement snapshot, with no
commit-time guarantee. Target and both immediate parent mutations commit together
or roll back together.

Invalid input returns400, missing identity or handle401, invalid current access or
visible foreign ownership403, hidden/unselected source or destination and selected
scope expansion404, stale/exhausted metadata or changed authority409, oversized
body an empty413, and dependency, relevant ancestry, native or visible content
corruption503. All responses use `Cache-Control: no-store`. Damaged opaque content
may be recoverably deleted through UC20; a move does not repair or propagate it.
Durable synchronization of inherited visibility changes remains a later integration
requirement. This operation does not create trash entries or physically purge data.


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
