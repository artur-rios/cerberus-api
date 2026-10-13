# Offline policy API

`GET /api/accounts/me/offline-policy` returns the current owner's saved renewal
policy. Authenticate with a current scoped Heimdall bearer token and supply one
`X-Cerberus-Vault-Access` header. The handle must identify a current account-wide
session or a session selecting an active, unerased profile belonging to that
same account. A selected profile never changes the owner of the policy returned.

The endpoint accepts no query parameters or request body. Malformed, empty or
duplicate handles return `400 validation_failed`; a missing handle returns
`401 vault_access_required`. Current identity denial returns 401; unavailable
identity validation returns `503 identity_unavailable`.

Success uses the existing `DataOutput` envelope with message
`offline_policy_found` and exactly these data fields:

```json
{
  "renewalEnabled": true,
  "renewalInterval": "1.00:00:00",
  "policyRevision": 1,
  "revocationGeneration": 1
}
```

`renewalInterval` is a .NET TimeSpan string (`[-][d.]hh:mm:ss[.fffffff]`);
an enabled policy must have a positive duration. A newly registered account
defaults to 24 hours. Saved positive intervals are returned without rounding or
replacement by the default. Disabled renewal returns `renewalEnabled: false`
and an explicit `renewalInterval: null`. Policy and revocation versions are
positive integers. Invalid saved policy fails closed with
`503 persistence_unavailable`.

Inactive, absent or terminally erased accounts return nonrevealing
`404 not_found`. Revoked, future-issued, expired, stale-policy or stale-generation
sessions and hidden or foreign selected profiles return `403 vault_access_denied`.
Expiry is exclusive. Disabled renewal requires a session without an expiry.
Required persistence failures return `503 persistence_unavailable`.

All policy and authorization fields are read from one database snapshot. This
read selects no account content, client ciphertext, encrypted wrappers or
credentials and does not mutate account/session state. Responses use `no-store`.
It issues no offline lease; clients continue to enforce their last issued policy
until reconnection or its existing expiry. Policy changes and lease renewal are
separate use cases.

UC-43 remains blocked by the missing Heimdall application-authentication contract,
tracked by [Heimdall #178](https://github.com/artur-rios/heimdall-api/issues/178).
The owner authorized moving into M-05 while this external prerequisite remains
open. This human-owner metadata endpoint does not depend on software credentials.
Independent protocol/client review remains deferred for development only and is
still required before release.
