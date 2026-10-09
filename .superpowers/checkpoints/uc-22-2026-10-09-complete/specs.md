# UC22 Permanently delete record

## Intent and authority

Implement UC22 main and AF01–05, FR-RC-07/08, BR21/26: an authenticated owner can
explicitly erase an active or trashed record, cannot recreate that typed record
identifier, and backup replay removes the erased record before traffic opens.
No plaintext or usable keys are read. Other resource kinds sharing the GUID
survive. User-authorized all-issues batch supplies automated design/plan/delivery
gates; independent formal security/protocol/client qualification remains deferred
for develop only. No production deployment or live customer deletion is performed.

Sources: current Use Case Specification UC22; System Requirements records,
authorization matrix, metadata and lifecycle sections; Business Rules21/26;
Testing Specification; Technology Stack; initial/formal Workflow; Operations
backup/retention runbook. Existing native record read/trash/move, current scope,
ordered locks, external FileErasureLedger, terminal store, restore traffic gate,
and fenced RetentionRunner are the implementation patterns.

## Architectural choice

Database commit followed by ledger can lose deletion across backup restoration.
Ledger write inside a rollbackable authorization transaction can leave a live
resource accessible despite an external terminal event. Use a committed terminal
intent/outbox, then durable external ledger, then physical purge. The terminal
intent immediately hides the record in every reader/writer and reserves its typed
identifier. Only completion after durable ledger and physical removal returns200.
Dependency/caller failure after intent can leave irreversible deletion pending;
503 is never a claim that the already authorized terminal intent was rolled back.
An idempotent worker recovers pending work without user credentials or new authority.

## Typed erasure prerequisite

Before implementing any physical purge, replace global GUID identity everywhere
with (ResourceKind, ResourceId). Supported kinds are account, profile, record,
folder, collection, grant. Typed terminal unique index and kind check constraint
preserve legitimate existing rows; invalid legacy kinds fail migration closed.
All SQL/LINQ authorization/reservation predicates must test the actual kind,
including compound owner/content/grant terms. The terminal lookup interface
requires kind. Invalid kinds/empty IDs fail before persistence.

New ledger filenames are kind-GUIDN.json. Continue validating/reading legacy
GUIDN.json entries by the kind in their contents; preserve legacy files. Different
kinds with the same GUID coexist. Same-kind repeats preserve original terminal
time; replay keeps the earliest known time. Reads validate filename/id/kind,
corruption and symlinks and remain fail-closed. Storage privacy/fsync remain intact.
Existing fixture-only terminal kinds become the actual erased kind while retaining
all denial assertions. Real cross-kind tests cover readers, writers, reservation,
native-grant visibility and actual backup replay. No selected ProfileId is ever
set null; stale selection stays nonnull and denied or revoked.

## Public contract

DELETE /api/records/{id}/permanent, current Heimdall identity, exactly one canonical
X-Cerberus-Vault-Access, canonical lowercase nonzero GUID, no query, VaultProofBody
strict UTF8/no BOM/compression/duplicates/unknown/case/numeric strings. Exactly one
JsonRequired body property expectedRevision: positive safe integer<=9007199254740991.
No envelope/time/context/association overrides.200 record_permanently_deleted has
exactly recordId and deletedAt (UTC microsecond canonical terminal-intent time).
No content, key, trash association snapshot or owner metadata is returned.
All outcomes no-store:400 syntax/input;401 identity/missing handle;403 invalid
current access or visible foreign ownership;404 missing/hidden/terminal target;
409 stale revision/new unheld earlier authority;503 dependency/corrupt required
authority/metadata. Common oversized input is empty413. Lost success/terminal retry
is404, without a new deadline or terminal event; worker retries are idempotent.

## Current authority and scope

Only target owner can erase. Active source uses complete current active ancestry
and direct/owned-collection profile scope as current record trash. Validate all
contributing foreign native routes solely to classify visible foreign403; valid
RO and RW both deny before stale revision/content probing. Hidden404 precedes
native corruption. No foreign account/profile locks. Native route corruption503.
Current session/policy/generation/expiry/selected profile rechecked after waits
and in final guarded intent statement; exclusive database statement-time expiry.

Trashed owned record remains explicitly deletable before or after retention expiry.
Account-wide owner access does not need historical scope to erase its own ciphertext.
Selected access needs an active owned selected profile plus current validation of
the restricted typed trash operation membership and its RecordAssociationSnapshot:
original direct profile ID equals selection, or retained direct owned collection
still active and attached to selection, or retained folder's complete active owned
ancestry currently attached directly/through an owned collection to selection.
No foreign grant or inactive/foreign profile widens trash access. Missing restricted
membership or inaccessible old path404; malformed needed snapshot503. Historical
snapshot is never returned or used to reactivate links. Active cyclic ancestry503
unless an already hidden path requires404. Account-wide may erase corrupt ciphertext
or client time; those bytes are not parsed. Required current revision and sequence
must be safe; matching maximum record revision is deletable because no target
revision is advanced. Parent counters still require safe advancing capacity.

## Intent transaction

Own-account NO KEY UPDATE→current session UPDATE→all related own direct profiles
including selection GUID NO KEY UPDATE→related own direct collections GUID NO KEY
UPDATE→active immediate folder NO KEY UPDATE→record UPDATE→existing direct typed
association rows UPDATE. Current own writers serialize through account; recipient
writers use collection-before-content. No late earlier locks; newly contributing
parents/associations require409 after fresh authority classification. Complete
current ancestry is re-evaluated at final intent snapshot rather than ancestor locks.

Final guarded record transition requires current owner/scope/session, expected
revision, original parent and held direct links. Record terminal marker and
immediate-due record-purge/GUID work commit together. Active target is marked
deleted and direct typed links/snapshot removed atomically; active immediate folder
and directly linked active owned profiles/collections advance revision/sequence
once, preserving ciphertext/client time/key metadata. No inherited ancestor,
recipient-profile or unrelated metadata changes. Trashed target has no second
structural removal/bump. Safe sequence regression/exhaustion/parent capacity fails
before intent commit and rolls back all mutations. Terminal identity protects all
later writes even while bytes await ledger/purge. Intent contains no proof/handle,
usable keys or encrypted payload beyond the still-retained record awaiting purge.

## Durable purge and restore

Record purge handler acts only on an existing typed terminal intent and matching
record-purge work. Durably fsync external record event first. In a separate
transaction, fence every physical mutation using current persisted work claim
token and exclusive database expiry; no expired/stolen token can remove data or
mark work complete. Request path uses the same actual claim/finalizer protocol;
if another worker owns completion, return retryable503 until ordinary reload404.
Delete exact record ciphertext and direct associations, typed record trash entry,
and empty trash operation/work only; preserve nonempty cascade operations, all
other resource kinds, folders/collections/profiles/grants and other records.
Terminal marker and external minimal event persist. Caller cancellation before
intent does nothing; after intent leaves durable worker work. Ledger/fsync/DB
failure never acknowledges success or undoes terminal status. Repeated execution
and post-ledger crash recovery are safe. Worker does not need a vault session.

Restore reconciliation upserts typed terminal markers and physically removes
matching RECORD ciphertext/associations/trash membership before traffic opens.
Actual pg_dump/restore/replay tests establish deletion preservation and cross-kind
survival; corrupt ledger/DB outage leaves traffic closed. Other resource-kind purge
handlers remain their own later UCs; typed tombstones keep them inaccessible without
claiming their ciphertext has been purged. Selected profile references never null.

## Verification and delivery

TDD all four tasks, actual PostgreSQL/native HTTP/real filesystem ledger; no
in-memory substitute. Cover active/trash/accountwide/selected/current native RO/RW,
all AFs, stale winner, every lock wait expiry, before-final authority changes,
intent rollback faults, ledger outage, after-ledger/post-delete crash, lease theft,
stale create/edit/move/trash, cross-kind GUID and actual backup restore. Fresh full
unfiltered six families zero failures/skips,>=90% line each production assembly,
branch reported, locked restore/audits/native corpus/helpers/spec/OpenAPI gates.
Fresh merged UC21 exact-tree CI supplies baseline; no redundant unchanged run.
One fresh ordinary whole-branch reviewer, one blocking TDD correction pass if
necessary, all rulings/costs/minors retained. One PR closes23; latest exact-head
branch-policy/test/docker success, fresh base/rules/mergeability, normal merge,
nine DoD/issueClosed/projectDone/remote branch absent/develop tested tree/four
primary user-file hashes unchanged. Archive own scratch before cleanup.
Durable offline visibility synchronization and shared OpenAPI fixes remain before
release; BEFORE UC35 audit implicit recipient-account FK locks. Independent
formal/client release qualification remains required.
