# Profile access (UC15)

A current Heimdall identity can open an owned active profile by proving possession
of that profile's native scoped unlock key. Both `Master` and `PerProfile` bundles
use the existing `cerberus-proof-v1` protocol with operation `unlock-profile` and
scope `profile`. The account unlock key and other profiles' keys cannot authorize
this selection. Passwords, recovery secrets and private keys remain on the client.
Independent protocol and real-client approval remains pending; development-only
review deferral does not authorize a release.

## Challenge and local key loading

Choose the exact UTF-8 bytes of the future open body, for example
`{"expectedRevision":1}`, and calculate its SHA-256 digest as canonical unpadded
base64url. With a fresh identity bearer (signed `iat` less than 60 seconds old), send
`POST /api/profiles/{id}/access/challenges`:

```json
{"expectedRevision":1,"requestHash":"<32-byte SHA-256 in base64url>"}
```

No existing vault handle is needed. The `201` data has only `challenge` and
`profile`. The latter has `accountId`, `profileId`, `revision`, `envelope` and
`keyWrappers`; it contains no association IDs or underlying resource content.
This is an owner-only encrypted bootstrap. The client locally decapsulates the
Master bundle using its account keys, or unwraps the PerProfile password bundle,
and loads the independent scoped proof key. The server checks native public
bindings and signatures but cannot establish decryptability, password correctness
or correspondence between encrypted private material and public keys.

The random challenge expires after 60 seconds. It binds identity, account, profile,
profile content epoch, current profile revision (`protectionRevision`), null
`generation`, nonce and the original open-body digest. Do not reinterpret it as an
account challenge. Sign its existing native proof bytes using the scoped key.

## Open and consume

Send `POST /api/profiles/{id}/access` with the original body bytes and exactly one
of each header:

- `Authorization: Bearer <current Heimdall token>`
- `X-Cerberus-Challenge-Id: <canonical lowercase challenge UUID>`
- `X-Cerberus-Proof: <native 64-byte P1363 signature in unpadded base64url>`

The strict body has only required `expectedRevision`. Route IDs must be canonical
lowercase UUIDs. Both endpoints reject query parameters, unknown/case-mismatched or
duplicate body fields, numeric strings, compression, BOMs and invalid UTF-8. The
configured request byte limit applies. Never reserialize a signed body: whitespace
changes its digest.

The `200` data has only `accountId`, `vaultAccess`, `issuedAt`, `expiresAt` and
`profile`. Profile context has `profileId`, `revision`, `serverSequence`, `editedAt`,
`envelope`, `keyWrappers`, `recordIds`, `folderIds` and `collectionIds`. Typed IDs
reflect current visible links, including currently granted collections; hidden or
revoked links are omitted. No unwrapped resource keys or sharing grant blobs are
returned. Unlocking leaves profile/account ciphertext, revisions, edit times and
links unchanged.

`vaultAccess` is an opaque random 32-byte handle; only its SHA-256 verifier is
stored. Supply it as `X-Cerberus-Vault-Access` to supported operations. It authorizes
only this profile, its permitted current contents and existing visible links.
Profile reads/listing, updates, retaining/removing associations and trash enforce
that scope. Adding links outside the original selection or accessing another
profile fails; account-wide operations require an account-wide handle. This handle
is not an offline lease. Current policy, revocation generation, lifecycle and
expiry remain authoritative. `expiresAt` is null when renewal is disabled; otherwise
it uses the current account renewal interval. Trashing the profile revokes its
selected handles; restoring it must never revive them.

Account then profile locks, current native material checks and final statement-time
expiry checks serialize proof use with mutations. Challenge consumption and selected
session insertion commit together. Two concurrent submissions have one winner.
Failures roll back both. After an ambiguous/lost successful response, obtain a new
challenge and proof; consumed proofs cannot retrieve an earlier opaque handle.

## Failures and key isolation

| Status | Meaning |
| --- | --- |
| 400 | Malformed required body, route, digest or present proof header. |
| 401 | Missing/invalid identity, unfresh challenge admission, missing or rejected/expired/replayed proof. |
| 404 | Missing, foreign, trashed or terminal profile/account, or closing account. |
| 409 | Stale visible profile revision, content epoch or challenge policy/generation. |
| 413 | Configured aggregate request byte limit exceeded (empty response body). |
| 503 | Required native material is corrupt/ambiguous, or a dependency is unavailable. |

Every response is `no-store`; failures contain no access handle or partial data.
Ownership/lifecycle checks precede revision/native disclosure. Malformed wire input
is rejected before persistence. Challenges/opening fail closed if another retained
owned profile, including trash, has the same scoped verifier or corrupt verifier
metadata. Creation now rejects duplicate scoped keys with `400`; they must also be
distinct from account role pins. Cross-account reuse does not transfer authority.

For legacy duplicates, use account-wide access to manage/trash them and create
replacement profiles with fresh IDs and independent keys. Trashing alone does not
remove a retained-key collision. The server never regenerates client secrets.
Profile password changes are a future protection operation; this API opens existing
bundles. Such changes and restore must enforce key isolation, correctly rebind
native wrappers and revoke affected selected sessions. Existing account master
rewrap/rotation remains documented in [vault protection](vault-protection-api.md).

No schema migration or new runtime dependency is introduced. `ProfileId` on a
selected session remains nonnull immutable scope metadata. A future physical purge
must revoke/delete selected sessions or retain their dangling nonnull scope for
fail-closed checks: clearing it to null would turn it into account-wide access.
