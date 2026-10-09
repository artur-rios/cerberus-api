# Isolated backup restore

This is the foundation procedure, not a claim that the unfinished vault is production-ready.
Only an authorized operator performs a restore. Never test it against the live database.

## Provisioning and backup

Use the approved beta inputs in [operational settings](proposed-beta-settings.md). Supply
database and Heimdall credentials through protected environment configuration; never commit
them or put them in shell history. The Linux erasure ledger must be private (0700), owned by
the service user, durably preprovisioned and independently preserved. Verify actual storage
durability and flush new directory entries in their parents during provisioning. Missing,
nonprivate or symlinked storage is rejected; the API does not recreate it.
Configuration validation parses PostgreSQL settings without opening a connection and
rejects worker intervals outside the timer's one-millisecond-to-4,294,967,294-millisecond
range. Failures expose setting names only. A successful syntax check does not prove network
connectivity, credentials, current ownership or deployment readiness.

Back up PostgreSQL using operator-managed `pg_dump` or a consistent physical backup. Encrypt
backups with an operator-managed key independent of client vault keys. Restrict access,
verify backup integrity and enforce the configured seven-day expiry. Keep the erasure ledger
outside the database restore/backup rollback boundary; never replace it with an older copy.
Preserve it for the lifetime of terminal identifiers. The configured log collector must
restrict access and enforce seven-day expiry; the API's console sink cannot enforce an
external collector's policy. No credentials, body, ciphertext or personal details are logged.

## Restore steps

1. Stop all API/worker processes and disable ingress. Isolate a new database and verify the
   backup and preserved external ledger. Do not overwrite the live database.
2. Restore the backup into the isolated database using `pg_restore` or `psql`, matching the
   operator's backup format. Point the protected connection configuration at that database.
3. Run `dotnet ArturRios.Cerberus.WebApi.dll --validate-configuration`, then
   `dotnet ArturRios.Cerberus.WebApi.dll --migrate`. Any nonzero exit keeps ingress disabled.
4. Run `dotnet ArturRios.Cerberus.WebApi.dll --reconcile-restore`. It replays all committed
   ledger records idempotently and verifies the configured scope and service identity's
   **current** ownership in Heimdall, not just stale JWT claims. Each implemented domain
   module must register `IRestoreVerificationStep` checks for deadlines, grants, epochs and
   terminal resources before it can serve restored data. A failed, unavailable, cancelled or
   corrupt reconciliation does not open the traffic gate.
5. Independently verify terminal IDs remain erased and restored grants/deadlines are current.
   Set `CERBERUS_RESTORE_REQUIRED=true` for the restored instance. Normal startup repeats
   reconciliation before starting HTTP or the worker; its in-memory gate is not a durable
   certificate from an earlier maintenance process. Keep this mode on for restored instances.
6. Enable ingress only after successful startup and authorized operational verification.
   Record the operator, backup identity and safe result codes, not protected payloads. If
   verification fails, retain the isolated database for investigation and leave traffic off.

The worker uses bounded pages and timed exclusive PostgreSQL claims. Failed work remains
uncompleted and can be reclaimed after the lease expires. Handlers must be idempotent and
fence mutations with the persisted claim token; this is at-least-once, not exactly-once,
execution. Unknown operation kinds fail rather than silently completing work. Stable warning
codes and the `cerberus.retention.failures` counter expose failures without exception payloads.
Operators must monitor these signals; a worker outage never extends a product deadline.

## Reproducible foundation evidence

`RestoreReconcilerTests.GivenBackupBeforeErasure_WhenRestoringActualPostgresDump_ThenReplayLedgerBeforeOpeningTraffic`
uses an isolated PostgreSQL 18 container: dump the schema, durably record an erasure, restore
the older dump, confirm the marker is absent, replay the preserved ledger and verify the
marker before opening the gate. Other tests cover revoked current authority, corruption,
cancellation, duplicate replay and queued-reconciliation races. The migration CLI is tested
against a separate blank PostgreSQL container. These fixtures establish infrastructure
behavior, not actual domain deletion, client interoperability or a production restore drill.


## Typed permanent record erasure (UC22)

Apply `20261009035540_TypedTerminalErasure` with ingress disabled before serving
permanent-delete requests. The unique terminal identity is `(resource_kind,
resource_id)`, with account, profile, record, folder, collection and grant kinds.
Legitimate existing markers retain their kinds and times; invalid existing kinds
fail migration closed and require operator repair. Do not merge distinct kinds
sharing a UUID. Reverting to the old global UUID uniqueness can fail once typed
collisions exist; use a reviewed forward migration rather than deleting markers.

Preserve both new `kind-GUIDN.json` files and legacy `GUIDN.json` files in the
external ledger. Legacy contents determine their resource kind. Reads validate
kind/ID/filename, privacy, symlinks and corruption; do not rename files, replace
this ledger with a database backup or remove it to recover traffic. Repeated same
kind/ID events retain the original terminal time, and replay preserves the earliest
known time. The ledger contains minimal identity/kind/time, no content or keys.

Permanent deletion commits a typed terminal intent and `record-purge/{UUID}` work
before external ledger I/O. A pending record is already inaccessible and its ID
reserved even if the public request returns `503` or is cancelled. After the
configured lease expires, a fresh worker claim retries the same intent, flushes
the ledger, physically removes only that record and completes under database-time
claim fencing. Monitor existing retention failure signals. Correct the ledger or
database outage and restore the worker; do not clear terminal intent or reuse the
record ID. Recoverable trash remains in key rotation; terminal pending records do
not. Short worker intervals can expire during filesystem I/O and cause safe retries.

Record ledger replay upserts the typed marker and removes restored record
ciphertext, direct links and typed record trash membership in one transaction
before the traffic gate opens. Empty root-record trash operations and their trash
work are removed; nonempty cascades, other kinds, profiles, folders, collections
and grants survive. Other resource kinds retain fail-closed typed tombstones until
their physical replay handlers ship. Selected profile IDs are never cleared.

`RecordPurgeHandlerTests.GivenBackupBeforePermanentDeletion_WhenRestoringAndReplayingLedger_ThenRemoveCiphertextBeforeTrafficAndKeepOtherKinds`
uses an actual PostgreSQL dump taken before erasure, restores its ciphertext,
replays the preserved external ledger and checks physical record absence before
opening traffic while a same-UUID folder survives. Claim-expiry/theft, rollback,
ledger outage and corrupt-reconciliation tests cover failed recovery. These server
fixtures do not certify a production restore drill or external client behavior.
