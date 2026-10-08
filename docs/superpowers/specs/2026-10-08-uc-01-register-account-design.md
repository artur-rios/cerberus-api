# UC-01 account registration design

Implement issue #2 and FR-AC-01 through FR-AC-04 through `POST /api/accounts`.
The existing unattended batch authorization covers design, implementation, verified
merges and issue closeout. The owner's 2026-10-08 instruction defers independent
protocol review for development; it does not claim review approval or authorize release.

The request contains a client-chosen account GUID, operation GUID, encrypted details
envelope and Heimdall name/email/password registration input. Optional bearer identity
proof is accepted only after signature/scope and current identity revalidation. The
API never accepts a caller-selected Heimdall identity GUID. Unknown JSON members,
malformed canonical base64url, unsupported envelope format, invalid nonce/tag sizes,
epochs outside 1..9007199254740991 and noncanonical identifiers produce 400 without writes.
The six-field `cerberus-content-v1` envelope includes a canonical 32-byte `keySalt`.
JSON field casing, integer types and duplicate/unknown-member rules match the protocol.

A command handler validates with FluentValidation, fingerprints the operation input
using an independent durable registration HMAC key and calls a Domain registration-store interface. Persisted
registration operations contain public identifiers, opaque envelope bytes and keyed
fingerprints only. Identity credentials remain transient. Replicas
use the same registration key across routine identity-key rotations; operators
preserve it with protected restore configuration. PostgreSQL transactions
serialize attempts for the same normalized scoped email, persist the pending operation
before calling Heimdall, and atomically bind the identity and account after success.
Unique constraints cover operation GUID, account GUID and identity GUID. Terminally
erased identifiers cannot be reused. Identical completed retries require fresh proof
and return the original public account result; changed input or an already-bound
identity conflicts without replacing ciphertext.

Heimdall establishment first accepts an explicitly supplied valid current user token,
or performs scoped login using the submitted identity credentials. A completed login
is revalidated and proves an existing identity; a two-factor challenge requires
completion before linking. A rejected login permits constrained scoped creation;
an unavailable or malformed login never triggers creation. A timed-out creation
leaves a durable pending operation. On retry, scoped login reconciles a successful
upstream create whose response was lost. A duplicate-email creation conflict requires
identity proof rather than linking by email. No Cerberus identity token or vault
session is issued by registration.

Responses use the first-party DataOutput envelope and stable error/status mapping.
Success returns only the public account GUID, revision and `protection_required`
onboarding state. 201 means created; 200 means an identical completed retry.

| Flow | Observable behavior and verification |
| --- | --- |
| Main | One real PostgreSQL account linked to the trusted Heimdall identity; opaque envelope preserved; no vault keys or internal ID returned. |
| AF-01 | Validator and HTTP tests reject malformed/unknown input before persistence or dependency calls. |
| AF-02 | Missing scope or terminal account identifier returns nonrevealing 404. |
| AF-03 | Invalid identity proof, pending MFA or denied service authority returns 401/403 without account creation. |
| AF-04 | Identity and database outages return redacted retryable 503; winning state is preserved. |
| AF-05 | Failure after upstream creation leaves pending local operation; retry proves identity and commits one account without a second create. |
| AF-06 | Existing identity requires validated current proof; different operations cannot create two accounts for it. |

Alternatives considered: storing identity credentials to replay upstream requests
would violate the storage boundary; email-only lookup would not prove ownership;
an in-memory idempotency cache would lose reconciliation after a restart. Durable
PostgreSQL operation records plus actual identity proof satisfy those requirements.
