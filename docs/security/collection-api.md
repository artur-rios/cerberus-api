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

Existing profile/folder/record GET and list operations recalculate current initial membership and inherited visibility with hidden-reference filtering. Collection detail/update/deletion and membership replacement belong to UC31–34. UC48 must provide durable inherited visibility and offline removals. External key provisioning and authenticated client decryption, shared OpenAPI requiredness/wrapper/empty-413 metadata, and independent security/protocol/client qualification remain release obligations. The owner's development-only review deferral leaves main/tag/release gates intact.

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
