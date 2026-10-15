# Controlled Heimdall integration

Contracts were inspected at Heimdall revision
`e193520df928b13fdd051b03c3cf7b0cea88ede9`; controlled fixtures exercise the observed
`success/errors/data` JSON envelope. Unknown, oversized, malformed, timed-out or rejected
responses fail closed. Redirects are disabled; the whole response has a ten-second bound,
including its body. The typed HTTP client emits no HTTP request/response logs.
Protected requests distinguish current identity denial (401) from unavailable/incompatible
required identity service (503 `identity_unavailable`). Both outcomes prevent execution;
upstream error text is never returned.

- Login: `POST /api/auth/login`, email/password and configured scope in the body.
- Challenge completion: `POST /api/auth/2fa/verify`, challenge and code/recovery code in the
  body; never send a challenge token as authenticated bearer identity.
- Registration: `POST /api/scopes/{configuredScope}/persons`, using only a validated narrow
  scope-owner service JWT. Role 2 must own exactly that scope. No global-admin or unscoped
  person-creation fallback exists; the returned user must be role 3 in the configured scope.
- Online authorization: validate HS256 signature, issuer, audience, expiry, configured scope
  and nonempty public identity GUID, then `GET /api/persons/{identity}` with that user token.
  Require the active role-3 identity and current scope to match before executing a protected
  endpoint. A missing current deletion flag is not interpreted as active.
- Restore: retrieve the configured scope and service person with the narrow credential;
  require a nondeleted scope and service identity and current ownership in both responses.

Credentials are never returned or logged. Header/body transport is intentional; the API
does not mint identities. Public endpoints require explicit anonymous metadata; unknown
routes still return 404. Business ownership/profile/grant checks remain with the owning use
case. UC-01 exposes `POST /api/accounts`; UC-02 exposes `POST /api/auth/login` and
`POST /api/auth/2fa/verify`. Completed authentication revalidates current identity and
returns permitted public account context; pending MFA returns challenge state only.
An unbound identity receives no account rights; inactive/erased accounts are denied.
Neither endpoint opens vault access or mints an independent identity token.

## Application authentication (UC43)

Application contracts were inspected at Heimdall develop merge
`bedd912cfaa89509221d089ba992499b5a7b587a` ([PR185](https://github.com/artur-rios/heimdall-api/pull/185)).
Heimdall issues application credentials and `POST /api/applications/token` exchanges
an application ID, scope ID and secret for a short-lived application JWT. Cerberus
never accepts the application secret or mints the token. Provision
`CERBERUS_AUTH_APPLICATION_AUDIENCE` to the exact Heimdall application audience;
it is required and must differ from `CERBERUS_AUTH_AUDIENCE` for people.

Both audiences use configured HS256 trusted issuer keys. The current key is
`CERBERUS_AUTH_VALIDATION_SECRET`; optional
`CERBERUS_AUTH_VALIDATION_SECRET_PREVIOUS` retains the immediately previous
trusted key during rotation. Each key has at least 32 printable ASCII characters.
The durable registration fingerprint secret must differ from both. Provision
these protected values consistently across replicas before rotating Heimdall.
Retire the previous key explicitly after the accepted token lifetime window.

Only `GET /api/software-secrets` accepts an application bearer. Require the exact
application audience, issuer, `tokenKind=application`, canonical application,
scope and credential IDs, signed numeric `iat`/`nbf`/`exp`, zero clock skew and no
person-role authority. People and service validators reject any `tokenKind`.
Every sensitive retrieval forwards the original bearer to Heimdall
`GET /api/applications/self`, requires the current active application/scope/credential
and JWT-derived expiry to match, and caches no positive result. A refusal returns
401; unavailable, incompatible, redirected or rate-limited validation returns503.
The dedicated client disables redirects and HTTP logging, bounds the complete
response, and rechecks token lifetime after network waits. Scope credentials used
for human grant administration never substitute for the application's own token.
See the [retrieval contract](../security/software-secret-retrieval-api.md).
