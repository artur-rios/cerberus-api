# Grant software access (UC-42)

`POST /api/software-grants` creates an explicit read-only grant for one active
Heimdall application over a nonempty set of currently visible owned records and
collections. It also explicitly reactivates the exact same revoked grant. UC-43
owns software authentication and ciphertext retrieval; UC-44 owns revocation.

## Request and transport

Send the current human Heimdall bearer token in `Authorization` and exactly one
canonical opaque handle in `X-Cerberus-Vault-Access`. The body has exactly these
five required properties:

| Field | Contract |
| --- | --- |
| `grantId` | Client-chosen, canonical lowercase, nonzero dashed UUID. |
| `softwareIdentityId` | Canonical nonzero application UUID in the configured Heimdall scope. |
| `expectedRevision` | Explicit `null` for initial creation; the current positive safe revision for revoked reactivation. |
| `recipientKey` | Exact public P-256 JWK fields `crv`, `kty`, `x`, `y`. Private fields such as `d` are rejected. |
| `targets` | Nonempty unique `(kind,id)` array; each object has exactly `kind`, `id`, `keyEnvelope`. |

Kinds are exactly `record` or `collection`; each ID is a canonical nonzero UUID.
Each `keyEnvelope` uses the complete native `cerberus-recipient-wrap-v1` contract:
`format`, `keyEpoch`, `grantId`, `grantRevision`, `recipientIdentityId`,
`recipientKeyFingerprint`, `authorKeyFingerprint`, `enc`, `ciphertext`,
`signature`. See the [native protocol](protocol-review.md) for encoding and signed
bytes. Integers must use canonical integer JSON forms within the protocol's safe
range; reactivation expectations must leave room for the next revision.

No query parameters are accepted. The media type is case-insensitively exactly
`application/json`, optionally with one valid UTF-8 `charset` parameter (quoted
UTF-8 is allowed). Other parameters, repeated charset, absent or alternative
media types, compression, BOMs, invalid UTF-8, empty bodies, duplicate properties,
unknown or case-forged fields at every nested level, missing required fields and
alternate UUID/number forms are rejected. The configured aggregate request-byte
bound applies before parsing; oversized input returns an empty 413. Every
response has `Cache-Control: no-store`.

Trusted actor and access context comes only from the validated identity and raw
header. Current-human online revalidation forwards the original caller bearer
before protected request processing. Malformed transport or input makes no
application lookup or grant-store call; valid human requests can still require
that earlier human revalidation. Invalid or absent JWTs make no identity call.

## Minimal response and failures

Success is 201 with this output envelope:

```json
{
  "success": true,
  "errors": [],
  "messages": ["software_access_granted"],
  "timestamp": "2026-10-12T22:00:00Z",
  "data": {
    "grantId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "revision": 1
  }
}
```

Success data contains exactly `grantId` and `revision`. It never echoes targets,
account/application metadata, public keys, encrypted resource bodies or envelopes.
Nonempty failures contain only the safe process error and no partial data.

| Status | Error / condition |
| --- | --- |
| 201 | `software_access_granted`. |
| 400 | `validation_failed`: strict transport/input, malformed handle, or incorrect submitted native binding. |
| 401 | `authentication_required` for absent/invalid/currently rejected human identity; `vault_access_required` for an absent handle. |
| 403 | `vault_access_denied`: stale/revoked/expired current access, or validated visible foreign redelegation. |
| 404 | `not_found`: missing/deleted application, inactive/terminal account, or absent, hidden, unselected, trashed or terminal target. |
| 409 | `revision_conflict`: active/colliding/reserved grant, stale or mismatched reactivation, exhausted revision, changed captured state or concurrent winner. |
| 413 | Empty response for oversized input. |
| 503 | `identity_unavailable` for upstream identity failure; `persistence_unavailable` for database, required stored-state, serialization or post-write verification failure. |

Caller cancellation propagates. Internal timeout/cancellation fails closed.
Status and error precedences deliberately avoid revealing hidden foreign state.

## Identity, ownership and cryptographic binding

The narrow application lookup is
`GET /api/scopes/{configuredScopeId}/applications/{softwareIdentityId}` with the
independently configured scoped service credential. The returned identity and
scope must match exactly and deletion state must be explicit. Missing or deleted
applications are 404; malformed, mismatched, unavailable, timeout or transport
responses are 503. A non-Active application never reaches the grant store. No
database transaction or lock spans the upstream network call.

The actor must have an active nonterminal Cerberus account and a current access
session. Account-wide Master access admits active owned targets. A selected
Master or PerProfile session admits only targets currently visible in that exact
active owned profile: collections require their direct profile association;
records follow direct/profile-folder ancestry and visible collection membership
rules. Selection is never widened or cleared by this operation.

Every target must belong to the owner. Existing native read-only or read-write
sharing never authorizes redelegation. A visible foreign target returns 403 only
after relevant native evidence validates; malformed relevant evidence returns
503. Hidden or unrelated targets return 404 without disclosing their stored
material. One denied target rejects the entire aggregate.

Every envelope must verify against the current owner account UUID and author
pin, exact target kind/UUID, current key epoch, grant UUID, resulting grant
revision, software identity and submitted public recipient-key fingerprint.
Incorrect client signatures, epochs or pins return 400. Stored required native
corruption returns 503. Private software keys remain with the client; Cerberus
does not fabricate, unwrap or receive usable decryption keys.

Heimdall currently records an application identity without registering its
cryptographic authentication key. Client provisioning and the native owner
signature bind the submitted public key to the application. Mere Heimdall
existence never grants decryption capability.

## Atomic creation, conflict and reactivation

One normalized grant row and its complete typed record/collection child rows
commit together. The transaction locks the owner account, exact session, optional
selected profile, collections in PostgreSQL UUID order, records in UUID order,
then the candidate grant. Fresh authority and captured-state checks follow each
wait and guard the mutation; persisted key, children and vault evidence are
checked before commit. Failure rolls back the entire aggregate.

An initial explicit-null expectation creates revision 1 only for a previously
absent, unreserved grant ID. An active grant always conflicts, including an exact
byte replay. Reactivation requires the same revoked public/internal identity,
owner and software application, plus the exact current safe revision. It advances
once, changes the concurrency stamp, replaces the whole recipient key and target
set, and becomes Active. It preserves every vault resource, ciphertext, key
epoch, profile/membership, revision, sequence and account metadata.

After an ambiguous response, reconcile the grant state before retrying. An exact
active replay returns 409 and never overwrites the winner. After a confirmed 503
rollback, a clean retry can succeed. Different grant IDs may independently refer
to the same targets/application; UC-44 will revoke an aggregate by its public ID.

## Cross-service and release limits

Application deletion after the lookup is not atomic with PostgreSQL creation.
UC-43 must revalidate the current software identity and active grant before any
retrieval. A persisted grant alone must never authorize ciphertext access.

Native server binding and rollback tests use real P-256 signatures with opaque
structural HPKE fixtures. They do not establish external-client key discovery,
decryption or interoperability qualification. Independent protocol/security/client
review, a deployable Heimdall software-authentication contract, production
provisioning and applicable load/ownership qualification remain release gates.
The [development review deferral](development-review-deferral.json) permits
backlog implementation into `develop` only and supplies no production approval.

The generated [OpenAPI contract](../contracts/openapi.json) describes the route
and statuses. Existing shared generator limitations around requiredness, output
wrapper metadata and empty-413 schemas remain; these runtime HTTP tests and this
contract define the strict boundary.
