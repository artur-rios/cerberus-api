# Create encrypted records (UC16)

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
