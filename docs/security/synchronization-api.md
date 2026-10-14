# Download synchronization changes

UC48 exposes `GET /api/sync/changes`. Send a current Heimdall human bearer token
and `X-Cerberus-Vault-Access` for a current selected profile. The profile comes
from that session; an account-wide session does not authorize synchronization.
An owner or a collection recipient receives only that profile's currently
permitted native encrypted resources and current recipient provisioning.

## Request and response

`cursor` is optional. Omit it for a fresh snapshot. `pageSize` is optional,
defaults to the smaller of 50 and configured `MaxPageSize`, and must be a positive
canonical decimal integer no larger than that configured maximum. Keep the
same size for every continuation. Unknown or duplicate query fields, duplicate
access headers and GET bodies return `400 validation_failed`. Responses use
`Cache-Control: no-store`; transport caches must not retain encrypted vault data.

The normal `DataOutput` response contains `accountId`, `profileId`, current
`policyRevision`, `revocationGeneration`, `serverTime`, `items`, `nextCursor`,
`hasMore`, `clearScope` and `resynchronizationRequired`. An unchanged completed
window returns an empty item list and another usable cursor. `nextCursor` is
explicitly null when resynchronization is required.

Each item has a positive safe-integer `sequence`, typed `resourceKind`, public
`resourceId` and `eventType`. Kinds are `profile`, `record`, `folder`, `collection`
and `grant`. IDs are paired with kind; equal UUIDs of different kinds remain
distinct. The event forms are:

| Event | Data | Client action |
| --- | --- | --- |
| `upsert` | Current typed native payload | Replace the local resource using current revision and native validation. |
| `removed` | Null; no deletion time | Remove the resource and dependent local access material from this scope. |
| `permanently-deleted` | Null; minimal `deletedAt` | Remove the known typed identity and its dependent local material permanently. |

An upsert's `data.resourceType` matches `resourceKind`. Profile data includes its
encrypted envelope, native profile key wrappers and association IDs. Record and
folder data include current encrypted envelopes. Collection data includes its
current key epoch and encrypted envelope; grant data includes current collection
ID and native recipient key envelope. Resource revisions, content sequences and
edited times retain their native meanings. Event sequences describe sync
delivery and are separate from a resource's content sequence.

Only identities already disclosed in this stream can produce removal or terminal
events. The endpoint does not enumerate another scope's tombstones. It checks
current native grant signatures, protection pins, ancestry and visibility using
the same production readers as the existing APIs. It never decrypts content.

## Cursor and client application rules

The authenticated encrypted opaque cursor binds acting identity, selected
profile, retained stream, window version, last event, boundary and page size.
Its cryptographic purpose is separate from other API cursors. Clients must store
and transmit it as an opaque value. It grants no authority and does not bind the
expiring online handle: a fresh valid unlock of the same profile can continue.

1. Reconnect, authenticate and obtain current selected-profile vault access.
2. Submit the last cursor with its original page size, or omit it for a snapshot.
3. When `clearScope` is true, discard the prior local inventory and access material
   for that account/profile before applying items. Do this only on the first
   fresh snapshot page; continuation pages carry `clearScope=false`.
4. Apply all removals before new content. A window orders removal descriptors
   before upserts, and event sequences increase across pages and new windows.
5. Apply the page and persist its cursor together locally. Continue while
   `hasMore` is true; retain the final cursor for the next reconnect/delta.
6. On a typed `409 resynchronization_required`, clear the scope, discard the
   unusable cursor and request a fresh authorized snapshot. This response has
   empty items, null cursor and both clear/resynchronization flags true.

Malformed, tampered, wrong-actor/profile, missing, expired, superseded or
incompatible cursors require explicit resynchronization after current authority
is checked. A membership/permission change during an unfinished or replayed
window also requires clearing the scope before further disclosure. Current
authority failure returns its denial instead of replaying a saved page.

Replay within the retained current window rechecks authority and rereads current
payloads. This is net-change synchronization, not an audit log or frozen history:
edits coalesce and ciphertext from an earlier revision is never replayed. A
content edit after one page appears through current payload rereads or a later
delta. The caller must independently validate native cryptographic bindings and
the current offline lease before allowing local use.

## Persistence, concurrency and retention

`sync_stream`, `sync_inventory` and `sync_change` persist checkpoint metadata,
typed disclosed IDs, visibility/terminal flags, 32-byte fingerprints and minimal
event descriptors. They contain no ciphertext, key wrapper, plaintext, vault
access handle or private key. Removed IDs remain known until stream expiry so
a later permanent deletion can still produce a minimal terminal event.

The transaction locks the acting and associated owner accounts in canonical
public-ID order, then the current session, profile and retained stream. It also
locks **all collections of those captured owners** in canonical public-ID order.
Shared recipient writers acquire collection locks before editing content, so
this covers alternate collection routes and directly selected owned resources.
Authority is rechecked after lock waits and at final database statement time.
This uses one connection and a consistent inventory, at the cost of serializing
shared edits across those owners' collections while the inventory is scanned.

Each page compares the native current inventory against minimal previously
disclosed metadata and delivers a bounded page of descriptors. Inventory work
scales with the selected profile's visible content even when response page size
is small. Newly generated descriptor sequences must advance beyond the previous
boundary. Dependency faults, sequence corruption, cancellation or lost authority
roll back checkpoint and inventory changes together; PostgreSQL sequences may
have gaps after rollback, which carry no resource disclosure.

`CERBERUS_SYNC_RETENTION` gives a stream a fixed lifetime at creation. Continuing
does not extend it. An encountered expired stream is deleted with its inventory
and descriptors and returns explicit resynchronization. Account/profile deletion
cascades remove their private synchronization state. UC53 is responsible for
scheduled cleanup of abandoned expired streams. Retention does not recall copies
held by disconnected clients; those clients must enforce their issued lease and
clear stale data on expiry/reconnection under the offline contract.

## Errors and qualification

| Status | Stable error / meaning |
| --- | --- |
| 400 | `validation_failed` for visible request shape or invalid native input. |
| 401 | `authentication_required` or `vault_access_required`. |
| 403 | `vault_access_denied` for invalid/revoked/stale/expired or account-wide access. |
| 404 | `not_found` for absent or inactive acting account; inaccessible list targets are omitted. |
| 409 | Typed `resynchronization_required`, or non-content `revision_conflict` after a captured-state race. |
| 503 | `identity_unavailable` or `persistence_unavailable`; no content/cursor is returned. |

This delivers the server-side UC48 contract. Independent protocol/security and
external native-client storage/clock/reconnection qualification remain release
requirements. Heimdall application identity authentication remains separately
blocked by [Heimdall #178](https://github.com/artur-rios/heimdall-api/issues/178).
