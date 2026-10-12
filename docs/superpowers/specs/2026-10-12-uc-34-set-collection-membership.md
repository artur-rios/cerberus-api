# UC-34 Set collection membership design

## Intent and traceability

Implement issue #35, UC-34 and FR-CL-07 through FR-CL-09. A current collection
owner atomically replaces the direct record and folder members of one active owned
collection. Records and folders remain independently owned and may stay linked to
other collections. Direct member folders continue to expose their active descendants
through the existing dynamic read model. Recipients never obtain membership
administration rights.

The operation preserves BR-06, BR-27 and BR-28, the current selected-profile
boundary, typed resource identity, native grant validation and end-to-end
encryption. It introduces no schema, dependency or cryptographic algorithm.
Independent protocol/security and external-client qualification remain release
gates.

## Chosen approach

Use a dedicated strict command and guarded PostgreSQL transaction. Reusing profile
association replacement would admit foreign collections and mutates the wrong
aggregate. Composing individual link inserts/deletes cannot provide one expected
revision, one encrypted key-manifest replacement or all-or-nothing recipient
visibility. A dedicated collection-membership aggregate is therefore required.

Membership changes are accompanied by a replacement collection envelope at the
same key epoch. The opaque encrypted payload is the client boundary for any member
key manifest or wrapped-key changes required by the client protocol. The server
validates shape, epoch and nonce/salt freshness but never decrypts or claims that
the client payload contains usable keys. Existing active collection-grant wrappers
remain unchanged because the collection root epoch and grant revision do not
change; every active grant is nevertheless revalidated before its effective scope
can change.

## HTTP and application contract

`PUT /api/collections/{id}/members` requires current Heimdall identity, exactly one
`X-Cerberus-Vault-Access` header, a canonical lowercase dashed nonzero collection
UUID, no query parameters, and bounded strict UTF-8 `application/json` with no BOM,
compression, duplicate, unknown or case-forged fields. Every response is
`Cache-Control: no-store`; the shared oversized response is an empty 413.

The body has exactly five required fields:

- `expectedRevision`: positive safe integer at most `9007199254740991`;
- `envelope`: strict `cerberus-content-v1` envelope retaining the stored key epoch;
- `editedAt`: nondefault UTC time, floored to PostgreSQL microseconds;
- `recordIds`: nonnull distinct canonical nonzero record UUIDs;
- `folderIds`: nonnull distinct canonical nonzero folder UUIDs.

The same UUID may occur in both typed arrays. If the envelope changes, both
`keySalt` and `nonce` must differ from the stored envelope. An unchanged envelope
and unchanged membership is permitted as an expected-revision-controlled metadata
operation. Plaintext, profiles, owner, grants, recipient wrappers, epoch overrides,
access context and per-member revisions are rejected.

Success is 200 `collection_membership_set` with exactly `collectionId`, `revision`,
`serverSequence`, normalized `editedAt`, sorted `recordIds` and sorted `folderIds`.
The collection revision advances exactly once and the sequence is a greater safe
integer. No envelope, internal identifier, owner, grant, protection material,
wrapper or usable key is returned.

Domain contracts:

`CollectionMembershipInput(long ExpectedRevision, EncryptedEnvelope Envelope,
DateTimeOffset EditedAt, Guid[] RecordIds, Guid[] FolderIds)`;
`CollectionMembershipRequest(Guid Actor, string AccessVerifier, Guid CollectionId,
CollectionMembershipInput Input)`; `CollectionMembershipDetails(Guid CollectionId,
long Revision, long ServerSequence, DateTimeOffset EditedAt, Guid[] RecordIds,
Guid[] FolderIds)`; and `ICollectionMembershipStore.SetAsync(...)`.

## Authority and relationship admission

The actor must map to an active nonterminal account and a current unrevoked vault
session with current positive policy and generation, valid database-time lifetime,
and a current active selected profile when selected. A selection is never cleared
or widened.

Account-wide access admits any active owned collection and any active owned record
or folder with complete active same-owner ancestry. Selected access additionally
requires an exact current `ProfileCollection` link to the target. Every requested
member must already be visible through that selected profile's current record or
folder scope. This includes visibility contributed by the target collection, so a
currently visible effective member may be retained or promoted to a direct link,
but an unrelated private item cannot be introduced. Record-only visibility never
admits its containing folder. The resulting set therefore cannot expand the
selected handle's pre-operation effective visibility.

Every requested direct member belongs to the collection owner. Missing, foreign,
trashed, terminal, incomplete-ancestry or out-of-selection targets return a
nonrevealing 404. A relevant fully active ancestry cycle is persistence corruption
and returns 503; unrelated branches are not traversed.

Membership administration is owner-only. A currently visible native read-only or
read/write recipient receives 403 only after the relevant signed grant, collection
epoch/envelope, owner/recipient identity and protection pins validate. Hidden,
revoked or unselected foreign targets return 404 without inspecting irrelevant
evidence or stale revisions.

## Native grants and effective visibility

Before changing an owned collection's effective content, validate every current
active grant on that collection against its immutable owner/collection/recipient
binding, exact epoch and grant revision, current owner author pin and recipient key
pin. A missing or malformed required wrapper or pin returns 503 without mutation.
Revoked grants and unrelated collections do not contribute.

The replacement retains the collection key epoch and all grant rows byte-for-byte.
The new opaque collection envelope is the only client-supplied wrapped-key payload
at this boundary. Existing recipient wrappers remain valid for the unchanged
collection root. The API does not generate keys or certify decryptability; native
HTTP evidence proves only supported binding and current server authorization.

Direct record/folder rows are replaced. Active descendants of a direct member
folder become or remain effective members dynamically; they are not inserted as
fabricated direct rows. An explicitly linked record remains direct even when it is
also below a linked folder. Current GET/list calls observe additions and removals
immediately. Recipient-held plaintext or keys cannot be recalled. UC-48 remains
responsible for durable offline visibility-removal events.

## Atomic storage and concurrency

Capture the target, old direct links, requested members with complete ancestry,
selected visibility routes, and all active grant/native evidence. Lock in the
established order: own account `UPDATE`, own session `UPDATE`, selected owned
profile `NO KEY UPDATE`, target collection `UPDATE`, active target grants `SHARE`,
the sorted union of old and requested folders then records `UPDATE`, and existing
typed association rows in deterministic order. No foreign account/profile,
unrelated content or upward ancestor row is explicitly locked.

Reload authority and the complete captured graph after every waiting phase. New
unheld members, links, grants or changed current native evidence require 409 and a
reload rather than late lock acquisition. Current permission or lifecycle loss
wins over stale revision. The final guarded collection update repeats current
identity/session/selection/ownership/lifecycle, expected revision, original
envelope/sequence/epoch and exact captured inventory predicates at database time.

Replace the envelope/edit time, advance the collection revision/sequence/stamp,
delete old `CollectionRecord` and `CollectionFolder` rows, and insert the requested
sets in the same transaction. Every distinct active owned record or folder in the
symmetric difference advances structural revision/sequence/stamp exactly once.
Retained direct members, inherited descendants, ancestors, profiles, other
collections, revoked or active grants, protection material, parent links,
ciphertext and client edit times remain unchanged. An exact membership replacement
therefore advances only the collection.

Validate all changed counters and timestamps, active null-purge consistency and
the stored target envelope. Exhausted collection or changed-member revision and
global sequence capacity return 409. Corrupt required metadata or regressed
post-write allocation returns 503. Count every delete/insert/update stage and
verify the committed projection. A fault after any write rolls back the envelope,
links and structural counters. Sequence gaps after rollback are acceptable.

## Error and verification mapping

- AF-01: malformed route/header/body/envelope/time/typed IDs and unsupported media
  return 400; oversized input returns empty 413; no mutation occurs.
- AF-02/AF-06: hidden, missing, foreign-owned, inactive, incomplete-ancestry or
  out-of-selection members return nonrevealing 404.
- AF-03: missing identity/access returns 401; current session/selection denial and
  valid visible foreign ownership return 403.
- AF-04: stale/exhausted revision, concurrent winner, changed captured graph/native
  evidence or newly required unheld scope returns 409 and preserves the winner.
- AF-05: unavailable persistence and required structural/native corruption return
  503; caller cancellation propagates and internal cancellation maps safely.

Tests must cover wide and selected owners, typed overlap, multiple collections,
direct/inherited member visibility, exact graph preservation, every active grant,
foreign RO/RW denial, current authority precedence, writer races in both relevant
orders, stage faults, clean retry, query shape, HTTP strictness, native rotation
compatibility and complete fresh verification. Before UC-35 begins, audit the
implicit recipient-account foreign-key lock interaction called out by UC-29.

## Design rulings

1. Require a same-epoch replacement collection envelope with membership. This is
   the only current opaque boundary capable of carrying client-managed member key
   metadata atomically. If future protocol review separates that manifest, migrate
   this input without weakening transactionality.
2. Preserve active grant rows and wrappers, but validate all of them. The wrapper
   binds the unchanged collection root, not the membership revision. If protocol
   review binds membership hashes, add explicit replacement wrappers and revisions.
3. Bump only direct records/folders in the symmetric difference. This reports
   structural membership changes without fabricating descendant relationships. If
   synchronization needs descendant events, UC-48 adds dedicated visibility events.
4. Permit selected access to retain or promote members already visible through the
   target collection, while forbidding any new effective scope. Requiring an
   independent route would prevent legitimate narrowing and direct-link promotion.

