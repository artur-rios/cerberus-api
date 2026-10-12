# UC-37 Revoke collection access design

## Intent and scope

Implement issue #38, UC-37 and FR-SH-06 through FR-SH-08. An authenticated
collection owner revokes one exact active recipient grant. The committed change
removes that grant from all subsequent online authorization, invalidates the
recipient account's existing vault handles, erases the server-held recipient
wrapper and confirms the new grant revision.

Revocation cannot destroy plaintext or keys already copied by a recipient. The
server does not possess the client key needed to rotate collection ciphertext or
create replacement recipient envelopes. Authorized clients must use a future key
epoch that excludes the revoked recipient. UC-37 enforces the server-side
boundary now; UC-48 owns durable offline synchronization and reconnect delivery.
Independent protocol, security and client approval remain release gates.

## Selected approach

Keep the unique collection/recipient grant row and perform a monotonic state
transition from `Active` to `Revoked`. Advance its revision, clear its recipient
envelope, replace its concurrency stamp and increment the recipient account's
revocation generation in one database authorization statement.

This approach preserves optimistic concurrency, grant identity, audit continuity
and UC-35's controlled regrant semantics. Regrant must present the same grant ID,
the resulting revoked revision and a new correctly bound recipient envelope.

Rejected alternatives:

1. Rotate the collection key and every remaining recipient wrapper in this
   endpoint. The server cannot manufacture client-encrypted material, and the
   requirements explicitly place future key rotation with authorized clients.
2. Delete the grant row. Hard deletion loses the expected-revision boundary,
   creates ambiguous retries and conflicts with the established regrant model.
3. Revoke only the grant without a recipient generation change. Existing online
   handles would remain current and could continue unrelated requests without an
   explicit reconnect boundary, contrary to the required access-context
   invalidation.

## Public contract

`DELETE /api/collections/{id}/grants/{grantId}` requires a current Heimdall
identity, exactly one canonical `X-Cerberus-Vault-Access` header, canonical
lowercase dashed nonzero collection and grant UUID paths, no query parameters and
a bounded strict UTF-8 JSON body handled by `VaultProofBody`.

The body contains exactly one required property:

- `expectedRevision`: a positive safe integer strictly below
  `9007199254740991`, because success advances it once.

Unknown, duplicate, case-forged or missing fields, numeric strings, `null`, BOM,
compression, unsupported media types, malformed identifiers and extra query or
header values return `400 validation_failed`. The request cannot submit recipient
identity, state, access, key envelope, key epoch, profile, member or account
generation fields.

Success returns `200 collection_access_revoked` with exactly:

- `collectionId`: the canonical path collection UUID;
- `grantId`: the canonical path grant UUID;
- `revision`: `expectedRevision + 1`.

No access value, recipient identity, profile, generation, collection envelope,
recipient envelope, pin, key or internal identifier is returned. Every response
is `Cache-Control: no-store`; the shared oversized request response is an empty
`413`.

## Authority, visibility and error precedence

The actor must be an active nonterminal account with a current nonrevoked vault
session. Its positive policy and revocation generations must equal the account,
the session must be issued and satisfy renewal expiry, and a selected profile
must remain active, owned and nonterminal. A disappearing selected profile never
widens access.

Account-wide owner access admits an active owned collection. Selected owner
access admits only an active owned collection directly linked to the exact
selected profile. Only the collection owner may revoke grants.

A foreign collection is visible only through the actor's current active
read-only or read-write grant and, for selected access, an exact active profile
link. Before returning `403`, validate that visibility grant against the current
collection identity and epoch, exact grant identity and revision, actor Heimdall
identity, and current owner/actor protection pins. A hidden or unselected foreign
target returns `404` without locking the requested path grant. Corrupt relevant
foreign native evidence returns `503`; valid visible foreign access never grants
administration.

The successful owner path targets one exact active nonterminal grant belonging
to the path collection and an existing nonterminal recipient account distinct
from the owner. A grant already revoked before the request, absent grant,
wrong-collection grant, terminally reserved grant, hidden collection/owner or
missing/terminal recipient returns nonrevealing `404`.

The owner path deliberately does not require the old recipient envelope to pass
native verification. Revocation is a monotonic permission reduction and must be
able to quarantine and erase malformed stored wrapper bytes. It still validates
the grant's active state, exact collection/recipient relationship, positive safe
revision and the recipient account counter that will be advanced. Invalid stored
revision or unsafe account counters are persistence corruption, not caller input.

Error ordering is:

- Missing identity: `401 authentication_required`.
- Missing vault handle: `401 vault_access_required`.
- Invalid visible request input: `400 validation_failed`.
- Inactive/terminal actor, hidden target, or initially absent/inactive grant or
  recipient: `404 not_found`.
- Invalid current owner session/selection or valid visible foreign target:
  `403 vault_access_denied`.
- Stale/exhausted expected revision, changed captured authority, or a concurrent
  permission/revocation/regrant winner after initial capture:
  `409 revision_conflict`.
- Required relevant foreign native corruption, unsafe stored counters,
  database timeout/outage, internal cancellation or failed post-write
  verification: `503 persistence_unavailable`.

Caller cancellation propagates. Every failure preserves the winner and leaves
grant, generation, collection, membership, profile and session state unchanged.

## Revocation and future rotation semantics

The grant row remains reserved for its collection/recipient pair and public ID.
Success changes exactly:

- grant state from `Active` to `Revoked`;
- grant revision from expected to expected plus one;
- grant recipient envelope to an empty byte string;
- grant concurrency stamp to a fresh UUID;
- recipient account revocation generation by exactly one.

Grant access remains stored as historical non-authority. Collection identity,
ciphertext, key epoch, revision, sequence, timestamps and direct members remain
unchanged. Owner and recipient account revision, policy, lifecycle and protection
remain unchanged. Profiles, `ProfileCollection` links and vault-session rows also
remain unchanged.

Existing recipient handles become stale because their recorded generation no
longer matches the account. After a fresh unlock, every existing collection,
folder and record authorization path omits the revoked grant. Retained selected
profile links are inert without an active grant; they do not restore visibility.
A later explicit UC-35 regrant can reactivate the same row only with the revoked
revision and a new envelope bound to the then-current pins, identity, collection
epoch, grant ID and next revision.

Clearing the wrapper and filtering all authorization/projection queries to active
grants ensures revoked recipients are absent from server-returned key envelopes.
It does not rotate already distributed collection keys. Documentation and success
responses must not claim remote destruction or immediate cryptographic exclusion
from previously copied material.

The account-level generation is intentionally broader than one collection: the
current schema has no per-grant session generation. This is the same online
invalidation model used by UC-36. Durable revocation events and offline lease
enforcement remain UC-48 work.

## Atomic storage and lock protocol

Start with a target-first snapshot that classifies authentication, owner versus
foreign visibility, selection, the exact active path grant and its recipient.
Foreign denials acquire no requested-grant or requested-recipient lock.

The owner path acquires locks in this order:

1. owner and recipient account rows together in one statement, ordered by public
   account UUID, `FOR UPDATE`;
2. current owner vault session `FOR UPDATE`;
3. selected owner profile `FOR NO KEY UPDATE`, when present;
4. target owned collection `FOR UPDATE`;
5. exact active grant `FOR UPDATE`.

Both account locks precede collection and grant locks. This preserves the UC-35
and UC-36 reciprocal order and serializes recipient session issuance, lifecycle,
protection and profile-association writers before they can acquire later locks.
No recipient profile lock is needed because the account lock excludes those
writers and UC-37 does not mutate profile rows or links.

After every wait, reload authority, selection, owner/recipient lifecycle,
collection and grant state. A captured active grant that changes while waiting is
a `409`; an initially hidden/inactive grant remains `404`. Natural session expiry
after the guarded authorization statement does not cancel the committed atomic
operation.

The authorization linearization point is one CTE statement. Its guarded grant
CAS requires the exact collection, recipient, public grant ID, active state,
expected revision, captured authority graph and safe recipient generation. The
dependent account update can increment the recipient generation only by consuming
the successful grant update. A suppressed or partial side is a persistence fault
and transaction disposal rolls back the other side.

Post-write verification reloads and requires the exact revoked row, advanced
revision, empty envelope, fresh stamp and recipient generation plus one. It also
requires no active grant for the same pair, no authorization visibility through
the revoked row, and byte-for-byte stable collection/member/profile/session and
unrelated grant state. Any verification, trigger, projection or commit fault
rolls back both mutations.

Two same-revision revocations have one winner. Revocation races with UC-36 change
or UC-35 regrant preserve the locked winner and return a conflict to the loser.
Reciprocal revocations acquire the same account pair in the same order. Real
PostgreSQL tests must prove blocking with `pg_blocking_pids`, not only scheduling
signals.

## Flow mapping and verification

- Main flow: strict request -> current owner scope -> canonical account pair ->
  exact active grant capture -> atomic revoke/generation change -> exact 200.
- AF-01: strict transport, path and single-field body failures return 400 with no
  mutation.
- AF-02: hidden collection, inactive/revoked/terminal/wrong-collection grant or
  hidden recipient returns 404.
- AF-03: missing identity/handle returns 401; invalid owner access or valid native
  foreign visibility returns 403.
- AF-04: stale/max revision and owner/session/selection/grant races return 409 and
  preserve the winning state.
- AF-05: stored counter corruption, relevant foreign native corruption, database
  failures and post-write faults return 503 with full rollback.
- AF-06: responses and documentation explicitly distinguish online revocation,
  wrapper erasure and future client rotation from destruction of copied keys.

Command tests cover the exact one-field model, trusted path/session context,
validation precedence, safe error allowlist and exact three-field result. Real
PostgreSQL tests cover wide/selected owners, both access levels, hidden and
foreign cases, corrupt old wrappers, unsafe revisions/generations, exact mutation
scope, rollback, cancellation and bounded target queries.

Concurrency tests cover same-grant and reciprocal revocations, permission-change
and regrant races, recipient session issuance, lifecycle, protection and profile
association writers in both acquisition orders, exact held account order and
actual blocker evidence. HTTP tests use real native envelopes and prove that old
wide and selected recipient handles become stale; fresh unlocks cannot list,
read, create, update or move through direct or inherited collection visibility;
unrelated recipient content remains available; regrant requires the new revision
and a fresh envelope.

Run focused tests, all six Release families, per-production-assembly 90% line
coverage and aggregate branch coverage. Also require locked restore/build, NuGet
audit, native Java/Python/corpus/helper/OSV suites, specification validation,
generated OpenAPI drift/shape checks, source/evidence hashes, whitespace checks,
fresh task reviews, a whole-branch review and exact-head GitHub CI.

## Documentation and deferred gates

Update generated OpenAPI, the collection security contract, foundation status,
CHANGELOG and README tracker. After implementation the tracker must independently
count 38 Done and 17 Todo business use cases.

No schema, migration, package or cryptographic algorithm change is required.
Shared generated OpenAPI metadata limitations remain documented. External
protocol/security/client approval, authenticated recipient-key discovery,
production qualification, actual client-side collection-key rotation and durable
offline synchronization are not waived by this development merge.

## Review priorities

1. Owner and recipient accounts are strongly locked together in canonical order
   before collection/grant locks, with real reciprocal and adjacent-writer proof.
2. Only current wide or exact-selected owners revoke; foreign 403 requires valid
   native visibility and hidden targets remain 404 without requested-grant locks.
3. The exact initially active grant and expected revision are captured; malformed
   old wrapper bytes can be safely quarantined rather than blocking revocation.
4. The grant CAS, envelope erasure and recipient-generation increment are one
   atomic authorization write with exact rollback and mutation-scope proof.
5. Online visibility/session invalidation is proven without claiming destruction
   of copied keys or delivery of UC-48's future durable offline event stream.
