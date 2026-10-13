# Revoke software access (UC-44)

`DELETE /api/software-grants/{grantId}` revokes one active software grant owned by
the authenticated human account. The grant remains for explicit UC-42
reactivation; its server-held recipient key and complete typed target set are
erased atomically. Software authentication and retrieval (UC-43) remain blocked.
This endpoint neither implements UC-43 nor calls Heimdall application APIs.

## Request and transport

```text
DELETE /api/software-grants/aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa
Authorization: Bearer <current human Heimdall token>
X-Cerberus-Vault-Access: <current opaque vault-access handle>
Content-Type: application/json; charset=utf-8
```

Send exactly one canonical opaque handle and a body with exactly one required
property:

```json
{
  "expectedRevision": 1
}
```

`grantId` must be a canonical lowercase, nonzero dashed UUID. `expectedRevision`
must be a canonical JSON integer from 1 through 9007199254740990, leaving room
for the next revision within the protocol maximum of 9007199254740991. Null,
quoted, fractional and exponent forms are rejected. Actor, account, software
identity and vault-access context cannot be supplied through the body.

No query parameters are accepted. The media type is case-insensitively exactly
`application/json`, optionally with one UTF-8 `charset` parameter (quoted UTF-8
is allowed). Missing or alternate media types, other or repeated parameters,
content encodings/compression, BOMs, invalid UTF-8, empty bodies, unknown,
duplicate, missing or case-forged properties and alternate UUID/number forms
return 400. A present empty or malformed access handle is 400; an absent header
is 401. The configured aggregate request-byte limit applies before parsing;
oversized input returns an empty 413. Every response has `Cache-Control: no-store`.

The trusted actor comes from the validated human identity. Protected-endpoint
online revalidation forwards the original bearer token before protected request
processing. Invalid or absent JWTs make no identity call. Malformed transport or
visible input cannot reach the revocation store; valid human requests may still
require the earlier human revalidation. No software application lookup or
application-authentication call is made by this revocation path.

## Minimal response and failures

Success is 200 with this envelope; the timestamp is illustrative:

```json
{
  "success": true,
  "errors": [],
  "messages": ["software_access_revoked"],
  "timestamp": "2026-10-13T00:00:00Z",
  "data": {
    "grantId": "aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa",
    "revision": 2
  }
}
```

Success data contains exactly `grantId` and the advanced `revision`. It never
echoes account/software identities, target identifiers, recipient keys,
ciphertext or key envelopes. Nonempty failures expose only safe process errors
without partial data or private storage details.

| Status | Error / condition |
| --- | --- |
| 200 | `software_access_revoked` after the verified transaction commits. |
| 400 | `validation_failed`: malformed transport, route, body, revision or access handle. |
| 401 | `authentication_required` for absent, invalid or currently rejected human identity; `vault_access_required` for an absent access header. |
| 403 | `vault_access_denied`: missing, stale, revoked, expired or otherwise invalid current vault session. |
| 404 | `not_found`: absent, foreign, terminal or already revoked grant, or initially inactive/terminal account. |
| 409 | `revision_conflict`: stale expected revision, exhausted stored revision, changed captured state or concurrent winner. |
| 413 | Empty response for oversized input. |
| 503 | `identity_unavailable` for protected human revalidation failure; `persistence_unavailable` for database, serialization, timeout, required stored-state, affected-row or post-write verification failure. |

Current account/session authority is checked before grant visibility, so guessing
another account's grant ID cannot reveal its ownership. Once valid authority and
an active owned grant have been captured, account loss becomes 409 and current
session denial remains 403 before changed-state conflict classification. Caller
cancellation propagates; internal cancellation/timeout fails closed as 503.
Failed transactions roll back the whole revocation and preserve concurrent
committed winners.

## Ownership and atomic erasure

The handler hashes the submitted handle for lookup. The store resolves the human
identity to an active, nonterminal account and requires a current nonrevoked
session with matching account policy revision and revocation generation, valid database-time
issuance/expiry and an active nonterminal selected profile when one is selected.
Grant administration is account-owned: a selected Master or PerProfile session
does not require the grant's targets to be visible in that profile.

One PostgreSQL transaction captures account, session, optional selected profile,
grant, typed targets and collateral vault evidence. It locks the owner account,
exact session, optional selected profile and grant in the UC-42-compatible prefix
order, rechecking authority and captured state after each possible wait.

The guarded compare-and-set update is the authorization linearization point. It
requires the grant still to be Active, the exact expected revision, room for the
increment and unchanged captured evidence. It sets the state to Revoked, advances
the revision once, replaces the concurrency stamp and empties the recipient
public-key bytes. It then deletes every `software_grant_record` and
`software_grant_collection` row for that exact owner/grant pair, including all
recipient key envelopes and target associations. Affected counts must match the
captured target set.

Before commit, post-write checks verify the retained grant identity, owner and
Heimdall software identity, new state/revision/stamp, empty recipient key, absence
of both target-row kinds and unchanged authority/collateral data. Natural session
time expiry after the authorized compare-and-set does not undo that transition;
non-time authority changes fail verification and roll it back.

Revocation does not parse old recipient keys or envelopes before erasing them.
Malformed opaque bytes do not prevent reduction; relational corruption that
makes the exact aggregate ambiguous returns 503 without partial erasure. Vault
resources, ciphertext, key epochs, memberships, revisions/sequences, unrelated
grants, account policy/revocation generation and owner access sessions are
preserved. No new migration is required beyond the existing software-grant schema.

## Concurrency, retry and reactivation

Revocation is an optimistic transition, not an idempotent replay. Exactly one
concurrent request can commit for an active revision. A request that captured the
active grant and loses to a state change receives 409; a later request after
revocation receives the same 404 as an absent or foreign grant. Stale expectations
and stored revision exhaustion are 409; a submitted expectation at the protocol
maximum is invalid input (400).

After an ambiguous response, reconcile the grant state before retrying. A
confirmed storage rollback permits a clean retry with current authority and the
current active revision. An old request cannot overwrite a concurrent revocation
or UC-42 reactivation winner.

[UC-42 reactivation](software-access-api.md#atomic-creation-conflict-and-reactivation)
requires the same grant owner/software identity, the exact revoked revision, a
complete new recipient key and a complete new target/envelope set. It advances
once and replaces the whole aggregate. Revocation preserves the grant's stable
identity/ownership/software binding and records the revoked revision needed for
that flow.

## Retrieval and release limits

The Revoked state and removed target rows prevent future retrieval authorization
through this grant. UC-43 must check the current Active grant and software
identity on every retrieval; that blocked use case is not implemented or simulated
here. Other independent active grants to the same targets are unaffected.

Revocation cannot remotely retract ciphertext, keys or plaintext already copied
by a client. It does not rotate client keys, invalidate the owner's vault handles
or provide UC-48 durable offline permission delivery. Independent
protocol/security/client review, deployable software authentication and production
qualification remain release gates. The
[development review deferral](development-review-deferral.json) permits development
into `develop` only and supplies no production approval.

The generated [OpenAPI contract](../contracts/openapi.json) exposes this DELETE as
`/api/software-grants/{id}` with the required UUID path, one-field JSON request,
two-field success data, safe status envelopes and content-free 413.
Command contract/handler unit tests, PostgreSQL store and concurrency tests, and
HTTP/OpenAPI tests cover validation, owner/session denial, nonrevealing absence,
atomic erasure, selected administration, winning revisions, rollback/clean retry,
no-store and response non-disclosure.
