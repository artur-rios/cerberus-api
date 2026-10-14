# Upload offline edits

UC49 exposes `POST /api/sync/edits`. Send a current Heimdall human bearer token,
`X-Cerberus-Vault-Access`, and a strict UTF-8 `application/json` body. Current
account-wide or selected-profile vault authority determines the permitted scope.
An offline lease does not authorize this online upload. Reauthenticate and unlock
before reconnecting; every edit and replay checks current native permissions.

## Request

The body contains exactly `edits`, a nonempty array bounded by configured
`MaxPageSize` and the aggregate HTTP body limit. Each item contains exactly an
`editType` discriminator, a unique nonempty stable `operationId`, and `input`.
Use canonical lowercase GUIDs and UTC edited timestamps. Unknown/missing fields,
duplicate JSON properties or operation IDs, numeric strings, duplicate access
headers, query parameters, other media types and compression are rejected.

| editType | input |
| --- | --- |
| `record-create` | Native record creation fields: record ID, envelope, edited time, profile IDs and optional folder ID. |
| `folder-create` | Native folder creation fields: folder ID, envelope, edited time, profile IDs and optional parent folder ID. |
| `collection-create` | Native collection creation fields: collection ID, envelope, edited time and profile/record/folder IDs. |
| `profile-create` | Native profile creation fields: profile ID, envelope, key wrappers, edited time and record/folder/collection IDs. |
| `record-update`, `folder-update`, `collection-update`, `profile-update` | Exactly `resourceId`, `envelope`, `editedAt`. |

Create payloads reuse the [record](record-api.md), [folder](folder-api.md),
[collection](collection-api.md) and profile APIs' cryptographic and association
rules. Client-generated resource IDs never overwrite an existing resource.
Selected-profile creates must remain inside that selection. Collection recipients
can update currently writable existing records/folders; they cannot create in a
foreign account or change its profiles, collections, sharing or key policy.
Parent/member changes, deletion, key rotation and sharing use their existing APIs.

Updates replace encrypted content only. They carry no expected revision: the
server compares their edited time against the current locked native resource.
Online update APIs retain their strict expected-revision behavior. Native envelope
format, epoch, nonce/salt reuse, grant signatures, protection pins and ancestry
checks still apply. The server neither decrypts content nor validates its meaning.

## Ordering and outcomes

Normalize submitted UTC times to PostgreSQL microseconds. An older native-valid
update is `superseded`; a newer one is `applied`. Equal timestamps are applied in
serialized server order and receive strictly advancing persisted native content
sequences. Client time is a literal ordering input, not proof of when a person
edited content. A far-future timestamp can therefore supersede later real-world
edits with lower submitted times. Clients must implement their clock policy and
use winning metadata when reconciling conflicts.

The normal `DataOutput` response contains `accountId`, nullable selected
`profileId`, `serverTime`, and `outcomes` in submitted order. Every outcome has
`operationId`, `resourceKind`, `resourceId`, and `status`:

| Status | Winning metadata | Meaning |
| --- | --- | --- |
| `applied` | revision, serverSequence, editedAt | This operation committed its native write. |
| `superseded` | revision, serverSequence, editedAt | Current visible content wins an older update or a create collision. |
| `denied` | All three explicitly null | Hidden, missing or currently unwritable target; no existence distinction. |
| `permanently-deleted` | All three explicitly null | A known typed identifier was permanently erased and cannot be recreated. |

Permanent status requires prior scoped disclosed inventory or a prior successful
scoped operation. Unknown terminal identifiers receive generic denial. Resource
kind is part of identity; equal UUIDs for different kinds remain distinct.
Responses contain no envelopes, wrappers or ciphertext. Fetch current content
through [synchronization](synchronization-api.md). All responses are `no-store`.

## Stable operations and transactions

Keep the same operation ID and exact payload/scope when retrying a lost response.
The metadata-only `sync_operation` ledger is unique per acting account and stores
scope, a SHA-256 canonical request fingerprint, typed target/action and minimal
outcome metadata. It stores no ciphertext, key wrapper, plaintext or access token.
Changed reuse returns `409 operation_conflict`, rolling back the entire batch.
Replay never allocates another content write/sequence and preserves the original
accepted outcome while current write authority remains valid. A current denial
or known permanent erasure takes precedence over historical winning metadata.
An originally denied operation remains denied unless known terminal erasure takes
precedence; submit a new operation after a
meaningful permission/input change.

Canonical exclusive acting/associated-owner account locks serialize operation
IDs and multi-resource shared batches, even when recipients submit opposite item
orders. Native mutations run on the batch connection with savepoints. The
transaction locks all captured
owners' collections in canonical public-ID order before any native target lock.
This serializes standalone online recipient writers before a batch can retain
record/folder locks and then edit a collection. Safe item denials can coexist with
accepted edits; dependency faults, invalid native input,
conflicting operation reuse, sequence corruption or caller cancellation roll back
all resource writes and new ledger rows. Authority is checked after waits and
again at final database statement time. Sequence gaps after rollback are normal.
This trades some parallelism across associated owners for consistent batches.

`SyncRetention` expires download streams, not stable upload operation IDs. Upload
metadata remains throughout the active account's lifetime and cascades on account
erasure. Removing it early would silently permit duplicate operations and is not
the UC53 expired-stream cleanup policy. Old scoped metadata grants no new access.

## Errors and qualification

| HTTP status | Stable error |
| --- | --- |
| 400 | `validation_failed` for strict request/native validation. |
| 401 | `authentication_required` or `vault_access_required`. |
| 403 | `vault_access_denied` for stale/revoked/expired current vault authority. |
| 404 | `not_found` for an absent/inactive acting account. |
| 409 | `operation_conflict` or `revision_conflict`; batch writes roll back. |
| 413 | Empty response when the aggregate body limit is exceeded. |
| 503 | `identity_unavailable` or `persistence_unavailable`; batch writes roll back. |

Independent protocol/security review and external client storage, clock and
reconnection qualification remain release requirements. Heimdall application
identity retrieval remains blocked on
[Heimdall #178](https://github.com/artur-rios/heimdall-api/issues/178).
