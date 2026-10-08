# Profile listing

`GET /api/profiles` requires a current Heimdall identity and one
`X-Cerberus-Vault-Access` header. It returns `data.items` and `data.nextCursor`.
Each item has `profileId`, `revision`, `serverSequence`, UTC microsecond `editedAt`,
opaque `envelope`, `keyWrappers`, and visible `recordIds`, `folderIds`, `collectionIds`.
Names stay encrypted. There are no totals, account details, internal identifiers or
linked ciphertexts. Responses use no-store.

Use `pageSize` once, as a canonical positive decimal no greater than deployment
`CERBERUS_MAX_PAGE_SIZE` (required and startup validated). Default is the smaller of
50 and that bound. Pass the returned `cursor` with the SAME page size and access
session. Unknown parameters, duplicate parameters/headers, GET bodies, malformed
handles and invalid cursors return400. Missing identity/access returns401; denied
current access403; absent/closing/erased account404; unavailable dependencies or
corrupt permitted stored data503. Empty permitted lists return200 with empty items
and null nextCursor. Reads never change vault state.

Ownership, current selected-profile scope, trash and terminal erasure are checked
before ordering and pagination on every page, in one database statement. Collection
grants do not grant access to somebody else's profiles. A selected session sees
only its owned active selected profile. Current expiry, policy and revocation apply
even when a cursor was issued earlier. Invalid or duplicate sequences in permitted
profiles fail the whole page closed; hidden rows cannot trigger that diagnostic.
A returned owner wrapper must name the authenticated identity as recipient.
Profile access issuance belongs to UC15. The three association arrays are evaluated
in that same statement: own active direct items, and active collections whose owner
is active and whose recipient grant remains valid. Hidden links are omitted.
See [association rules](profile-associations.md).

Ordering uses increasing server sequence. The initial permitted high watermark
excludes later inserts/edits; sequence gaps and filtered profiles do not consume
page slots. Concurrently changed profiles can leave the listing boundary: refresh
from page1 to see them. This ordinary list is not the UC48 synchronization snapshot.
Cursors are encrypted/authenticated and bound to actor, hashed session handle, size
and boundary. Replicas sharing the dedicated registration fingerprint secret derive
the same versioned-purpose cursor key; JWT signing secrets are not used. Rotating
that deployment secret invalidates outstanding cursors (400); restart page1. Do not
log handles, cursor URLs, encrypted bodies or key wrappers.

Independent protocol/client approvals remain pending; this endpoint is development
work under the existing deferral. Release gates remain enforced separately.
