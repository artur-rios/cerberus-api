# UC-02 scoped authentication design

Implement issue #3 and FR-AC-05–07 through anonymous `POST /api/auth/login` and
`POST /api/auth/2fa/verify`, both explicitly listed in the endpoint inventory.
The existing batch authorization replaces per-stage approvals with automated checks.
Independent security/client review remains deferred for development only.

Login accepts only email/password. Challenge completion accepts challengeToken and
exactly one nonblank code/recoveryCode. Reject unknown/duplicate members, invalid
types and invalid visible input before dependency calls. The configured Heimdall
scope is server-owned; never forward a caller-selected scope or service credential.

Extend the existing Heimdall adapter with typed login/challenge outcomes that
distinguish denied authentication (401), denied authority (403) and unavailable or
malformed required service (503). Retain existing nullable adapter methods for their
current callers. A completed result requires signature/issuer/audience/expiry/scope
validation plus current online identity revalidation before account lookup. Pending
MFA returns the opaque challenge state, no identity bearer and no account lookup.
Challenge completion follows the same completed-result validation as direct login.

A Domain authentication-store interface reads account context by the trusted public
Heimdall identity GUID, using PostgreSQL without tracking or writes. Active accounts
return only public account GUID/revision. Closure-pending accounts and accounts with
terminal tombstones return generic authentication denial, with no bearer/context.
Database failures return stable redacted 503; cancelled requests stay cancelled.

Ruling: an otherwise valid scoped identity with no Cerberus account returns its
Heimdall identity result with null account context. Absence is distinct from an
existing inactive account. This supports prospective-owner MFA completion before
UC-01 binding and non-human scoped identities before their explicit grants exist;
it grants no vault access or account rights. The current inspected Heimdall login
contract is scoped person email/password, so this route does not invent an
application-secret login contract. Resource authorization remains with later grants.
Cost if wrong: onboarding context may need an explicit state in a later client
contract; no content/session or independent identity token is exposed.

Ruling: closure-pending accounts are denied by ordinary UC-02 login under FR-AC-06.
UC-07 separately uses fresh identity authentication through the retained Heimdall
adapter for its restricted cancellation flow; this route never opens that access.

Use first-party DataOutput and mediator dispatch. Success returns the validated
Heimdall login/challenge result and nullable public Cerberus context. No vault
session, content, keys, password, internal identifier or independent token is issued.
No-store applies to every success/failure. Logs retain the existing redacted policy.

| Flow | Evidence |
| --- | --- |
| Main | Controlled actual HTTP Heimdall login plus real PostgreSQL active account context; original signed token returned, no vault handle. |
| AF-01 | Validator and real HTTP malformed/unknown/duplicate tests prove no dependency calls or writes. |
| AF-02 | Invalid/foreign/expired/challenge bearer, current deleted identity, inactive account and forbidden dependency fail closed with 401/403. |
| AF-03 | Actual controlled service outage/malformed reply and PostgreSQL connection failure return redacted 503 without token/context. |
| AF-04 | MFA login returns challenge only; separate verification endpoint accepts code or recovery code and validates completed identity; rejected/reused challenge gives 401. |
| AF-05 | Missing identity and wrong password yield the same generic 401 body and no account context. |

Alternatives: minting a Cerberus identity token duplicates Heimdall authority;
returning a pending challenge as a bearer would bypass MFA; looking up an account
by supplied email would trust an unproved identity. Typed dependency outcomes and
public-ID account lookup avoid these paths.
