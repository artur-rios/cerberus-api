# Retrieve software secrets (UC43)

`GET /api/software-secrets` releases one explicitly permitted encrypted target to
an authenticated Heimdall application. Send its application JWT in `Authorization:
Bearer …`; no human vault handle or proof is accepted. See the
[application authentication configuration](../operations/heimdall.md#application-authentication-uc43).

Query fields are single-valued and case-sensitive:

| Field | Contract |
| --- | --- |
| `grantId` | Required canonical lowercase nonzero dashed software-grant UUID. |
| `kind` | Required exact `record` or `collection`. |
| `id` | Required canonical lowercase nonzero dashed target UUID. |
| `collectionId` | Optional canonical collection UUID, only for a record retrieved through that explicitly granted collection. |

Bodies, duplicate/unknown query fields, alternate UUID forms and `X-Cerberus-*`
human authority headers return400 `validation_failed` after authentication.
An oversized declared body is rejected by the shared request-size guard before
authentication with an empty413 response and no provider call. Invalid application tokens
return401 before any live provider call. Valid transport performs a fresh
`/api/applications/self` check for every request using the original bearer.
Heimdall401 returns401 `authentication_required`; unavailable or incompatible
responses return503 `identity_unavailable`. No provider detail is exposed.

The store derives the owner from the exact current grant and application ID.
The account, grant and selected target must be active. Direct reads require that
exact record/collection target and its own native recipient envelope. A member
record read requires the named explicitly granted collection, current direct or
folder-inherited membership and complete active record ancestry. A collection
root does not authorize an unrelated record or produce a separate record root.
The response includes the selected encrypted collection alongside the requested
record; the client uses its existing opaque collection member-key manifest.
No other membership paths, grants or resources are enumerated.

Success is200 with the standard `success/errors/messages/timestamp/data` envelope;
`messages` contains `software_secret_found`. The seven required `data` fields are:

| Field | Contents |
| --- | --- |
| `ownerAccountId` | Public owner UUID used for native signature verification. |
| `grantId`, `grantRevision` | Exact current software-grant identity and revision. |
| `target` | `kind`, `id`, `revision`, `keyEpoch`, native `envelope` ciphertext. |
| `provisioning` | `resource` with the same five fields and signed native `keyEnvelope`. Direct reads repeat the target; member reads carry the selected collection. |
| `recipientKey` | Stored public P-256 software recipient JWK. |
| `authorKey` | Independently pinned current owner public author JWK. |

The generated contract requires the new output, target and provisioning fields.
Shared native envelope/JWK schemas still lack complete requiredness annotations;
clients must enforce the complete native wire contract as part of qualification.

Native ciphertext and recipient-wrap formats are unchanged. The signed wrapper
must bind the exact owner, provisioning kind/ID/epoch, grant/revision, application
and both public-key fingerprints. The server never receives usable private or
plaintext keys. It returns no account envelope, protection material, sessions,
credentials, personal identity details or unrelated metadata.

Missing, revoked, trashed, terminal or hidden authority returns404 `not_found`.
Incomplete ancestry is hidden; a visible active ancestry cycle, invalid required
native evidence or unavailable persistence returns503 `persistence_unavailable`.
No failure includes partial ciphertext. Owner → selected collection → record →
grant locks agree with native writers. Database snapshots after waits and before
release recheck expiry, lifecycle, membership and exact stored evidence. Expiry
at or before the final database statement returns401. Reads mutate no vault data.

Every response has `Cache-Control: no-store`, without ETag or Last-Modified;
conditional request headers cannot bypass live authorization. Live identity and
database checks have separate authorization boundaries: revocation after a
completed check cannot recall an already authorized response. Every next request
checks again, so subsequent online reads honor current revocation. Already-held
software ciphertext and provisioned keys cannot be recalled. Compliant offline
clients follow separately issued offline policy; UC43 does not validate or issue
offline leases and enables no writes. Independent release security/protocol/client
qualification remains separately required.
