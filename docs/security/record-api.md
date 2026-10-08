# Encrypted record API (UC16–17)

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
retained trash. The existing global terminal-ID reservation is conservative across
resource kinds until the separately required typed-erasure repair before physical
purge. This endpoint does not perform physical purge or repair that reservation.
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
The pending independent protocol/client approvals and typed-erasure obligation
before physical purge still apply.
