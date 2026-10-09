# Profile retrieval

`GET /api/profiles/{id}` requires a current Heimdall identity and one
`X-Cerberus-Vault-Access` header. The ID must be a nonzero canonical lowercase
D-format public GUID. No query parameters or body are accepted, including unknown
length HTTP2 bodies. Duplicate or malformed access headers, malformed IDs and
unsupported input return400; a missing identity or access handle returns401.

A current account-wide session can read an owned active profile. A current selected
session can read only its owned active selection: guessing another own profile or a
foreign, trashed, erased or absent target returns the same404 with no data. Invalid,
expired, revoked or policy-stale sessions return403; an absent/closing/erased account
returns404. Required dependency outages or corrupt permitted stored data return503
without partial payloads. Every request checks current state in one SQL snapshot.
Reads do not change profiles, keys, account state or access sessions. Responses use
no-store.

The response data contains profileId, revision, serverSequence, UTC microsecond
editedAt, opaque envelope/keyWrappers and visible recordIds/folderIds/collectionIds.
It excludes internal IDs, account contents and plaintext names or keys. List and
retrieval share strict stored metadata validation, including the authenticated
identity as owner-wrapper recipient and matching profile/content-epoch context.
The server cannot establish client-only encrypted-content decryptability.

Collection grants expose included collection content through a recipient's own
profiles; they do not expose somebody else's unrelated profile metadata. The three
arrays contain actual stored links filtered for current resource/owner/grant visibility
in that same statement. Revoked, trashed, erased and closing-owner links are omitted.
Creation accepts valid nonempty sets using the [association rules](profile-associations.md).
Selected-session proof minting and cross-profile key isolation belong to UC15.
Independent security/client approvals remain deferred for development; release gates
remain pending.
