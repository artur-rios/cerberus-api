# Controlled Heimdall integration

Contracts were inspected at Heimdall revision
`e193520df928b13fdd051b03c3cf7b0cea88ede9`; controlled fixtures exercise the observed
`success/errors/data` JSON envelope. Unknown, oversized, malformed, timed-out or rejected
responses fail closed. Redirects are disabled; the whole response has a ten-second bound,
including its body. The typed HTTP client emits no HTTP request/response logs.

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
case, and this bootstrap does not implement login or account-registration endpoints.
