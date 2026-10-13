# UC-44 Revoke Software Access Design

## Intent and scope

Implement issue #45, UC-44, so an authenticated Cerberus account owner can revoke one
active software grant. A successful revocation must prevent every subsequent UC-43
retrieval through that grant, erase the server-held recipient material associated with it,
and leave already copied ciphertext outside remote control as documented by the product.

This use case is independent of the blocked UC-43 application-authentication contract.
Revocation is performed by the human grant owner through the existing Heimdall person
authentication and vault-access session. It does not authenticate the software identity or
call Heimdall's application endpoint.

The design implements FR-SW-05 and business rules BR-15 and BR-17 while preserving BR-10.
It follows the established collection-grant revocation and software-grant creation patterns.

## Chosen approach

Add a dedicated software-grant revocation command, handler, domain contract, and PostgreSQL
store. The store performs one guarded transaction that changes the grant from Active to
Revoked, advances its revision, erases its recipient public key, and deletes all typed target
rows containing recipient key envelopes.

This is preferred over adding revocation to `SoftwareGrantStore`, because creation/reactivation
and revocation have distinct authorization, validation, and postcondition checks. It is also
preferred over retaining blank target rows: a revoked grant needs only its stable identity,
owner, software identity, state, and revision for exact reactivation. Removing target rows
eliminates stale scope metadata and makes it impossible for a retrieval query to observe an
old target association accidentally.

No database migration is required. The existing software-grant tables and revoked state
already support this transition, and UC-42 reactivation already replaces the complete target
set for a revoked grant.

## HTTP contract

Expose:

```text
DELETE /api/software-grants/{grantId}
Authorization: Bearer <current human Heimdall token>
X-Cerberus-Vault-Access: <current opaque vault-access handle>
Content-Type: application/json[; charset=utf-8]
```

The request body has exactly one required property:

```json
{
  "expectedRevision": 1
}
```

`grantId` must be a canonical lowercase, nonzero dashed UUID. `expectedRevision` must be a
positive canonical JSON integer below the protocol maximum so the next revision remains
representable. No query parameters, alternate media types, extra media-type parameters,
unknown or duplicate properties, case-forged fields, BOMs, invalid UTF-8, empty bodies, or
compressed request bodies are accepted. The existing request-size limit applies before
parsing, and an oversized request returns an empty 413 response.

Every response receives `Cache-Control: no-store` through the existing middleware.

Success is 200 with the normal data envelope and only the grant identifier and new revision:

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

The response never echoes the software identity, recipient key, target identifiers,
ciphertext, or key envelopes.

## Application and domain components

Add these focused components:

- `SoftwareGrantRevocationInput`, `SoftwareGrantRevocationRequest`,
  `SoftwareGrantRevocationDetails`, and `ISoftwareGrantRevocationStore` in the Domain
  software contracts.
- `RevokeSoftwareAccessCommand` with JSON `expectedRevision` and internal actor,
  vault-access, and route-grant context.
- `RevokeSoftwareAccessValidator` for the visible route/body contract.
- `RevokeSoftwareAccessHandler` for cancellation, authentication, access-handle hashing,
  validation, store dispatch, safe error mapping, and result-shape verification.
- `RevokeSoftwareAccessOutput` containing only `GrantId` and `Revision`.
- `SoftwareGrantRevocationMessages` for stable messages and HTTP mappings.
- `SoftwareGrantRevocationStore` for the transactional authorization and state transition.
- A `DELETE {id}` action on `SoftwareGrantsController` plus dependency registrations in
  `Startup`.

The command layer follows the existing defensive boundary: an absent actor is
`authentication_required`, an absent vault header is `vault_access_required`, and a malformed
handle or visible input is `validation_failed`. Unknown store errors and internally
inconsistent data/error combinations collapse to `persistence_unavailable`.

## Authorization and transactional data flow

The store receives only the validated human identity GUID, hashed access verifier, grant GUID,
and expected revision. It never trusts an account identifier from the request.

Within one PostgreSQL transaction:

1. Resolve the acting identity to an active, nonterminal account.
2. Resolve a current, nonrevoked vault-access session whose policy revision and revocation
   generation still match the account. A selected profile does not narrow grant administration:
   the grant itself is account-owned, but the selected profile must remain active so the
   submitted access session remains valid.
3. Resolve the requested software grant only when it belongs to that account and is Active.
   Missing, foreign, terminal, and already revoked grants are indistinguishable as `not_found`.
4. Capture the account, session, grant, and typed target rows needed to detect changes while
   waiting for locks.
5. Lock the owner account, access session, and software grant in the same prefix order used by
   UC-42. Recheck authority and the captured state after each wait.
6. Require the stored revision to equal `expectedRevision`, require room for the increment,
   and use a guarded compare-and-set update as the linearization point.
7. Set the grant state to Revoked, set revision to `expectedRevision + 1`, replace the
   concurrency stamp, and replace the recipient key with an empty byte array.
8. Delete every `software_grant_record` and `software_grant_collection` row for that exact
   owner/grant pair. Verify the affected counts against the captured target set.
9. Re-read and verify the revoked grant and absence of all target rows before committing.

Revocation is a security-reducing operation, so it does not parse or cryptographically validate
the old recipient key or target envelopes before erasing them. Corruption that prevents the
store from identifying the exact owner, grant, revision, or complete target-row set fails closed
as `persistence_unavailable`; malformed opaque key bytes do not preserve otherwise revocable
access material.

The transaction does not modify vault records, collections, ciphertext, collection membership,
key epochs, account policy/revocation generation, or unrelated grants. Online UC-43 checks the
grant's current Active state on every request, so the committed state transition is sufficient
to deny future retrieval. Account revocation generation is not advanced because the software
identity has no Cerberus account/session to invalidate; invalidating the owner's vault sessions
would be unrelated and harmful.

## Concurrency and retry behavior

The expected revision makes revocation an optimistic transition, not an idempotent replay:

- Exactly one concurrent request can commit for an active revision.
- A request that captured the active grant but loses to another state change returns
  `revision_conflict`.
- A later request made after the grant is already hidden by revocation receives the same
  nonrevealing `not_found` as an absent or foreign grant.
- A stale expected revision returns `revision_conflict` without changing the winner.
- Revision exhaustion returns `revision_conflict`.
- Database, serialization, timeout, relational stored-state, affected-row, or post-write
  verification failures return `persistence_unavailable`; transaction disposal rolls back all
  partial changes. Opaque recipient material is erased without parsing.
- Caller cancellation propagates. Internal cancellation or timeout fails closed as 503.

Reactivation remains UC-42 behavior: the owner must submit the exact revoked revision and a
complete new recipient key and target/envelope set. Revocation therefore preserves the grant
row's owner and Heimdall software identity while erasing all current access material.

## Failure contract

| Status | Stable error and condition |
| --- | --- |
| 200 | `software_access_revoked` after verified commit. |
| 400 | `validation_failed` for malformed transport, UUID, body, revision, or vault handle. |
| 401 | `authentication_required` for missing/invalid/currently rejected human identity; `vault_access_required` for an absent access header. |
| 403 | `vault_access_denied` for a stale, revoked, expired, or otherwise invalid current vault session. |
| 404 | `not_found` for absent, foreign, terminal, or already revoked grants and inactive/terminal accounts. |
| 409 | `revision_conflict` for stale or changed captured state, revision exhaustion, or a concurrent winner. |
| 413 | Empty response for request-size rejection. |
| 503 | `identity_unavailable` from protected human revalidation; `persistence_unavailable` for required storage or stored-state failure. |

The status and lookup precedence must not reveal whether another account owns a guessed grant
identifier. No failure may change the grant, delete target rows, or mutate unrelated vault data.

## Testing strategy

Implement test-first at each layer.

### Domain and application unit tests

- Validate nonzero canonical route context and revision boundaries.
- Reject absent actors, missing access handles, malformed handles, invalid revisions, and
  inconsistent store results without making unsafe calls.
- Map every allowed store error and collapse unknown errors to `persistence_unavailable`.
- Verify cancellation propagation and exact success output/message.

### PostgreSQL store tests

- Revoke an active owned grant and verify state, incremented revision, new concurrency stamp,
  empty recipient key, and removal of every record/collection target row.
- Verify the owner account, records, collections, encrypted bodies, key epochs, revisions,
  sequences, collection grants, other software grants, and access session remain unchanged.
- Cover absent, foreign, already revoked, terminal/inactive account, missing/stale/revoked/
  expired session, stale expected revision, revision exhaustion, and corrupt stored-state paths.
- Exercise record-only, collection-only, and mixed target grants.
- Race two revocations and prove one commit with no partial loser mutation.
- Race revocation against UC-42 reactivation/grant state changes and verify the captured-state
  classification and winning database state.
- Inject failures at the guarded update, target deletion, and post-write verification boundaries;
  prove rollback and a clean retry where applicable.

### Functional HTTP tests

- Verify the exact successful DELETE contract and response shape.
- Cover strict route, query, header, body, media-type, encoding, compression, duplicate/
  unknown-field, request-size, and authentication behavior.
- Cover 401, 403, nonrevealing 404, 409, and both identity/persistence 503 paths while asserting
  the database is unchanged on failure.
- Assert `Cache-Control: no-store` on success and failures and confirm response bodies never
  disclose targets, ciphertext, identities, recipient keys, or key envelopes.
- Regenerate and check the OpenAPI contract.

Run focused tests during implementation, then the required Unit, Functional, unfiltered,
OpenAPI, dependency-audit, and merged coverage gates. Production line coverage must remain at
least 90 percent; branch coverage is reported.

## Documentation and tracking

Add `docs/security/software-access-revocation-api.md` describing the final runtime contract and
update the foundation status and changelog consistently with existing use cases. At the
pre-pull-request gate, update only issue #45's README backlog row from Todo to Done and advance
M-04 from 4/6 to 5/6 closed. UC-43 remains Todo and blocked.

## Explicit exclusions and assumptions

- UC-43 software authentication and secret retrieval remain blocked and are not implemented or
  simulated here.
- UC-45 through UC-49 offline authorization/synchronization remain separate use cases.
- Revocation cannot retract ciphertext already copied by a software client.
- No Heimdall API or application-authentication change is introduced.
- No plaintext, usable decryption key, or server-generated recipient key is added.
- No schema migration, new package, service, background worker, or cryptographic primitive is
  required.
- The approved development protocol-review deferral remains a release limitation and is not
  treated as production qualification.
