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
