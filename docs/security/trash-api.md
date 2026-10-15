# Recoverable trash

`GET /api/trash` lists the authenticated owner's currently recoverable deletion
operations. Supply a current human bearer and one `X-Cerberus-Vault-Access` handle.
The endpoint never decrypts content or returns another account's trash, including
content previously available through a shared collection.

Each item represents **one deletion operation's root**. A deleted folder represents
its retained cascade once; its children are not independent restore requests.
An item deleted separately before that cascade remains a separate operation with
its original deadline. Restoration is implemented separately by UC51.

The response is `DataOutput<TrashListOutput>` with `items` and nullable
`nextCursor`. Each item has `kind` (`record`, `folder`, `collection`, `profile`),
`operationId`, `resourceId`, `revision`, `serverSequence`, `editedAt`, `deletedAt`,
`purgeAt` and the native encrypted `envelope`. Collection items also carry
`keyEpoch`; profile items carry encrypted `keyWrappers` for authorized local use.
No retained association IDs, cascade inventory, plaintext, usable key, vault
access handle or account protection/recovery material is included.

The deadline is the original deletion time plus exactly 720 hours. At or after
that deadline, or after a typed terminal erasure, an operation is omitted even
if a purge worker has not yet removed its rows. Listing never extends retention
or changes resource revisions, sessions, operations, associations or work queues.

Account-wide handles can list all owned recoverable roots. Selected-profile
handles retain their scope: a root must have originally linked directly to the
selected profile, originally linked to a currently active owned collection still
selected by that profile, or have a former parent with complete currently active
owned ancestry accessible through the selected profile/folders/collections.
Direct retained profile/collection authority does not require a surviving former
parent. An inactive former container creates no additional historical access.
Trashed profile roots require account-wide access. Missing or unusable retained
scope evidence is omitted for selected handles; it cannot widen the handle.
This follows the existing retained-record authority used for permanent deletion.

Only exact `pageSize` and `cursor` query names are accepted, once each. The default
page size is the lesser of 50 and configured `MaxPageSize`; valid requested sizes
are positive integers up to that configured maximum. The opaque authenticated
cursor binds identity, vault handle, page size and a trash-specific purpose.
It bounds newly created deletion operations while rechecking current authority,
expiry and scope on every page. It is not a fixed multi-request snapshot or a
synchronization cursor. Permission loss can shorten a subsequent page. Replicas
must share the durable `RegistrationFingerprintKey`; rotating it invalidates old
cursors with 400, and clients restart listing.

GET bodies, unknown query arguments, duplicate headers/queries, noncanonical page
sizes and caller-supplied authority override headers are rejected with 400
`validation_failed`. Missing/invalid identity returns 401, unavailable current
identity validation 503, absent owner 404 and invalid current vault authority 403.
Required visible stored/native corruption or a persistence outage returns 503
`persistence_unavailable` without a partial page. Preauthentication request-size
rejection returns an empty 413. Every response is no-store; conditional cache
headers never bypass current checks.

The store takes own account SHARE then own session SHARE, serializing owner
lifecycle/topology changes while allowing concurrent readers and foreign-key
checks. There is no identity-service call under those locks. Strict retained JSON
parsing precedes a final PostgreSQL statement that captures current authority,
visibility, exclusive database-time deadlines, typed terminal exclusions, bounded
root ciphertext and current profile signature pins together. No stored JSON is
cast into UUID/JSON SQL types. Profile master wrappers are verified against current
independent owner pins. Resource kind remains part of every terminal lookup.

Selected listing scans the owner's retained root association metadata to determine
scope; database work and lock duration can grow with the retained inventory even
though response ciphertext is page-bounded. Account-wide listing does not parse
those snapshots. Capacity and production workload qualification remain operator
work. Independent protocol/client release qualification remains separate from
this development implementation.

## Restore one deletion operation (UC51)

POST `/api/trash/{id}/restore` with the deletion **operationId** from the list,
a current human bearer, one current vault handle, and the strict JSON body
`{"expectedRevision":2}`. The revision is the listed root's revision. The route
uses canonical lower-case UUIDs; queries, authority overrides, unknown or duplicate
body fields, quoted numbers and unsupported media are rejected with 400. Size
rejection is an empty 413 before authentication. Responses are always no-store.

A successful 200 `trash_restored` response has `operationId`, `rootKind`,
`rootResourceId`, `restoredAt`, `items` and `omissions`. Each item contains `kind`,
`resourceId`, `revision`, `serverSequence` and `editedAt`. Each omission contains
an authorized restored `resourceKind`/`resourceId`, `associationKind` and positive
`count`; unavailable target identifiers are never exposed. Association kinds are
`parent`, `profile`, `collection`, `record`, `folder` and `resource` (missing or
terminal members of the chosen folder operation). The response contains metadata;
clients use the normal authorized reads to obtain encrypted content.

Restoration preserves every surviving member's native encrypted bytes, encrypted
profile wrappers, client edit time, identity and ownership. Each restored row
advances revision and server sequence once. Structural parent metadata advances
once where its relationship changes, matching the corresponding trash operation.
Profile restoration preserves the existing profile-association semantics and
advances the profile itself. Independent earlier deletions retain their separate
operation and deadline. Physically missing or typed terminal cascade members are
omitted. An active or changed retained member causes 409 and a rollback.

Former links are checked against current active ownership and visibility. A
missing, hidden or unavailable former parent leaves the item at the root. Current
selected-profile access admits only its retained original scope; restored direct
profile/collection links must still be permitted by that selection. Folder and
collection roots admit only their original owned unit. Profile roots require an
account-wide handle. Profile links to foreign collections require a currently
valid signed native grant and independent current author/recipient pins. Missing
or revoked grants produce omissions; corrupt required native evidence returns
503. No session, selection, grant or terminal marker is reactivated or removed.
In particular, a session revoked when its profile was trashed stays revoked with
its original ProfileId after restoration.

The original exclusive 720-hour deadline is checked on the PostgreSQL clock,
including after lock waits. Visible owned expired operations return 410
`trash_expired`, even before physical cleanup. Missing, foreign, hidden or terminal
roots return 404 `not_found` before revision, native metadata or expiry disclosure.
Stale/exhausted revisions return 409 `revision_conflict`; invalid current vault
authority returns 403; required persistence/native corruption returns 503 with no
partial result. Identity failures follow the listing contract above.

Restoration locks own account, own session, the chosen retention job, operation
and entries, then ordered own profiles, collections, folders and records. Other
root kinds take strong own collection locks before owned content, serializing
shared writers. Profile restoration uses collection SHARE locks, including foreign
collections, without taking foreign account/profile locks; reciprocal restores do
not acquire strong own collection locks before foreign SHARE locks. A guarded
root mutation after all waits admits the transaction only while current session
and retention authority are valid. Natural expiry after that admission may finish
the held cascade atomically. Required member evidence and typed terminal checks
still apply. Failures or caller cancellation roll back resources, parents, links,
entries and job together; global sequence gaps may remain.

Only the chosen operation/entries/job are consumed. An old worker claim cannot
complete the removed job. A lost-success retry returns 404; clients reload normal
resource/trash state. Trashing a restored item again creates a new deletion
operation with a new original deadline. Emptying and scheduled physical expiry
remain UC52 and UC53 responsibilities; restoration never purges content.
