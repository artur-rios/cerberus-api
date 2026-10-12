# UC-33 Delete collection design

## Intent and authorization

Implement issue 34, UC-33, FR-CL-05/06 and BR-05/06/20/23: an authenticated
owner recoverably trashes one currently visible owned collection for exactly 30
days. The collection disappears from active views, every active association and
sharing grant is deactivated, and the underlying records and folders remain
active and owned exactly as before.

The UC names only the Owner actor. Read-only and read-write recipients may see a
shared collection and its effective members, but they never receive collection
deletion, membership or sharing authority. A currently visible recipient target
therefore returns `403 vault_access_denied`; hidden, revoked or unselected foreign
targets remain nonrevealing `404`.

This is a Domain, Command, persistence and HTTP addition using the existing trash
schema. The authorized all-open-issues batch replaces repeated design approval
menus with recorded self-checks, test-first implementation, task reviews, one
fresh whole-branch review and exact-head CI. Independent protocol, security and
client acceptance remain development-only deferred release gates.

## Selected approach

Use a dedicated collection trash transaction built from the current collection
authority path and the established record/folder trash machinery. The transaction
captures all stored memberships, locks and reloads the current graph, advances the
collection lifecycle once, removes active membership rows, revokes active grants,
bumps directly affected active owned resources, and creates one trash operation,
one typed entry and one retention item atomically.

Alternatives rejected:

1. Physically delete the collection. This violates the 30-day recovery window and
   would cascade away the evidence needed for a valid restore.
2. Mark only `collection.deleted_at`. This hides reads but leaves live association
   and grant rows, contradicting BR-23 and the explicit requirement that collection
   deletion revokes grants.
3. Delete grants and save their opaque wrappers in the trash snapshot. Restoration
   must not revive grants; retaining them as revoked rows preserves audit identity
   without putting credential material in the association snapshot.
4. Trash included records or folders. Collections are groupings, not ownership
   containers; FR-CL-06 expressly preserves the underlying resources.
5. Reuse per-association command handlers. Multiple transactions could expose a
   partially deleted graph or extend deadlines independently.

## Public contract

`DELETE /api/collections/{id}` requires a current Heimdall identity, exactly one
canonical `X-Cerberus-Vault-Access` header, a lowercase dashed nonzero GUID path,
no query parameters and a bounded strict UTF-8 JSON body handled by
`VaultProofBody`.

The body contains exactly required `expectedRevision`, a positive safe integer no
greater than `9007199254740991`. It cannot contain envelope, edit time, owner,
profiles, records, folders, grants, key epochs, wrappers or lifecycle overrides.
Unknown, duplicate, case-forged, missing or numeric-string properties, malformed
paths, BOM/compression violations and unsupported transport forms return
`400 validation_failed`.

Success returns `200 collection_deleted` with exactly `collectionId`,
`trashOperationId`, `revision`, `serverSequence`, `deletedAt` and `purgeAt`.
Revision becomes expected plus one; sequence is a new greater safe value; timestamps
are UTC at PostgreSQL microsecond precision and differ by exactly 720 hours. No
ciphertext, association snapshot, grant, wrapper, owner/internal ID or usable key is
returned. All responses remain no-store and the shared oversized-body response is
an empty `413`.

## Current authority and failure precedence

The actor must resolve to an active nonterminal account. The vault session must be
current, nonrevoked, issued, match positive account policy and revocation generation,
and satisfy renewal expiry at database statement time. A selected profile must
remain active, owned and nonterminal; its disappearance must never widen access.

Wide access admits an active owned collection. Selected access admits it only
through a current direct `ProfileCollection` link to the selected profile. The
target and owner must not be trashed, closing or terminally erased.

A foreign target is visible only through a current active read-only or read-write
grant and, for selected access, an exact current profile link. Before returning
`403`, validate every relevant native binding used by the target projection: current
collection identity/epoch/envelope, grant identity/revision/recipient envelope,
recipient identity and owner/recipient protection pins. Hidden or irrelevant corrupt
evidence is never inspected. Valid visible foreign access denies before stale
expected revision; malformed relevant evidence returns `503`.

Failure mapping is:

- missing identity: `401 authentication_required`;
- missing vault handle: `401 vault_access_required`;
- invalid visible request: `400 validation_failed`;
- inactive/terminal actor or absent, hidden, trashed or terminal target/owner:
  `404 not_found`;
- invalid current session/selection or valid visible foreign target:
  `403 vault_access_denied`;
- stale/exhausted revision, changed captured graph, grant revision exhaustion,
  winning concurrent state or existing typed entry: `409 revision_conflict`;
- corrupt required persisted/native metadata, database timeout/outage, internal
  cancellation or post-write verification failure: `503 persistence_unavailable`.

Caller cancellation propagates. A lost-success retry sees the trashed target as
`404` and cannot create a second operation or extend the deadline.

## Retained associations, grants and structural effects

Introduce `CollectionAssociationSnapshot(Guid[] ProfileIds, Guid[] RecordIds,
Guid[] FolderIds)` using sorted distinct public IDs only. Capture every stored
`ProfileCollection`, `CollectionRecord` and `CollectionFolder` row for the target,
including links whose profile or member is currently inactive. The restricted
snapshot contains no grant identifiers, recipient identities, ciphertext, wrappers
or keys.

Remove all target membership rows in the transaction. The profile array may include
owner and recipient-owned profiles because the row itself belonged to the deleted
collection, but UC-51 must restore only links that are still valid at restore time.
In particular, recipient profile links cannot become effective without a newly
created active grant.

Every active target grant changes from Active to Revoked and increments its safe
revision exactly once. Preserve grant public ID, recipient, access and opaque
recipient wrapper byte-for-byte. Already revoked grants remain unchanged. Do not
delete grant rows, rotate the collection key, invent replacement wrappers, revoke
recipient sessions or claim destruction of client copies. Restoration never changes
these grants back to Active; re-sharing requires the later sharing use cases.

For every distinct active directly linked owned profile, folder and record whose
membership row is removed, advance revision, server sequence and concurrency stamp
once while preserving ciphertext, edit time, ownership, parents, key metadata and
all unrelated associations. Do not bump inactive/trashed/terminal resources or
foreign recipient profiles. Recipient visibility disappears immediately through
the trashed collection and revoked grant; UC-48 remains responsible for durable
offline visibility-removal delivery.

The collection itself retains opaque envelope bytes, client edit time, key epoch and
owner. It sets `deleted_at` and `purge_at`, advances revision/sequence/stamp once and
remains in complete vault-protection rotation inventory during retention.

Create one `TrashOperation` rooted at kind `collection`, one typed `TrashEntry`
containing the association snapshot, and one `RetentionWorkItem` keyed
`trash/{operationId}` and due at `purgeAt`. A stale typed entry conflicts rather
than being overwritten. UC-51 must consume or reconcile an old entry before
re-trash; UC-53 supplies physical expiry. No restore, list or purge endpoint is
introduced here.

## Transaction and lock order

Acquire locks in this order:

1. Actor account `FOR UPDATE` and current actor session `FOR UPDATE`.
2. Selected profile plus all currently linked owned profiles, ordered by public ID,
   `FOR NO KEY UPDATE`.
3. Target collection `FOR UPDATE`.
4. Linked active owned folders, then records, each ordered by public ID,
   `FOR UPDATE`.
5. Target grants ordered by public ID `FOR UPDATE`.
6. Typed profile, folder and record association rows in deterministic key order.

Do not lock foreign accounts, recipient profiles, recipient protection rows or
unrelated content. The target collection `FOR UPDATE` excludes new foreign-key
associations while the graph is reloaded. The actor account lock serializes all
same-owner resource/membership/rotation writers. Collection-before-content and
grant ordering remain compatible with recipient content writers and future sharing
operations; actual controlled races must establish this rather than relying only
on SQL lock names.

After every potentially waiting phase, recheck statement-time account/session/
selection authority. Reload the complete target membership/grant inventory after
the target and related rows are locked. Growth, replacement or an association to an
unheld owned resource returns `409`; harmless disappearance is classified from the
fresh current graph and never reconstructed from a stale snapshot.

The guarded collection update is the authorization linearization point and repeats
current actor/session/selection/ownership/lifecycle/terminal predicates, expected
revision, prior sequence/stamp and captured inventory at statement time. A guard
miss receives a fresh error classification before falling back to `409`. All
membership deletes, parent bumps, grant revocations, trash rows and queue insertion
commit once. Any injected failure rolls back the entire graph; PostgreSQL sequence
gaps after rollback are allowed.

## Verification matrix

Main flow:

- Wide and selected owner deletion; exact six-field result and 720-hour deadline.
- Preserve collection bytes/edit time/epoch/owner and every underlying record/folder.
- Capture all memberships, remove all active association rows, bump each active
  owned linked resource once, revoke each active grant once, preserve revoked grants
  and wrappers, and create exactly one operation/entry/queue item.
- Native owner and recipient GET/list views lose the collection and its inherited
  visibility while direct noncollection access to underlying content survives.

AF-01:

- Strict path/header/query/body/JSON/transport checks, zero/unsafe revision and
  forged association/grant/lifecycle inputs; no mutation.

AF-02:

- Missing, private foreign, unselected, trashed or terminal collection/owner;
  hidden target precedes stale revision and corrupt opaque content.

AF-03:

- Missing identity/handle, revoked/expired/future/stale-policy session, invalid or
  disappearing selection; valid native RO/RW recipient target returns `403` and
  malformed relevant native evidence `503`; no scope widening.

AF-04:

- Stale/max collection revision, max active grant/resource revision, sequence
  exhaustion, existing typed entry, same-revision concurrent delete/lost retry,
  membership/grant/selection/lifecycle/protection races and final guard changes.
  Exactly one winner is preserved.

AF-05:

- Real database outage, internal versus caller cancellation, required metadata
  corruption and failures after collection write, each association family, parent
  bump, grant revocation, operation, entry and queue. Every failure rolls back and a
  clean retry succeeds.

Performance and integration:

- Explain target lookup against at least 100 unrelated collections and prove only
  captured rows are locked; exercise actual collection update/create, profile
  association, protection rotation and recipient content writer races.
- Fresh focused and whole Command/Data/Web tests, native Master/PerProfile fixtures,
  full unfiltered six-family suite with every production assembly at least 90%
  line coverage, native dependency audit, corpus/helper/spec/OpenAPI/drift/whitespace
  checks, one fresh whole-branch review and exact-head required CI.
- Delivery requires a clean mergeable PR, normal non-admin match-head squash merge,
  closed issue, Done project/README state, deleted feature branch and refreshed
  `develop` tree equal to the tested merge tree.

## Recorded design rulings

1. Collection trash revokes active grants in place, increments their revisions and
   preserves wrapper bytes; restore never reactivates them. If later sharing design
   requires physical removal, UC-37/51 must migrate the audit representation and
   restore tests before release.
2. The association snapshot records all linked profile public IDs, including foreign
   recipient profiles, but restore must revalidate ownership and a newly active
   grant rather than silently reviving access. If restore instead omits all foreign
   links, only UC-51 reconciliation changes; deletion evidence remains sufficient.
3. Active directly linked owned profiles, folders and records receive structural
   revision/sequence bumps; foreign profiles do not. If UC-48 later requires a
   recipient-profile event, it must add a dedicated visibility-removal stream rather
   than cross-owner profile mutation here.
4. Owned damaged collection ciphertext can be trashed unchanged because deletion
   neither returns nor replaces it. If future purge policy requires parsing before
   retention, that would be a new validation rule and could strand existing damaged
   ciphertext.
