# Encrypted collection API

## Create a collection (UC29)

`POST /api/collections` creates a private owned collection with permitted initial relationships. Send current Heimdall authentication, one `X-Cerberus-Vault-Access` header and bounded UTF8 JSON. No query parameters are accepted. All responses use `Cache-Control: no-store`.

The body has exactly six required fields: `collectionId`, `envelope`, `editedAt`, `profileIds`, `recordIds`, `folderIds`. IDs use canonical lowercase dashed nonzero UUIDs. Each array is nonnull and unique within its resource kind; explicit empty arrays and the same UUID across different kinds are supported. Choose the collection ID before client encryption. The envelope uses `cerberus-content-v1` at initial key epoch 1. A name belongs in the encrypted client payload; the server cannot inspect it. Plaintext, ownership, grants, key wrappers, proof, expected parent revisions and access overrides are rejected.

`editedAt` must be UTC and at least `0001-01-01T00:00:00.000001Z`. The server floors it to microseconds before PostgreSQL binding, including pre-2000 timestamps. Success is 201 `collection_created` with exactly `collectionId`, `revision`, `serverSequence`, `editedAt`. Initial revision is 1 and the sequence is a positive safe integer. Internal IDs, ownership, credentials and linked ciphertext are absent from this output.

Account-wide access permits empty relationships or multiple owned active profiles. Selected access requires exactly the current selected profile. Every requested folder or record must already be visible through that selection's owned direct links, folder ancestry or owned collection membership. Record-only access never admits its containing folder. A visible leaf does not expose private ancestors or siblings. All members must belong to the same owner and have complete active same-owner ancestry. Foreign native read-only/read-write recipients cannot attach another owner's content, even when a current native GET succeeds. Creation does not broaden the session or provision a grant or usable key.

The collection, direct profile/collection, collection/folder and collection/record links commit atomically. Each distinct explicitly linked profile, folder and record advances structural revision, server sequence and concurrency stamp once. Overlapping direct folder and record membership preserves both typed links. Existing ciphertext, client edit times, parent references, protection, key wrappers, grants, unrelated relationships, recipient metadata and prior trash operations remain exact. Ancestors, inherited descendants and nonlinked profiles receive no fabricated direct changes. Owned grouping does not decrypt or repair damaged existing member ciphertext; required changed structural metadata is validated.

The transaction locks the own account and session, requested/selected profiles, contributing owned collections, requested folders and requested records in that order. Collections precede content, as in current native recipient editors. No foreign account, profile, grant or upward ancestor chain is locked. Current authority is rechecked after each wait and inside the guarded INSERT, which is the authorization boundary. Completing that authorized atomic operation after later natural expiry is permitted. Changed valid captured state or newly needed unheld scope requires reload and retry. Current permission loss is returned before uniqueness or stale metadata inspection. Controlled owner and native recipient writer races are tested; the separate implicit recipient-account FK audit remains required before UC35 introduces grant creation.

Malformed/unknown/duplicate/case-forged fields, invalid envelope/time/typed IDs, compressed/non-UTF8/BOM transport and malformed/repeated access headers return 400. Shared oversized input returns an empty 413. Missing identity or access is 401; revoked, expired, superseded or invalid selected access is 403. Hidden, missing, foreign, trashed or terminal relationships and inactive accounts return nonrevealing 404. A retained or permanently reserved collection ID, stale captured state or exhausted revision/sequence capacity returns 409 `revision_conflict`. Relevant active ancestry cycles, required structural corruption and unavailable persistence return 503 `persistence_unavailable`, without private dependency detail or success data. Hidden/incomplete ancestry is rejected before corrupt content inspection; unrelated cycles are ignored.

Collection identity is reserved within the collection kind. Other resource kinds may use the same UUID independently. A concurrent creation has one winner. Retrying a lost successful response returns 409 and preserves the winning collection and links. A failure after any actual INSERT, link batch or structural write rolls back the entire graph; the same ID can succeed on a clean retry. Global sequence gaps after rollback are allowed. Reload current permitted metadata before retrying a conflict.

New collections enter the complete retained owned protection-rotation inventory immediately. Actual native Master and PerProfile rotations include the new collection and current linked content/profile revisions. Omitted, extra, foreign or stale replacements reject before proof consumption. Successful rotation preserves client edit times and typed relationships, advances the collection epoch and revokes old selected access. Rewrapping preserves content and links.

Existing profile/folder/record GET and list operations recalculate current initial membership and inherited visibility with hidden-reference filtering. Collection updates and deletion are described below; membership replacement belongs to UC34. UC48 must provide durable inherited visibility and offline removals. External key provisioning and authenticated client decryption, shared OpenAPI requiredness/wrapper/empty-413 metadata, and independent security/protocol/client qualification remain release obligations. The owner's development-only review deferral leaves main/tag/release gates intact.

## List collections (UC30)

`GET /api/collections` returns current permitted encrypted collections. Send current
Heimdall authentication and exactly one `X-Cerberus-Vault-Access` header. Only
canonical `pageSize` and an opaque `cursor` are accepted; repeated or unknown query
keys, selected-profile overrides, repeated/malformed headers and any request body
return 400 `validation_failed`. Omit `pageSize` to use the smaller of 50 and the
configured maximum; an explicit size must be positive and at most that maximum.
All responses use `Cache-Control: no-store`, including failures.

Success is 200 `collections_found`. The data object has exactly `items` and
nullable `nextCursor`. Each item has exactly `collectionId`, `revision`,
`serverSequence`, `editedAt`, and `envelope`. The envelope remains opaque native
`cerberus-content-v1` ciphertext. Stored key epoch is checked against its envelope
but is not an additional item field. Internal IDs, owner or recipient identities,
profile/member inventories, grants and protection material are absent.

Account-wide access admits active owned collections and active native read-only or
read-write recipient grants. Selected access additionally requires an exact
current profile/collection link. Direct profile/folder or profile/record links,
collection content membership, and folder ancestry cannot imply collection
permission. Collections, owners and grants in trash, closure, revocation or their
typed terminal state are omitted before pagination. A terminal marker for another
resource kind with the same UUID does not hide a collection. Listing changes no
resource, relationship, ciphertext, edit time or authority state.

One PostgreSQL statement captures the current actor/account, session policy and
revocation generation, statement-time expiry, selected owned active profile,
visible collection inventory, native grant evidence and page. Every currently
visible foreign collection requires a valid native envelope and epoch, signed
recipient wrapper and grant revision, and current owner/recipient protection
pins. Evidence for visible foreign collections is validated even outside the
returned page or carried highwater window; malformed/missing required evidence
fails the whole request without partial data. Hidden and out-of-selection evidence
cannot poison a page. No subsequent authority read, foreign lock, content traversal
or scope widening occurs.

Items are ordered by positive unique safe `serverSequence` after permission
filtering. The first page captures the maximum currently visible sequence. The
cursor carries that highwater boundary and binds actor, access verifier, page size
and last sequence with authenticated encryption under a collection-only purpose.
Folder, record and profile cursors, modified cursors and substituted context
return 400. Continuations reapply current permissions and retain the original
boundary, excluding later inserts. This is not a frozen multi-request snapshot:
revocations or removed profile links can remove items, and edits can move them
past the boundary. A terminal page has `nextCursor: null`; a valid empty scope is
200 with an empty `items` array.

Missing or current-rejected identity and missing access return 401. Expired,
revoked, stale-policy or invalid selected-profile access returns 403
`vault_access_denied`. Unknown or inactive acting accounts return 404 `not_found`.
Visible corrupt native evidence, ordering, projection or unavailable persistence
returns 503 `persistence_unavailable`; an unavailable current identity dependency
returns 503 `identity_unavailable`. Errors disclose no private dependency detail or
success data. Shared oversized transport still returns an empty 413. Caller
cancellation propagates; internal dependency cancellation fails as 503.

## Get an encrypted collection (UC31)

`GET /api/collections/{id}` retrieves one currently permitted collection. The ID
must be a nonzero canonical lowercase dashed UUID. Send current Heimdall identity
and exactly one `X-Cerberus-Vault-Access` header. No query parameter or request
body is accepted, including transfer-encoded or unknown-length HTTP/2 bodies.
Duplicate, malformed or empty access headers return 400 `validation_failed`.
Every response is `Cache-Control: no-store`; shared oversized transport returns
an empty 413.

Success is 200 `collection_found`. The data object has exactly `collectionId`,
`revision`, `serverSequence`, `editedAt`, `envelope`, `profileIds`, `recordIds`,
and `folderIds`. The ciphertext envelope is unchanged native
`cerberus-content-v1`; its epoch must match the stored epoch. Revision and sequence
are positive safe integers, and edit time is nondefault UTC at microsecond
precision. The complete projection is validated before any payload is returned.
Stored epoch, internal IDs, owner/recipient identities, grants, key wrappers,
protection pins and usable keys are absent.

Account-wide access admits an owned collection or a current native read-only or
read/write recipient grant. Selected Master or PerProfile access additionally
requires that exact profile's current `ProfileCollection` link. Access to a
record, folder or descendant never grants access to its containing collection.
The next request observes session, selection, grant and relationship removals.

Relationship arrays are nonnull, unique within each resource kind and sorted by
public UUID. The same UUID can independently identify a profile, record and folder.
`profileIds` contains only the actor's active directly linked profiles; selected
access returns only the selected profile. A recipient never receives the owner's
or another recipient's profile identifiers. `recordIds` and `folderIds` contain
only direct active same-owner members with complete active same-owner ancestry.
An explicitly linked record remains present when it also descends from a directly
linked folder. Descendants and private containers are not added. Hidden, terminal
or incomplete-ancestry members are omitted, including when all arrays become
empty. Member ciphertext and ordering metadata are not parsed to return an ID.

One target-first PostgreSQL statement captures current account/session authority,
statement-time expiry, selected profile, target, direct links, relevant upward
ancestry and target-native evidence. It acquires no explicit locks and changes no
state. Fully active cycles affecting direct members fail safely; unrelated trees
and collections are not traversed. A foreign target requires current signed grant
binding, envelope/epoch and owner/recipient protection pins. Only that target's
native evidence is inspected. Owned projection validation remains in the query
layer. This server admission does not provision keys or certify client decryption.

Missing/current-rejected identity returns 401 `authentication_required`; missing
access returns 401 `vault_access_required`. Invalid transport returns 400,
current denied access returns 403 `vault_access_denied`, and missing/inactive
actors or missing, hidden, foreign, unselected or revoked targets return
nonrevealing 404 `not_found`. Required persistence, native-evidence, ancestry or
projection corruption returns 503 `persistence_unavailable` without partial data.
Unavailable current identity returns 503 `identity_unavailable`. Caller cancellation
propagates; internal dependency cancellation becomes a safe 503. Independent
security/protocol and external-client qualification remain release gates.

## Update an owned collection (UC32)

`PUT /api/collections/{id}` replaces one active owned collection's encrypted
envelope. Send current Heimdall authentication, exactly one canonical
`X-Cerberus-Vault-Access` header, a lowercase dashed nonzero UUID path and bounded
strict UTF-8 JSON. No query parameters are accepted. All responses, including
failures, use `Cache-Control: no-store`; shared oversized input returns an empty 413.

The body contains exactly three required fields: `expectedRevision`, `envelope`
and `editedAt`. Revision must be a positive safe integer no greater than
`9007199254740991`. The envelope must be strict `cerberus-content-v1` and retain
the stored key epoch. If the envelope changes, both its `keySalt` and `nonce`
must be fresh. An identical envelope is permitted for a metadata edit.
`editedAt` must be UTC and at least `0001-01-01T00:00:00.000001Z`; it is floored
to PostgreSQL microseconds, including dates before 2000. Older and equal client
timestamps are accepted: expected revision controls concurrency.

Unknown, duplicate, case-forged or missing fields, numeric strings, invalid
envelopes, reused salt or nonce, changed epoch, malformed IDs/access headers,
compressed bodies, BOMs and invalid UTF-8 return 400 `validation_failed`.
Collection ID, owner, key epoch outside the envelope, profiles, records, folders,
grants and recipient wrappers cannot be supplied as additional body fields.

Success is 200 `collection_updated`. The data object contains exactly
`collectionId`, `revision`, `serverSequence` and normalized `editedAt`.
The ID matches the target; revision advances by one and server sequence becomes
a greater positive safe integer. Ciphertext, internal IDs, ownership, associations,
grants, protection pins, wrappers and usable keys are absent from this response.
GET detail returns the updated envelope and the preserved visible relationships.

Account-wide owner access admits any active owned collection. Selected Master or
PerProfile access requires the exact current `ProfileCollection` link; a removed
selection never widens to account-wide access. Collection updates are owner-only:
even a current native read/write recipient receives 403 `vault_access_denied`.
For a visible foreign target, the single relevant signed grant, revision,
collection identity/epoch/envelope, recipient identity and current protection pins
are verified before that denial. Corrupt required native evidence returns 503.
Hidden, revoked or unselected foreign targets return 404 without inspecting
irrelevant evidence or revealing revision/ciphertext corruption. Owner updates
do not inspect grants; native grant binding excludes collection ciphertext and
the epoch cannot change here.

Only the target envelope, edit time, revision, server sequence and concurrency
stamp change. Ownership, public ID, key epoch, deletion/purge state, all profile
and content memberships, grants, recipient wrappers and related resource metadata
remain exact. Updating does not rotate keys or replace membership.

The transaction locks the actor account, session, selected profile when present,
and admitted target collection in that order. It does not lock foreign authority
or unrelated collections. Authority is reloaded after waits and reasserted in
the guarded UPDATE alongside the expected revision, previous sequence/envelope
and unchanged epoch. Current access or lifecycle loss wins over stale revision.
Protection rotation serializes through the owner's account lock. A successful
row is reloaded and verified before commit; a post-write failure rolls back.

Missing identity returns 401 `authentication_required`; missing access returns
401 `vault_access_required`. Inactive/terminal actors and missing, hidden or
inactive targets/owners return 404 `not_found`. Invalid current sessions or
selections and valid visible foreign targets return 403 `vault_access_denied`.
Stale or exhausted revision, winning concurrent edits, newly unheld authority
or exhausted global sequence return 409 `revision_conflict`. Required persisted
metadata/native corruption, outage, timeout, internal cancellation or failed
post-write verification return 503 `persistence_unavailable`. Unavailable current
identity returns 503 `identity_unavailable`; caller cancellation propagates.
Every failure preserves collection data and relationships.

Two same-revision requests have exactly one winner. Retrying a lost success with
the old revision returns 409 and preserves that winner; reload current permitted
metadata before retrying. PostgreSQL sequence gaps after rollback are permitted.
Generated OpenAPI includes the route and request/response models, but shared
requiredness, output-wrapper and empty-413 schema limitations remain. Actual HTTP
tests enforce the exact runtime shapes. Independent security/protocol review and
external client decryption/qualification remain release gates.

## Recoverably delete a collection (UC33)

`DELETE /api/collections/{id}` trashes one currently visible owned collection
for exactly 720 hours. Send current Heimdall authentication, one canonical
`X-Cerberus-Vault-Access` header, a lowercase dashed nonzero UUID path and bounded
strict UTF-8 JSON with media type `application/json` (case-insensitive, optionally
with a UTF-8 charset), containing exactly `{"expectedRevision":1}`. The required
revision is a positive integer at most `9007199254740991`. Query parameters,
unknown/duplicate/case-forged fields, numeric strings, ownership, membership,
grants, wrappers, ciphertext and lifecycle overrides are rejected. Non-JSON,
compressed, BOM or invalid UTF-8 bodies return 400 `validation_failed`.
All responses use `Cache-Control: no-store`; shared oversized input is an empty 413.

Success is 200 `collection_deleted` with exactly `collectionId`,
`trashOperationId`, `revision`, `serverSequence`, `deletedAt` and `purgeAt`.
The revision advances once, the sequence becomes a greater safe integer, and
both timestamps are UTC at PostgreSQL microsecond precision, exactly 720 hours
apart. No envelope, association snapshot, grant, identity, wrapper or key is returned.

Wide owner access permits an active owned target. Selected Master or PerProfile
access requires its current direct profile/collection link; selection loss never
widens access. Native read-only and read/write recipients cannot delete a collection.
A visible foreign target returns 403 `vault_access_denied` after validating its
current signed grant binding, collection envelope/epoch and owner/recipient pins.
Corrupt relevant native evidence returns 503; hidden, revoked or unselected foreign
targets return nonrevealing 404 before stale revision or irrelevant corruption.

The collection retains its ciphertext, client edit time, owner and key epoch.
Every stored profile/collection, collection/folder and collection/record link is
removed, with sorted distinct public IDs retained in one restricted typed trash
entry. The snapshot includes inactive links and recipient-owned profile links;
it contains no grants or wrappers. Each distinct active directly linked owned
profile, folder and record advances its revision, sequence and concurrency stamp
once. Foreign profiles and inactive resources do not advance. Underlying records
and folders stay active with the same opaque content, parent, owner, edit time and
direct noncollection links. Owner direct access survives; inherited recipient
GET/list visibility disappears immediately.

Every active target grant becomes Revoked and advances revision once. Already
revoked grants stay exact. Grant identity, recipient, access and opaque wrapper
bytes are retained; recipient sessions remain valid. This does not rotate keys,
destroy client copies or promise immediate offline removal. Restore must never
reactivate these grants. UC51 must revalidate all restored links, including current
ownership and a newly active grant for recipient profile links; re-sharing requires
the sharing use cases. The retained collection remains in complete protection
rotation inventory. UC48 supplies durable offline visibility removal.

The atomic transaction locks the actor account/session, ordered linked owned and
selected profiles, target collection, active owned folders/records, grants and
typed links. It locks no foreign account/profile/protection or unrelated content.
Current authority is rechecked after waits; the complete captured graph is reloaded
under locks and checked again in the guarded lifecycle update. New unheld links or
changed captured state conflict; harmless disappearance uses the fresh graph.
Membership removal, owned structural bumps, grant revocation, one trash operation,
one typed entry and one `trash/{operationId}` retention item commit together.

Missing identity/access returns 401; invalid current session/selection returns 403.
Inactive or terminal actors, targets or owners and hidden targets return 404.
Stale/exhausted revisions, exhausted sequences, changed captured state or an existing
typed entry return 409 `revision_conflict`. Required stored/native corruption,
outage, timeout, internal cancellation or failed post-write verification return
503 `persistence_unavailable`; unavailable current identity is 503
`identity_unavailable`. Caller cancellation propagates. Owned damaged ciphertext
can be trashed unchanged. Any post-write failure rolls back the entire graph;
sequence gaps are allowed. Same-revision concurrent deletion has one winner.
A lost-success retry returns 404 and cannot create another operation or extend
the deadline.

UC50–53 trash listing, restoration, emptying and physical expiry remain later work;
missing collection retention handlers currently retry. A restored collection must
reconcile its old typed entry before another trash operation. The generated OpenAPI
includes this route and models; shared advertised media-type, requiredness,
output-wrapper and empty-413 schema limitations remain, with actual HTTP tests
enforcing the runtime contract. Formatter-advertised `text/json` and
`application/*+json` request types are rejected at runtime.
Independent security/protocol and external-client qualification remain release gates.
