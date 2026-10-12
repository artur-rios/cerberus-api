# Changelog

All notable changes to the Cerberus API are recorded in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and this project adheres to
[Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

No release has been tagged yet. Independent review of the encryption protocol was deferred for
development into `develop` only; it is still required before any release.

### Added

- Owner-only collection access grants (UC-35) with strict initial creation and
  explicit same-identity revoked regrant, native recipient-envelope binding and
  read-only/read-write access. Ordered owner/recipient account locking prevents
  reciprocal foreign-key deadlocks. One grant row commits with an exact direct
  record/folder visibility projection; recipient profiles and collection/member
  metadata remain unchanged. Current recipient GET/list visibility, selected
  isolation, safe HTTP errors and rollback are verified. See the
  [collection grant API](docs/security/collection-api.md#grant-collection-access-uc35).

- Owner-only atomic collection membership replacement (UC-34), with exact typed
  member sets and a same-epoch envelope for the client key manifest. Selected access cannot
  expand its current visibility. Every active native grant validates and keeps
  its wrapper/revision; only direct symmetric-difference members receive structural
  bumps. Current GET/list visibility, strict HTTP transport, winner retry and
  rollback are verified. See the
  [collection membership API](docs/security/collection-api.md#replace-collection-membership-uc34).

- Recoverable owner-only collection deletion (UC-33), with one exact 30-day
  deadline, atomic membership removal and active grant revocation. Underlying
  records/folders, direct access and opaque content survive; restore cannot revive
  revoked grants. Native selected authority, lifecycle races and rollback are
  enforced through the strict six-field HTTP contract. See the
  [collection deletion API](docs/security/collection-api.md#recoverably-delete-a-collection-uc33).

- Owner-only encrypted collection updates (UC-32), with strict expected-revision
  requests, fixed key epoch and fresh salt/nonce for changed envelopes. Updates
  return four metadata fields and preserve memberships, grants and recipient
  wrappers. Current native RO/RW recipients receive 403 after proof validation;
  hidden targets remain 404. Authority changes and concurrent updates fail safely.
  See the [collection update API](docs/security/collection-api.md#update-an-owned-collection-uc32).

- Encrypted collection detail (UC-31), with current owner/native recipient scope,
  exact selected-profile admission, actor-only profiles and sorted direct typed
  members from one read-only permission snapshot. Relevant native, projection and
  ancestry corruption fail without payload; unrelated corruption is isolated.
  See the [collection detail API](docs/security/collection-api.md#get-an-encrypted-collection-uc31).

- Encrypted collection listing (UC-30), with current ownership/native recipient
  grants, exact selected-profile scope and bounded encrypted continuations.
  Permissions are reapplied before pagination; listing preserves stored data.
  See the [collection API](docs/security/collection-api.md).

- Owned encrypted collection creation (UC-29), with current selected scope,
  initial profile/folder/record links and atomic structural metadata updates.
  Native complete protection rotation includes new collections; recipient
  membership cannot transfer ownership. See the
  [collection create API](docs/security/collection-api.md#create-a-collection-uc29).

- Owned folder moves (UC-28) with atomic root/immediate-parent metadata,
  per-member selected scope checks, cycle rejection and current native recipient
  bindings. Encrypted subtree content and direct links remain unchanged; current
  GET/list inheritance changes immediately. Durable synchronization remains later
  work. See the [folder move API](docs/security/folder-api.md#move-a-folder-uc28).

- Recoverable owned folder deletion (UC-27), with atomic active subtree/record
  cascades, one 30-day deadline, retained opaque ciphertext and typed restore
  snapshots. Earlier independent trash retains its deadline; recipient reads
  disappear while grants remain active. Restore, physical expiry and durable
  synchronization remain later work. See the
  [folder deletion API](docs/security/folder-api.md#recoverably-delete-a-folder-uc27).

- Encrypted folder content updates (UC-26), with current owner/native read-write
  scope, revision conflict protection, guarded permission revalidation and rollback.
  Folder organization and descendant content stay unchanged.
  See the [folder update API](docs/security/folder-api.md#update-encrypted-folder-content-uc26).

- Encrypted folder detail (UC-25), with current owner or native recipient scope,
  selected folder ancestry and only visible direct profile, effective collection
  and independently permitted parent references from one permission snapshot.
  See the [folder detail API](docs/security/folder-api.md#get-an-encrypted-folder-uc25).

- Encrypted folder listing (UC-24), with current owner or native recipient
  scope, selected member descendants, hidden ancestry and record-only
  nonexpansion, one permission snapshot and opaque high-water pagination.
  Read-only and read/write recipients receive only included encrypted folders.
  See the [folder list API](docs/security/folder-api.md#list-encrypted-folders-uc24).

- Encrypted owned folder creation (UC-23), with current account-wide or selected
  access, direct and owned-collection parent ancestry, typed ID reservation,
  atomic profile links and immediate-parent metadata. New folders participate
  in complete protection rotation and can contain records immediately. See the
  [folder creation API](docs/security/folder-api.md).

- Permanent owned record erasure (UC-22), with current active/trash scope, typed
  terminal IDs, durable intent and fenced purge recovery. Success follows ledger
  flush and physical removal; failures after intent keep deletion irreversible.
  Backup replay removes record ciphertext before traffic and preserves other kinds
  sharing its UUID. Terminal records leave rotation inventory while recoverable
  trash remains. See the [record API](docs/security/record-api.md#permanently-delete-a-record-uc22)
  and [restore runbook](docs/operations/restore.md#typed-permanent-record-erasure-uc22).

- Owned record folder moves (UC-21) preserve ciphertext, identity, client time and
  direct links while atomically updating record and changed immediate parent
  metadata. Selected moves retain or narrow effective profile/collection scope;
  native recipient visibility follows current ancestry, with current resulting
  native envelopes and pin evidence revalidated. No-op/retry and concurrent
  edit/trash/rotation behavior remain optimistic and fail closed. See the
  [record API](docs/security/record-api.md#move-an-encrypted-record-uc21).

- Recoverable owned record deletion (UC-20), with exact revision checks, unchanged
  retained ciphertext, restricted association snapshots and a 720-hour deadline.
  Atomic link removal and immediate parent metadata updates hide the record while
  preserving selected access. Native recipients cannot delete, and retries cannot
  extend retention. Restoration and typed expiry remain later release prerequisites.
  See the [record API](docs/security/record-api.md#delete-an-encrypted-record-uc20).

- Encrypted record replacement (UC-19) for current owners and native read/write
  recipients, with optimistic revisions, immutable relationships and four-field
  metadata responses. Database-time scope/ancestry guards, native grant validation
  and compatible row locks preserve winners during revocation, hierarchy changes,
  reciprocal edits, creation and owner rotation. See the
  [record API](docs/security/record-api.md#update-an-encrypted-record-uc19) for request
  fields, epoch/context rules and retry behavior.

- Encrypted record lookup (UC-18) by canonical public UUID, with current selected
  scope and native collection-grant validation. Responses include only visible
  direct profile, parent-folder and collection references; direct record access
  hides parents outside scope, and recipients never receive owner profile IDs.
  One database snapshot reads only the target and its ancestry. Shared native
  validation rejects quoted collection epochs before returning ciphertext. See the
  [record API](docs/security/record-api.md#get-an-encrypted-record-uc18) for fields
  and failure behavior.

- Encrypted record listing (UC-17) with current account-wide/selected-profile scope,
  native recipient collection grants, direct and descendant membership, deduplicated
  keyset pages and opaque bound cursors. One database snapshot enforces permission
  and ancestry before pagination; responses expose only encrypted record metadata.
  Apply the additive `CollectionMembership` migration before deployment. See the
  [record API](docs/security/record-api.md) for cursor, visibility and error rules.

- Encrypted record creation with owned profile/folder links (UC-16), including new
  content inside native selected-profile access, atomic parent metadata updates and
  rollback on failure. See [record API](docs/security/record-api.md) for the request,
  scope, timestamp and retry rules.

- Opening profile access with native scoped proofs in Master and PerProfile modes (UC-15): fresh-identity
  challenge bootstrap, one-use proof consumption and opaque handles restricted to the selected profile.
  See [profile access API](docs/security/profile-access-api.md) for exact bodies, expiry and retry behavior.

- The specifications: initial notes, business rules, the formal requirements and the use case specification.
- The .NET 10 solution scaffold, with typed configuration validation, request byte limits and no-store responses.
- PostgreSQL persistence with its initial migration, and durable erasure files and retention leases.
- Strict Heimdall token validation and scoped Heimdall adapters with online identity revalidation.
- Restore reconciliation that replays erasures before traffic resumes.
- Command-line modes to validate configuration, migrate the database and reconcile a restore without starting HTTP.
- A development container image, a generated OpenAPI contract, and a protocol-review record validator.
- A protocol interoperability harness, and the recorded owner deferral of independent protocol review for
  development.
- Account registration that creates the Heimdall identity and the encrypted account, with idempotent retries and
  reconciliation (UC-01).
- Authentication delegated to Heimdall, including second-factor challenges (UC-02).
- Reading and atomically updating the encrypted account details with current vault access (UC-03, UC-04), and
  updating Heimdall identity details (UC-05).
- Vault protection: initializing and changing it, recovering vault access and refreshing the recovery key
  (UC-38 to UC-41).
- Creating profiles, listing the permitted ones with opaque pagination, and retrieving one within the current vault
  scope (UC-09 to UC-11).

- Updating opaque profile content with optimistic concurrency and immutable key protection (UC-12).
- Recoverable profile deletion with durable trash membership, a 30-day deadline, queued retention work and
  revocation of selected profile handles while retaining encrypted content (UC-13).

- Replacing profile associations to owned records/folders and owned or explicitly shared collections, with current
  grant checks and selected-handle scope limits (UC-14).
- Real association IDs in profile creation, retrieval and list items; profile trash snapshots and removes its links
  while retaining underlying resources and other profiles' memberships.
- Complete vault content rotation covering retained owned records, folders, collections and active owned grants,
  including trash; received and revoked grants remain unchanged.

### Fixed

- Profile creation rejects scoped verifier reuse across retained owned profiles, including trash. Challenge/open
  fail closed on legacy duplicate or corrupt retained keys; use independent keys and fresh replacement profile IDs.

- Profile create/update reject client timestamps that round down to the default storage value. Accepted UTC
  timestamps are normalized down to microseconds before database binding, including dates before 2000.
- Vault protection rotation accepts the original signed wrapper revision after profile content/metadata revisions
  advance, while binding replacement wrappers to the new revision.

- Vault content rotation rejects reused account envelope salt/nonce and rolls back profile/resource sequence
  regressions with the rest of the transaction.

[Unreleased]: https://github.com/artur-rios/cerberus-api/commits/develop
