# UC-01 final review and correction record

One fresh whole-branch code review inspected `5d4df15..b9d1527` before this correction
pass. The reviewer inspected production/tests/migration and ran read-only .NET serializer
probes. It found no Critical issues and three Important issues; its assessment was
“With fixes.” The executor corrected all three without requesting a second review.

| Finding | Ruling and evidence |
| --- | --- |
| Envelope incompatible with reference harness | Accepted. Use `cerberus-content-v1` and all six fields, including exact 32-byte key salt. A literal worked harness envelope failed HTTP registration with 400 before the fix and succeeded with 201 afterward; every persisted field matches. |
| Permissive numeric strings, oversized epochs, case, GUIDs and body proof | Accepted. Strict numeric and case handling, maximum safe epoch, canonical nonzero lowercase GUIDs, duplicate rejection, and nonpublic proof metadata enforce the wire contract. Seven additional HTTP cases first returned 201, then 400 after correction. Rejection tests assert no identity requests and no durable pending operation. |
| Identity signing-key rotation breaks durable retries | Accepted. Require a separate protected registration fingerprint key, shared across replicas and preserved on restore. A real PostgreSQL pending retry failed after signing-key rotation, then reconciled and replayed across two rotations with the dedicated key. Missing/weak/reused key validation also failed before correction. |
| Stale registration status in operational and protocol documents | Accepted minor; corrected historical snapshot labeling, current route text and `accountId` wire name. No deferred code-review minor remains. |

The review recognized current ownership proof, denied/MFA/outage fail-closed behavior,
durable preparation, uniqueness and transaction locking, concurrency/failure coverage,
and isolated additive migrations. It did not independently rerun the reported full suite;
the executor runs a fresh unfiltered suite and required CI on the corrected head.

The reviewer declined to judge independent cryptographic approval, actual-client/device
interoperability and production nonce accounting, as explicitly deferred by the owner.
Actual protocol approval remains pending and release gating is unchanged. The compatibility
regression above establishes DTO agreement, not independent cryptographic approval.

It also declined future update/closure/purge/restore lifecycle coordination because those
implementations do not exist yet. Registration checks existing terminal tombstones; later
lifecycle use cases must coordinate concurrent erasure with account creation and restore.
Production load, anonymous-registration rate limits and pending-operation retention remain
operational decisions. Dependency calls currently hold serialized database locks for the
bounded upstream attempt; failed preparation metadata remains durable for retry. These
are recorded limits, not claims of production capacity or a supplied retention policy.

The registration fingerprint key itself has no blind rotation support. Preserve it in
protected operational configuration; key replacement requires a versioned migration.
No credentials or client vault keys are persisted to support retries.
