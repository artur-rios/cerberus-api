# UC-35 Grant collection access design

Binding design: UC-34 PR #96 is merged; local and remote `develop` point to
`e6ea84cebd166d897fa5613f3e2c3ab7171f213a`, whose tree exactly matches the
reviewed feature tree. Issue #36 is In Progress on its dedicated branch. The
mandatory recipient-account foreign-key audit was completed on PostgreSQL 18.6
before this design: reciprocal owner-first inserts deadlocked through the
implicit recipient `FOR KEY SHARE`, while one ordered `FOR UPDATE` lock over both
account rows allowed both reciprocal grants to commit.

## Intent and scope

Implement issue #36, UC-35 main flow and AF-01 through AF-05, FR-SH-01 through
FR-SH-03, and the applicable BR-10, BR-27 and BR-28 rules. An authenticated
collection owner creates an explicit read-only or read/write grant for an active
recipient account using a client-created recipient envelope. A previously revoked
grant can be explicitly regranted with the same grant identity and a new revision.

Add dedicated domain contracts, command/handler/validator, guarded PostgreSQL
store, HTTP route, tests, OpenAPI and operational documentation. Reuse the
existing collection, grant and protection schema; add no migration, dependency,
plaintext processing, client key discovery endpoint, permission-change endpoint,
revocation endpoint, profile association, offline change stream or usable key.
UC-36 owns active permission changes, UC-37 owns revocation, and UC-48 owns durable
offline visibility delivery and removal.

## Public contract

`POST /api/collections/{id}/grants` requires current Heimdall authentication,
exactly one canonical `X-Cerberus-Vault-Access` handle, no query, a canonical
lowercase dashed nonzero collection UUID, and bounded strict UTF-8 JSON. The
parsed media type is exactly `application/json` case-insensitively, optionally
with a valid UTF-8 charset. Compression, BOMs, invalid UTF-8, duplicate, unknown,
case-forged or missing fields, and other JSON media types are rejected. All
responses are no-store and oversized input is an empty 413.

The body has exactly five required properties:

- `grantId`: client-chosen canonical nonzero UUID.
- `recipientAccountId`: existing recipient's public Cerberus account UUID.
- `expectedRevision`: explicit `null` for an initial grant, or a positive safe
  current grant revision strictly below `9007199254740991` for regrant.
- `access`: exactly `read-only` or `read-write`.
- `keyEnvelope`: strict `cerberus-recipient-wrap-v1` material.

For an initial grant, the envelope binds revision 1. For regrant it binds
`expectedRevision + 1`. In both cases it binds the submitted grant ID, current
owner account ID and author pin, resource kind `collection`, target collection ID
and current key epoch, and the current recipient Heimdall identity and recipient
key pin. Shape-valid but incorrectly bound, stale-key, wrong-signature or wrong-pin
caller material is 400 `validation_failed`. The server never decrypts, creates or
returns the wrapped root.

Success is 201 `collection_access_granted` with exactly `collectionId`, `grantId`,
`revision`, `access`, `recordIds` and `folderIds`. The arrays are sorted, unique
direct active members with complete active same-owner ancestry. They describe the
collection's current direct visibility roots; descendants of a shared folder are
resolved dynamically by existing read/list rules. No profile ID, account identity,
internal ID, collection envelope, recipient envelope, protection material or key
is returned.

## Authority, visibility and native binding

Wide owner access permits an active owned collection. Selected Master or
PerProfile access additionally requires the exact selected active profile's
current `ProfileCollection` link. Selection is never cleared or widened. Only the
owner manages grants. A currently visible foreign read-only or read/write target
returns 403 only after its one relevant native grant, collection epoch/envelope,
recipient identity and current owner/recipient pins validate. Corrupt relevant
native evidence returns 503. Hidden, revoked, unselected or absent foreign targets
return nonrevealing 404 without inspecting the requested recipient.

The requested recipient must be a distinct active, nonterminal account with
current protection material. Missing, closing, terminal or not-yet-protected
recipients return 404. Present malformed recipient protection, malformed owner
protection or malformed stored collection envelope/epoch is required persistence
corruption and returns 503. The recipient needs no vault session or profile, and
grant creation creates no `ProfileCollection` row. Account-wide recipient access
observes the grant immediately; a selected recipient must later establish its own
permitted profile association through the existing profile-association API.

An active grant for the collection/recipient pair conflicts even when its bytes
match; POST is not an idempotent replay and UC-36 owns changes. A revoked pair can
be regranted only when grant ID and expected revision match exactly. It retains
the same row, public ID, collection and recipient, advances revision once, becomes
Active, and replaces access, envelope and concurrency stamp. Its old revoked
envelope is not authority and cannot poison regrant. A different ID, stale or
missing expectation, public-ID collision, terminal reservation, unsafe or
exhausted revision, or a concurrent winner returns 409 and preserves the winner.

## Atomic storage and lock protocol

An initial target-first snapshot determines authentication, current session,
owner versus foreign visibility, selection and candidate owner/recipient IDs.
Foreign denials acquire no requested-recipient lock. The successful owner path
then uses this order:

1. owner and recipient account rows together, ordered by public UUID,
   `FOR UPDATE`;
2. actor vault-access session `FOR UPDATE`;
3. selected owned profile, when present, `FOR NO KEY UPDATE`;
4. target owned collection `FOR UPDATE`;
5. captured matching-pair and grant-ID-collision rows ordered by public UUID,
   `FOR UPDATE`.

Both account rows are locked in one ordered statement before any collection or
grant row. This is the mandatory audit result: it serializes reciprocal grants and
recipient lifecycle/protection writers before the insert's implicit FK key-share
lock. Never lock the recipient account after a collection/grant, upgrade an earlier
weak account lock, or acquire a newly discovered earlier lock late. Existing
owner, membership, rotation, profile-association and recipient-content writers
remain compatible because a grant transaction waiting on another actor's account
has not yet acquired either collection.

After every potentially waiting phase, re-evaluate current database-time actor
authority, selection, target ownership/lifecycle, recipient lifecycle and the
captured grant state. Parse each party's protection material once after account
locks and validate the current collection envelope/epoch. The guarded insert or
reactivation reasserts current session and lifecycle predicates and is the
authorization linearization point. Natural expiry after that statement does not
cancel an already authorized atomic operation.

The transaction changes only one grant row. Collection identity, ciphertext,
epoch, revision, sequence, memberships and owner metadata remain exact. Recipient
account/protection/profile/session data remain exact. The post-write grant and
member projection are reloaded and validated before commit. Any insert,
reactivation, projection or post-write fault rolls back the whole change; a clean
retry can succeed. No collection/content lock is acquired for descendants because
membership is dynamic and the collection lock serializes current membership
replacement.

## Flow mapping and verification

- Main: strict request -> current owner scope -> ordered owner/recipient accounts ->
  current pins and collection binding -> insert/regrant -> exact 201 projection.
- AF-01: malformed transport, IDs, nullable-CAS form, access or envelope returns
  400 without mutation.
- AF-02: inactive/hidden actor, collection, owner, recipient or selection returns
  nonrevealing 404.
- AF-03: missing identity/access returns 401; invalid current owner access or valid
  visible foreign administration returns 403.
- AF-04: duplicate active pair, stale/mismatched regrant, reserved/colliding ID,
  exhausted revision or concurrent winner returns 409.
- AF-05: required stored/native corruption, database failure, internal timeout or
  failed post-write verification returns 503 and rolls back. Caller cancellation
  propagates.

Command tests cover the exact five-field model, explicit-null presence, initial
and regrant revision relations, both access values, every envelope field, trusted
context, error allowlist and exact six-field output. Real PostgreSQL tests cover
wide/selected owner success, active/revoked/duplicate/collision paths, recipient
and protection states, owner/recipient/collection binding, native foreign denial,
direct-member projection and ancestry, faults and post-write rollback.

Concurrency tests must demonstrate the actual implicit FK behavior and ordered
fix: reciprocal grant creation, recipient lifecycle and protection writers in both
acquisition orders, membership/delete/rotation coexistence, same-pair initial and
regrant races, every wait-phase expiry or state loss, exact held account order and
no deadlock. HTTP tests use real native envelopes and prove immediate recipient
GET/list visibility without profiles leaking. Full six-family tests, per-production
assembly 90% line coverage, aggregate branch coverage, locked restore, NuGet audit,
native Maven/Python/OSV/corpus/helpers, specs, OpenAPI drift and exact-head CI are
required before merge.

## Deferred release gates

The API validates native envelope signatures and current pins but does not provide
recipient-key discovery, authenticated trust establishment or client-side
decryptability proof. Independent security/protocol/client qualification remains
a release gate. UC-48 still owns durable offline additions/removals, and copied
plaintext or roots cannot be recalled. Shared generated OpenAPI requiredness,
wrapper and media-type limitations remain documented until their common fix.

## Five review priorities

1. The two account rows are locked together in canonical order before every
   collection/grant lock, with real reciprocal PostgreSQL proof.
2. Only an active owner with current wide or exact selected authority can grant;
   foreign visibility is validated before 403 and never enables administration.
3. New and regrant envelopes bind exact current owner/recipient pins, identity,
   collection epoch, grant ID and revision; caller errors are 400 and stored
   corruption is 503.
4. Initial, duplicate-active and revoked-regrant semantics preserve the unique
   collection/recipient invariant and never revive a grant accidentally.
5. One grant mutation and the exact no-profile visibility projection commit or
   roll back together without changing ownership, collection/member metadata or
   either account's protection graph.
