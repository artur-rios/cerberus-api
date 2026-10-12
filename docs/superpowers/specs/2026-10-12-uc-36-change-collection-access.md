# UC-36 Change collection access design

Binding design: UC-35 PR #97 is merged. Local and remote `develop` point to
`e9f58936eab3723ad82dfe06f44c7667585bb284`, whose tree exactly matches the
reviewed feature tree. Issue #37 is In Progress on its dedicated branch.

## Intent and scope

Implement issue #37, UC-36 main flow and AF-01 through AF-05, FR-SH-04 and
FR-SH-05, and the applicable BR-10, BR-27 and BR-28 rules. An authenticated
collection owner changes one current active recipient grant from read-only to
read-write or from read-write to read-only. The grant keeps its public identity,
collection and recipient, advances its revision once, replaces its native
recipient envelope and invalidates the recipient's existing vault handles.

Add dedicated change contracts, command/handler/validator, guarded PostgreSQL
store, HTTP route, tests, OpenAPI and operational documentation. Reuse the
existing account, collection, grant, protection and session schema; add no
migration, dependency, plaintext processing, recipient-key discovery, revocation
endpoint, profile association or offline change stream. UC-37 owns grant
revocation and UC-48 owns durable offline permission delivery.

## Chosen public contract

`PUT /api/collections/{id}/grants/{grantId}` requires current Heimdall
authentication, exactly one canonical `X-Cerberus-Vault-Access` handle, no query,
canonical lowercase dashed nonzero collection and grant UUIDs, and bounded strict
UTF-8 JSON. The parsed media type is exactly `application/json`
case-insensitively, optionally with a valid UTF-8 charset. Compression, BOMs,
invalid UTF-8, duplicate, unknown, case-forged or missing fields, and other JSON
media types are rejected. All responses are no-store and oversized input is an
empty 413.

The body has exactly three required properties:

- `expectedRevision`: the current positive safe grant revision, strictly below
  `9007199254740991`.
- `access`: exactly `read-only` or `read-write` and different from the current
  active grant access.
- `keyEnvelope`: strict `cerberus-recipient-wrap-v1` material binding the path
  grant ID at `expectedRevision + 1`.

The replacement envelope is mandatory. A grant revision is part of the native
signature, so changing permission without a newly signed envelope would make the
persisted authority internally inconsistent. It binds the current owner account
and author pin, resource kind `collection`, path collection ID and current key
epoch, path grant ID, next revision, and the current recipient Heimdall identity
and recipient pin. Shape-valid but incorrectly bound, stale-key, wrong-signature
or wrong-pin caller material is 400 `validation_failed`.

Success is 200 `collection_access_changed` with exactly `collectionId`, `grantId`,
`revision`, `access`, `recordIds` and `folderIds`. The arrays remain the sorted,
unique direct active visibility roots with complete active same-owner ancestry.
The response contains no recipient identity, profile ID, internal ID, collection
or recipient envelope, protection material, revocation generation or usable key.

## Authority, grant state and error precedence

Wide owner access permits an active owned collection. Selected Master or
PerProfile access additionally requires the exact selected active profile's
current `ProfileCollection` link. Selection is never cleared or widened. Only the
owner changes grants. A currently visible foreign target returns 403 only after
its one relevant native grant and current collection/pin evidence validate.
Corrupt relevant native evidence returns 503. Hidden, revoked, unselected or
absent foreign targets return nonrevealing 404 without inspecting the requested
path grant.

The path grant must be the current active nonterminal grant for that exact owned
collection. A missing, revoked, terminal, cross-collection or otherwise hidden
grant returns 404. A stale expected revision, exhausted revision, same-access
no-op, concurrent winner or changed captured state returns 409
`revision_conflict`. POST regrant remains the only transition from Revoked to
Active; this operation never revives a grant.

The recipient must remain distinct, active, nonterminal and protected. A missing,
closing, terminal or not-yet-protected recipient makes the active grant unusable
and returns 404. Malformed present owner/recipient protection, malformed stored
collection envelope/epoch, an invalid current active grant envelope/access, or
unsafe persisted counters return 503 `persistence_unavailable`. The existing
active grant is validated against current pins before replacement; UC-36 is not a
corruption-repair endpoint.

Authentication/access and hidden-target decisions precede caller revision and
replacement-envelope binding. Valid visible input binding failures are 400;
current state conflicts are 409; required stored corruption is 503.

## Revocation-generation effect

The current schema has one revocation generation per account and no independent
grant-generation column. Therefore every committed permission change increments
the recipient account's `revocation_generation` exactly once, without changing
the recipient account revision, policy, protection, identity or lifecycle.
Existing account-wide and selected recipient handles immediately become stale.
After a new unlock, current reads/lists use the changed grant and current writes
permit read-write while rejecting read-only with 403.

This intentionally invalidates all current handles for that recipient, not only a
single collection handle: the server has no narrower session-generation primitive.
The behavior is required by the use case's revocation-generation step and is
documented as an online enforcement mechanism, not durable offline revocation.
Generation at the maximum safe integer conflicts without mutation; nonpositive or
unsafe persisted generations are corruption.

## Atomic storage and lock protocol

An initial target-first snapshot determines authentication, current owner versus
foreign visibility and selected scope without locking the path grant for a denied
foreign request. On the successful owner path, derive the recipient from the
captured exact active grant and lock in this order:

1. owner and recipient account rows together, ordered by public UUID,
   `FOR UPDATE`;
2. the actor vault-access session `FOR UPDATE`;
3. the selected owned profile, when present, `FOR NO KEY UPDATE`;
4. the target owned collection `FOR UPDATE`;
5. the exact path grant `FOR UPDATE`.

Both accounts are locked in one canonical statement before a collection or grant.
This reuses the UC-35 reciprocal/FK audit result and also serializes recipient
lifecycle, protection, session issuance and revocation-generation writers. Never
lock the recipient after the collection/grant or upgrade an earlier weak account
lock. Reciprocal permission changes acquire the same two accounts in the same
order and cannot form an owner-first cycle.

After every potentially waiting phase, re-evaluate current database-time owner
authority, selection, collection lifecycle, exact active grant, recipient
lifecycle and captured stable state. Parse each party's current protection once
after both account locks and validate the current collection and existing active
grant binding. Validate the caller's next-revision envelope after those checks.

One guarded write is the authorization linearization point and atomically:

- replaces the exact grant access, envelope and concurrency stamp;
- advances the grant revision once; and
- advances the locked recipient account revocation generation once.

No collection/member/profile relationship, collection revision/sequence/envelope,
account revision/policy/protection, session row or content row changes. Post-write
grant, generation and direct-root projection are reloaded and validated before
commit. Any grant, generation, projection or post-write fault rolls back both
mutations; a clean retry can succeed. Natural owner-session expiry after the
guarded authorization statement does not cancel the already authorized atomic
operation.

## Flow mapping and verification

- Main: strict request -> owner authority -> ordered dual-account locks -> current
  native binding -> exact active grant CAS -> permission/envelope/generation update
  -> exact 200 projection.
- AF-01: malformed transport, path IDs, revision, access or envelope returns 400
  without mutation.
- AF-02: inactive/hidden actor, collection, grant, recipient or selection returns
  nonrevealing 404.
- AF-03: missing identity/access returns 401; invalid current owner access or valid
  visible foreign administration returns 403.
- AF-04: stale/no-op/exhausted revision, changed state or concurrent winner returns
  409 and preserves the winner.
- AF-05: stored/native corruption, database failure, internal timeout or failed
  post-write verification returns 503 and rolls back. Caller cancellation
  propagates.

Command tests cover the exact three-field model, strict access transition, next
revision envelope agreement, trusted path context, authorization precedence, safe
errors and exact output. Real PostgreSQL tests cover wide/selected ownership,
both permission directions, current native pins, exact active grant resolution,
generation invalidation, projection, rollback and same-grant/reciprocal races.

HTTP tests prove old recipient handles become stale; newly unlocked recipients
retain read visibility, can edit only under read-write, and receive 403 under
read-only. Selected-profile isolation, direct/inherited visibility and exact
response fields remain enforced. Full six-family tests, per-production-assembly
90% line coverage, aggregate branch coverage, locked restore, NuGet audit, native
Maven/Python/OSV/corpus/helpers, specs, OpenAPI drift and exact-head CI are required
before merge.

## Deferred release gates

Independent security/protocol/client approval, authenticated recipient-key
discovery and external client decryptability remain release gates. Account-wide
generation invalidation is online session enforcement; UC-48 still owns durable
offline additions and permission/removal delivery. Copied plaintext or roots
cannot be recalled. Shared generated OpenAPI requiredness, wrapper, media-type and
empty-413 limitations remain documented until their common fix.

## Five review priorities

1. Owner and recipient accounts are strongly locked together in canonical order
   before collection/grant locks, with reciprocal and session-issuance proof.
2. Only a current wide or exact-selected owner can change the exact active grant;
   visible foreign denial is native-validated and hidden targets remain 404.
3. Existing and replacement envelopes bind current owner/recipient pins,
   identity, collection epoch, path grant ID and current/next revisions.
4. The grant CAS and recipient-generation increment commit together; stale,
   same-access, revoked, exhausted and concurrent states preserve the winner.
5. Exactly the grant row and recipient generation mutate; direct-root projection,
   rollback, subsequent read visibility and read-only write denial are exact.
