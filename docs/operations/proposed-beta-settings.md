# Proposed beta operational settings

Status: **Proposed for operator review; not deployment configuration.**

These values are review inputs for Operations §3. Production startup requires explicit settings; no value in this document is installed as a silent default. Secrets, identity scope and deployment host details must be supplied through the operator's protected configuration.

| Setting | Proposed value | Reason for review |
| --- | --- | --- |
| `CERBERUS_BACKUP_RETENTION` | `7.00:00:00` | Limits backup copies while preserving a short operational recovery window. |
| `CERBERUS_LOG_RETENTION` | `7.00:00:00` | Bounds redacted operational logs; operator collection must enforce expiry. |
| `CERBERUS_SYNC_RETENTION` | `30.00:00:00` | Retains a month of incremental synchronization history; expired cursors require an authorized full resync. |
| `CERBERUS_RETENTION_INTERVAL` | `00:05:00` | Worker polling interval; request-time deadlines enforce the product's 30-day restore windows independently. |
| `CERBERUS_BACKUP_PATH` | `/srv/cerberus/backups` | Protected backup storage; encrypt backups independently of client E2E. |
| `CERBERUS_ERASURE_LEDGER_PATH` | `/srv/cerberus-erasure-ledger` | Separate protected durable storage excluded from database/backup rollback; preserve independently. |
| `CERBERUS_MAX_REQUEST_BYTES` | `1048576` | Initial one-MiB bound; test against actual encrypted export/sync payloads before adoption. |
| `CERBERUS_MAX_PAGE_SIZE` | `100` | Initial bounded pagination; load-test authorization filtering and sync batches before adoption. |

The erasure ledger directory must be durably preprovisioned, private to its operator/service account, with no symbolic-link components. Provisioning must flush newly created directory entries in their parent directories and verify the filesystem/storage actually honors durability primitives. The service never creates missing ledger storage: absence fails startup rather than silently replacing a lost ledger with an empty one. Ledger files contain only resource GUID, resource kind and deletion time. The service durably flushes committed records and their directory before acknowledging a deletion record. Ensure its external ledger survives database restores; directory ownership must match the container's non-root service user.

The first four values use the invariant .NET constant TimeSpan format. These deployment retentions do not change the specified 30-day trash/account-closure period or the initial 24-hour account offline policy.

Record approval, deployment-specific changes, storage/backup access ownership and successful load/restore evidence before accepting production traffic. No deployment, benchmark, expiry enforcement by external log collection or operator approval is claimed by this proposal.
