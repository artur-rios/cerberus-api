# Owner account export

`POST /api/accounts/me/export` implements UC54 / FR-PV-01–02. Identity authentication
and vault access remain separate. Supply the current scoped owner bearer and one
canonical `X-Cerberus-Vault-Access` account-wide handle. Profile-selected handles,
application credentials and operational credentials cannot export a person's vault.

The request is strict UTF-8 `application/json`:

```json
{"expectedRevision":1}
```

`expectedRevision` is required and must be an integer in 1..9007199254740991.
Unknown/duplicate fields, numeric strings, decimal/exponent notation, query
parameters, duplicate access headers, unsupported media parameters, compression,
invalid UTF-8 and BOMs are rejected. The existing aggregate request limit applies.
The owner and signed `iat` come from the authenticated request, never its body.
Fresh authentication must be positive, not future and less than60 seconds old at
database admission. The owner must be active and nonterminal, the handle current
and account-wide, and its policy/generation/expiry valid. Export never renews it.

## Download contract

Success is a no-store JSON attachment named `cerberus-account-export.json`, with
no ETag or Last-Modified. Conditional request headers do not suppress the export.
The body is an archive, without a `DataOutput` wrapper:

| Field | Meaning |
| --- | --- |
| `format` | Exactly `cerberus-account-export-v1` |
| `header` | Captured owner and policy context |
| `entries` | Closed typed entries described below; ordering is unspecified |
| `entryCount` | Number of entries actually delivered |
| `complete` | `true`, written only after successful enumeration and cleanup |

The header contains `accountId` (Cerberus public owner ID), `identityId` (the
owner's Heimdall identity), `revision`, `policyRevision`, `revocationGeneration`,
`renewalEnabled`, `renewalIntervalSeconds` and `serverTime`. The interval is an
explicit null when renewal is disabled; otherwise it is a positive whole-second
duration. Timestamps preserve UTC microsecond precision. `serverTime` records the
captured eligibility-check time; it is not a global transaction sequence.

Native content and key material remain encrypted. Public IDs, counters,
relationships, policy and retention metadata are explicit JSON. Byte fields are
standard base64 transports of the exact original stored bytes. Decoding those
fields recovers the original envelope/wrapper JSON and its original base64url
values, epochs, signing context and grant counters. Export does not reinterpret
legacy crypto, decrypt material or certify that damaged historical bytes decrypt.

| `entryType` | Portable fields |
| --- | --- |
| `account` | Original details-envelope bytes |
| `protection` | Protection revision, key epoch, recovery generation and original material bytes, including encrypted wrappers and public JWK pins |
| `profile` | Public resource ID, revision, sequence, edit/deletion/purge times, envelope and key-wrapper bytes |
| `record` | Native metadata/envelope and eligible owned `folderId` |
| `folder` | Native metadata/envelope and eligible owned `parentFolderId` |
| `collection` | Native metadata/envelope and original key epoch |
| `relationship` | Relationship marker, typed source/target IDs and nullable original operation ID |
| `collection-grant` | Own collection's grant/recipient account references, `read-only`/`read-write` access, `active`/`revoked` state, revision and original recipient-key envelope |
| `shared-collection-reference` | Current incoming collection, owner account and grant IDs, grant revision and access; no foreign native ciphertext or protection bundle |
| `software-grant` | Own grant ID, related software ID, state/revision and original public recipient-key bytes |
| `software-target` | Own grant ID, owned `record`/`collection` target and original encrypted target-key envelope |
| `trash-operation` | Original operation/root ID and kind, deletion time and purge deadline |

For relationships, `relationshipKind` is `current` or `historical`. Profiles point
to record/folder/collection memberships, collections to record/folder memberships,
records to their folder, and folders to their parent. Historical relationships carry
their original `operationId`; current relationships carry null. Reconstruct links
using both kind and public ID: equal UUIDs across different native kinds are valid.

The archive includes eligible owned active material and original retained material
inside its exclusive720-hour window. It excludes expired/permanent targets and
hidden ancestor chains. Retained metadata must have its original matching owned
operation and typed entry. Invalid selected history fails safely. Historical links
are normalized and filtered; raw association snapshots and foreign private profile
organization are never copied. Incoming shared references do not certify a
currently decryptable foreign payload; shared content is fetched separately under
then-current permissions.

Sessions/handle verifiers, proof challenges, recovery-operation proofs, registration
and synchronization retry/idempotency/fingerprint data, download caches, retention
claims/jobs, erasure-ledger contents and configured service/signing credentials are
excluded. No internal numeric database IDs are archive fields. This is a vault
portability API; no import endpoint or archive signature is introduced.

## Consistency and failures

One captured database view supplies the entire package. Native edits can commit
while the download runs; they appear in a later export. Fresh local owner/session,
policy/generation, account-revision and typed permanent-exclusion checks run before
bounded batches and completion. Captured ancestor paths, historical roots and
incoming sharing contributors participate in those checks without becoming archive
fields. Already admitted buffered rows or delivered bytes cannot be retracted.
Fresh authentication is checked at admission; a download can outlast that window
while its current local authority remains valid.

Before output begins, failures are actual `ProcessOutput` JSON bodies:

| HTTP | Stable failures |
| --- | --- |
|400|`validation_failed`|
|401|`authentication_required`, `vault_access_required`|
|403|`fresh_authentication_required`, `vault_access_denied`|
|404|`not_found`|
|409|`revision_conflict`|
|413|Existing aggregate request limit; no response body is declared|
|503|`persistence_unavailable`, or the existing identity dependency failure|

After output begins, dependency errors, authority loss, terminal exclusions,
cancellation, serialization/transport errors or cleanup failure abort the response.
No replacement error body or successful footer is appended. Accept a download only
after successful HTTP transport EOF, valid v1 JSON, `complete:true` and a count
matching the entries. A status200 or partially received body is insufficient.

See [export operations](../operations/account-export.md), the
[generated contract](../contracts/openapi.json) and
[protocol context rules](protocol-review.md#content-protection-and-registration).
