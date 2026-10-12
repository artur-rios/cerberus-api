# UC-32 Update collection design

## Intent and authorization

Implement issue 33, UC-32 and FR-CL-04: an authenticated owner replaces one active owned collection's opaque encrypted envelope using an expected revision. The update advances the collection revision and synchronization sequence but does not change ownership, key epoch, selected-profile links, content membership, grants, recipient wrappers or lifecycle state.

UC-32 names only the Owner actor. The authorization matrix permits read/write recipients to edit shared records and folders, while ownership and grouping changes remain owner-controlled. A collection envelope is the collection resource itself, not included content. Consequently, an active read-only or read/write recipient can see the target but receives `403 vault_access_denied`; recipient access never authorizes this endpoint.

This adds Domain, Command, persistence and HTTP contracts. The authorized all-open-issues batch replaces repeated design and plan approvals with recorded self-checks, test-first implementation, fresh verification, one ordinary whole-branch review and exact-head CI. Independent protocol, formal security and client acceptance remain development-only deferred; release gates remain unchanged.

## Selected approach

Use a dedicated collection update contract and a purpose-built transactional store modeled on the established record and folder update flows. Resolve one target under current vault access, distinguish hidden from visible-but-denied foreign access, lock only the actor's authority rows and the target collection, reload authority after waits, then perform one guarded optimistic update.

Alternatives rejected:

1. Reuse the record/folder writer and treat read/write grants as collection write permission. This contradicts the UC actor and would let a recipient alter owner-controlled grouping metadata.
2. Reject every foreign ID as `404`. A currently visible shared collection is not hidden; returning `403` preserves the established visible-but-insufficient-permission distinction.
3. Validate authorization in one read and issue an unguarded update. Session expiry, selection changes, owner lifecycle changes and concurrent edits could otherwise become stale.
4. Rewrite or rotate grants when opaque collection bytes change. Native grant binding covers collection identity and key epoch, not ciphertext bytes; unchanged epoch means existing grants remain valid. Rewriting them would invent client key material and broaden UC-32.

## Public contract

`PUT /api/collections/{id}` requires a current Heimdall identity, exactly one canonical `X-Cerberus-Vault-Access` header, a lowercase dashed nonzero GUID path, no query parameters and a bounded strict UTF-8 JSON body handled by `VaultProofBody`.

The body contains exactly the required properties:

- `expectedRevision`: positive safe integer, at most `9007199254740991`.
- `envelope`: a strict supported `cerberus-content-v1` encrypted envelope.
- `editedAt`: UTC, representable, at least ten ticks, normalized down to PostgreSQL microsecond precision before persistence.

Unknown, duplicate, case-forged or missing properties, numeric strings, malformed identifiers, BOM/compression violations and unsupported envelope shapes return `400 validation_failed`. The body cannot specify collection ID, owner, key epoch separately, profiles, records, folders, grants or wrappers.

The replacement must retain the stored key epoch. When the envelope differs from the stored value, both `keySalt` and `nonce` must be fresh. An identical envelope is allowed as a metadata edit. Revision, not client timestamp ordering, determines concurrency; an older or equal valid `editedAt` is accepted.

Success returns `200 collection_updated` with exactly `collectionId`, `revision`, `serverSequence` and normalized `editedAt`. The target ID must match the request, revision becomes expected plus one, and server sequence is a new greater safe integer. No ciphertext, internal ID, ownership, association, grant, pin or usable key data is returned. All success and failure responses remain no-store; the common oversized request response is an empty `413`.

## Current authority and error precedence

The actor must resolve to an active, nonterminal account. The vault session must be current, nonrevoked, issued, have positive policy and revocation generations equal to the account, and satisfy the renewal expiry rules. A selected profile must remain active, owned by the actor and nonterminal. A disappearing selected profile must never widen access to account-wide scope.

Wide access admits any active owned collection. Selected access admits only an active owned collection directly linked by `ProfileCollection` to the selected profile. The target and owner must not be deleted, closing or terminally erased.

A foreign collection is visible only through a current active read-only or read/write grant and, under selected access, an exact `ProfileCollection` link. Before returning `403`, the store validates the one relevant grant against the current collection identity/epoch/envelope, grant identity/revision, recipient identity, and current owner/recipient protection pins. This prevents malformed server-side authority evidence from being treated as valid visibility. A hidden, revoked, unselected or absent foreign target returns `404` without inspecting irrelevant evidence. Corrupt relevant native evidence returns `503`; a valid visible foreign target returns `403` regardless of grant access level or expected revision.

Error ordering is:

- Missing identity: `401 authentication_required`.
- Missing vault handle: `401 vault_access_required`.
- Invalid visible request input: `400 validation_failed`.
- Inactive or terminal actor, absent or hidden target/owner: `404 not_found`.
- Invalid current session/selection or valid visible foreign target: `403 vault_access_denied`.
- Stale or exhausted expected revision, winning concurrent edit, newly unheld authority, or exhausted global sequence: `409 revision_conflict`.
- Required persisted metadata/native evidence corruption, database timeout/outage, internal cancellation or post-write verification failure: `503 persistence_unavailable`.

Caller cancellation propagates. Every failure leaves collection bytes, metadata, memberships, grants and wrappers unchanged.

## Stored validation and mutation

For an owned target, validate only the target collection's persisted revision, server sequence, UTC microsecond timestamp, key epoch and strict envelope. Unrelated collection ciphertext, grants and protection rows must not affect the owner update. Revision and sequence must be positive safe integers; edited time must be finite UTC within the supported range; stored envelope must be strict and agree with `collection.key_epoch`.

The successful update changes only:

- `envelope` to the replacement bytes;
- `edited_at` to the normalized client time;
- `revision` by exactly one;
- `server_sequence` to the next global value;
- `concurrency_stamp` to a fresh value.

It preserves account ownership, public ID, key epoch, deletion and purge fields, all `ProfileCollection`, `CollectionRecord` and `CollectionFolder` rows, all grants and recipient wrappers, and all related profile/folder/record metadata. Collection update does not imply key rotation or membership replacement.

## Transaction and concurrency

Acquire locks in this order:

1. Actor account `FOR NO KEY UPDATE`.
2. Current actor vault session `FOR UPDATE`.
3. Current selected actor profile `FOR UPDATE`, when present.
4. Target collection `FOR NO KEY UPDATE` only when initial admission resolves it.

Do not lock foreign accounts/profiles/protection rows or unrelated collections. A foreign denial needs no write lock on the owner graph; its visibility/native evidence is captured in one current statement. Owner updates do not inspect or lock grants because grant validity is independent of collection ciphertext while key epoch is fixed.

After any lock wait, reload the actor/session/selection/target state. Validate stored target metadata and expected revision. The guarded `UPDATE` must reassert current actor account, session, selected-profile link, owner lifecycle, target lifecycle, expected revision, previous sequence, original envelope and unchanged key epoch at statement time. A committed access/lifecycle change takes precedence through a fresh classification; a guard miss that remains authorized is `409`.

The database allocates a new global server sequence and fresh concurrency stamp. Reload and verify the exact resulting metadata before committing. A post-write fault or failed verification rolls back all row changes; PostgreSQL sequence gaps are allowed. Two same-revision updates have exactly one winner, and a lost-success retry with the old revision returns `409` while preserving the winner.

Protection rotation and update serialize through the owner's account lock. Collection creation/deletion/membership/grant operations must continue their established account/collection order. UC-32 adds no foreign lock cycle and no schema, migration, package or cryptographic algorithm change.

## Verification matrix

Main flow:

- Wide and selected-profile owner updates; exact four-field result; identical and changed envelopes; older/equal/new timestamps and microsecond normalization.
- Preserve owner, key epoch, profiles, records, folders, grants, recipient wrappers and related resource metadata.

AF-01:

- Strict route/header/query/body/JSON/envelope handling; zero/unsafe revision; non-UTC/minimum time; epoch change; reused salt or nonce; forged relationship/owner fields; no mutation.

AF-02:

- Absent, other-owner ungranted, unselected, deleted/terminal target or owner, revoked/terminal grant; hidden target remains `404` before stale revision or opaque corruption disclosure.

AF-03:

- Missing identity/handle; revoked, expired, future-issued or stale-policy session; invalid/disappearing selection; valid foreign read-only and read/write targets return `403`; no selected-profile widening.

AF-04:

- Stale/max revision, concurrent same-revision edits, lost-response retry, target deletion/terminal/selection removal/session expiry/protection rotation during waits, final guard race and global sequence exhaustion; winner preserved.

AF-05:

- Required owned metadata/envelope corruption, relevant foreign grant/envelope/pins corruption, database outage/internal cancellation and injected post-update failure; transaction rollback and retry safety; caller cancellation propagation.

Performance and integration:

- Explain the target lookup against at least 100 unrelated collections and prove one target lock with no unrelated or foreign authority locks.
- Fresh focused and whole Command/Data/Web tests, actual native collection fixtures, full unfiltered six-family suite with every production assembly at least 90% line coverage, native dependency audit, corpus and helper suites, spec/OpenAPI/drift checks and whitespace checks.
- One fresh whole-branch review includes all recorded rulings. Delivery requires exact-head `branch-policy`, `test` and `docker` success, a clean mergeable PR, normal non-admin squash merge, closed issue, Done project/README state, deleted feature branch and tested tree equal to refreshed `develop`.

## Recorded design rulings

1. Collection envelope updates are owner-only even for active read/write recipients. If this interpretation is wrong, a follow-up must deliberately introduce recipient write authorization and its races; no current data is lost.
2. A visible foreign target's single relevant native grant is verified before the owner-only `403`. If this precedence is wrong, a small store/error-order correction is required; mutation semantics remain unchanged.
3. Owner updates do not inspect or rewrite grants because native grant binding excludes collection ciphertext and key epoch cannot change here. If later protocol review binds ciphertext bytes, UC-32 must be expanded into a coordinated client-supplied grant replacement operation before release.

