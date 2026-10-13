Attempting to perform the InitializeDefaultDrives operation on the 'FileSystem' provider failed.
Hello world!
[ERROR] Failed on checkpwshupdate command
Program 'winget.exe' failed to run: An error occurred trying to start process 'C:\Users\Artur\AppData\Local\Microsoft\WindowsApps\winget.exe' with working directory 'D:\Repositories\cerberus-api'. The file cannot be accessed by the system.At C:\Users\Artur\OneDrive\Documentos\PowerShell\Microsoft.PowerShell_profile.ps1:19 char:18
+ . eckOutput = winget list --id Microsoft.PowerShell --upgrade-available .
+               ~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~~.
# UC-42 Grant software access design

## Binding and scope

Implement GitHub issue #43, UC-42 main flow and AF-01 through AF-05,
FR-SW-01 and FR-SW-02, and the applicable BR-10, BR-15 and BR-28 rules.
An authenticated owner creates an explicit read-only grant for one active
Heimdall application identity over an explicit nonempty set of owned records
and collections. The client supplies a public recipient key and one native
recipient envelope for every target; Cerberus validates and stores those opaque
materials but never creates, unwraps or receives a usable decryption key.

Add the domain contract, command boundary, Heimdall application lookup,
normalized PostgreSQL aggregate, HTTP route, migration, tests, OpenAPI and
operational documentation. UC-43 owns software authentication and ciphertext
retrieval. UC-44 owns revocation. UC-48 owns durable offline visibility change
delivery. No plaintext endpoint, general application-management proxy, target
update endpoint or software credential is added.

The base is current `origin/develop`, commit
`d730c91c1b3577802fc12568959054900d14b71d`, which contains the completed
UC-37 merge plus the subsequent attribution-license update.

## Public contract

`POST /api/software-grants` requires current Heimdall human authentication,
exactly one canonical `X-Cerberus-Vault-Access` handle, no query, and bounded
strict UTF-8 JSON. The media type is exactly `application/json`
case-insensitively, optionally with a valid UTF-8 charset. Compression, BOMs,
invalid UTF-8, duplicate, unknown, case-forged or missing fields and other JSON
media types are rejected. All responses are no-store; oversized input returns an
empty 413 through the existing strict request pipeline.

The body has exactly five required properties:

- `grantId`: client-chosen canonical nonzero UUID.
- `softwareIdentityId`: public UUID of an active Heimdall application in the
  configured Cerberus scope.
- `expectedRevision`: explicit `null` for a new grant, or the current positive
  safe revision for reactivating the same revoked grant.
- `recipientKey`: strict P-256 public JWK (`crv`, `kty`, `x`, `y`) whose private
  counterpart remains with the software client.
- `targets`: a nonempty array of unique target objects. Each has exactly `kind`
  (`record` or `collection`), a canonical nonzero `id`, and a strict
  `cerberus-recipient-wrap-v1` `keyEnvelope`.

For an initial grant, every envelope binds grant revision 1. For an explicit
reactivation, every envelope binds `expectedRevision + 1`. Each envelope also
binds the owner account public ID and current author pin, exact target kind and
public ID, current target key epoch, grant ID, Heimdall software identity and the
submitted recipient key fingerprint. All target envelopes use the same grant
identity, revision, software identity and recipient key. Shape-valid but
incorrectly bound, stale-key, wrong-signature or wrong-pin caller material is 400
`validation_failed`. The server does not fetch, generate or return software
private-key material.

Success is 201 `software_access_granted` with exactly `grantId` and `revision`.
It does not echo targets, account metadata, application metadata, public keys,
encrypted resource bodies, protection material or envelopes. The target IDs are
already known to the caller and are persisted for UC-43.

## Heimdall identity boundary

Cerberus extends `IHeimdallClient` with a narrow application lookup using the
configured scope and constrained service credential:
`GET /api/scopes/{scopeId}/applications/{softwareIdentityId}`. The response must
match the requested application ID and configured scope and must explicitly show
that the application is not deleted. Cerberus ignores the application name,
owner and timestamps and never forwards arbitrary application-management
operations.

The current Heimdall application model is an identity record only: it has no
authentication public key or client credential. Therefore UC-42 deliberately
uses client-side key provisioning. The owner submits the software public key and
signs each recipient envelope so the persisted binding covers both the Heimdall
application ID and exact key fingerprint. Heimdall existence does not by itself
grant decryption capability, satisfying FR-SW-02 and BR-15. UC-43 must later
authenticate the same application identity and match it to an active grant.

A missing or deleted application is a nonrevealing 404. A malformed successful
response, unexpected identity/scope, invalid configured credential, timeout,
transport failure or other unavailable upstream state is 503 and creates no
grant. Caller cancellation propagates. Lookup happens before the database
transaction so no PostgreSQL lock is held during the network call. Deletion after
the lookup cannot be made atomic across services; UC-43 revalidates the current
software identity before any retrieval, so a stale grant alone never authorizes
content.

## Authority and target visibility

The actor must map to an active, nonterminal Cerberus account and hold a current
vault-access session. Account-wide Master access permits active owned records and
collections. Per-profile access permits only owned targets visible in the exact
selected active profile: a collection requires its current profile association;
a record requires the existing selected-profile visibility rules, including
valid active ancestry where applicable. Selection is never widened or cleared.

Only the owner of every target may delegate it. Active foreign targets visible
through a collection grant do not become delegable. A foreign target returns 403
only after the one relevant native grant, target epoch/envelope, recipient
identity and current owner/recipient pins validate. Hidden, revoked, unselected,
terminal, trashed, absent or unrelated targets return nonrevealing 404. Corrupt
relevant native evidence or malformed required stored owner/target material is
503. These classifications prevent probes from disclosing unrelated account
resources.

The entire requested target set is authorized as one aggregate. Duplicate target
tuples, empty sets, invalid kinds/IDs or inconsistent envelopes return 400 before
mutation. If any target is not delegable, no grant or target row is written.

## Persistence model

Use a normalized aggregate instead of JSON targets or one grant per target:

- `SoftwareGrant`: internal ID, unique public ID, owner account FK, Heimdall
  software public ID, serialized validated recipient public key, Active/Revoked
  state, increasing revision and concurrency stamp.
- `SoftwareGrantRecord`: grant FK, record FK and serialized recipient envelope;
  unique `(grant_id, record_id)`.
- `SoftwareGrantCollection`: grant FK, collection FK and serialized recipient
  envelope; unique `(grant_id, collection_id)`.

The child rows are the explicit authorized scope. Foreign keys prevent orphaned
targets and make UC-43 filtering indexable. Per-target envelopes remain necessary
because each resource has its own identity and key epoch. Multiple grants may
reference the same software identity or target; authorization is the union of
currently active grants, and UC-44 revokes one grant aggregate by public ID.

An active public grant ID conflicts even if its submitted bytes match: POST is
not an idempotent replay and there is no UC-42 update operation. A revoked grant
can be reactivated only when its ID, owner, Heimdall identity and expected
revision match exactly. Reactivation advances the revision once, replaces the
recipient key and the entire target/envelope set, becomes Active and changes no
vault resource. A missing/stale expectation, changed identity, active grant,
public-ID collision, terminal reservation, unsafe/exhausted revision or concurrent
winner returns 409 and preserves the winner.

The migration adds indexes for public grant lookup, owner/application lookup and
both target reverse lookups. Account and target deletion behavior follows the
existing restricted lifecycle and terminal-erasure rules; no cascade may bypass
trash, revocation or erasure bookkeeping.

## Atomic write and lock protocol

After successful Heimdall lookup, an initial read-only snapshot classifies actor
authority, selection, all targets and the candidate grant. Hidden denials do not
lock unrelated resource graphs. The successful owner path locks in a canonical
order:

1. owner account row `FOR UPDATE`;
2. actor vault-access session `FOR UPDATE`;
3. selected owned profile, when present, `FOR NO KEY UPDATE`;
4. every target row ordered by `(kind, public_id)` `FOR UPDATE`;
5. existing/colliding software-grant rows ordered by public ID `FOR UPDATE`.

After every potentially waiting phase, re-evaluate database-time actor state,
session, selection, target ownership/lifecycle/visibility, key epochs and captured
grant state. Validate owner protection once after the account lock and validate
every submitted recipient envelope against the current target state. The guarded
insert/reactivation reasserts the captured graph and is the authorization
linearization point. Natural session expiry after that statement does not cancel
an already authorized atomic operation.

The transaction writes one grant row and its exact child target set. Records,
collections, profiles, memberships, ciphertext, envelopes, epochs, revisions,
sequences and owner metadata remain unchanged. Reload and byte-validate the grant,
recipient key and all target/envelope rows before commit. Any write, replacement
or post-write verification failure rolls back the whole aggregate. PostgreSQL
uniqueness/concurrency violations map to 409; database, serialization, timeout or
post-write corruption maps to 503.

## Flow mapping and verification

- Main: strict request -> active Heimdall application -> current owner scope ->
  canonical target locks -> native envelope verification -> atomic grant -> exact
  201 identifier/revision.
- AF-01: malformed transport, identity, nullable CAS form, target set, public key
  or envelope returns 400 with no external write or database mutation.
- AF-02: inactive/hidden actor, software identity, target or selected visibility
  returns nonrevealing 404.
- AF-03: missing human identity/access returns 401; invalid current owner access or
  validated visible foreign redelegation returns 403.
- AF-04: active/colliding grant, stale reactivation, changed aggregate, exhausted
  revision or concurrent winner returns 409.
- AF-05: Heimdall, database or required stored-state failure returns 503, fails
  closed and leaves no partial grant. Caller cancellation propagates.

Command tests cover the exact five-field model, explicit-null presence,
initial/reactivation revision relations, public-key and target validation, trusted
context, error allowlist and exact two-field output. Heimdall adapter tests cover
the inspected application response, configured scope/identity binding, deleted
and missing applications, status mapping, malformed bodies, size/media limits,
timeouts and cancellation.

Real PostgreSQL tests cover wide and selected owner success, mixed targets,
foreign/hidden/trashed/terminal targets, native foreign denial validation,
initial/active/revoked/collision paths, target/key/epoch/signature failures,
canonical locks, write faults, post-write rollback and exact no-mutation
invariants. Concurrency tests cover same-ID create/reactivation races,
session/profile/target lifecycle changes at every wait phase, opposite target
orders and coexistence with record/collection update, membership, trash and
protection writers without deadlock.

HTTP tests cover strict transport, exact response shape, no-store behavior,
upstream classification and mixed-target persistence using real native envelopes.
Full six-family tests, per-production-assembly 90% line coverage, aggregate branch
coverage, locked restore, dependency audit, native protocol suites, specs,
OpenAPI drift/shape, exact-head CI and whole-branch review are required before
merge.

## Deferred release gates

The API validates the native owner signature, current author pin and submitted
software key fingerprint, but Heimdall currently does not register or authenticate
that cryptographic key. Independent protocol/security/client qualification and a
deployable Heimdall software-authentication contract remain release gates. The
approved development review deferral permits backlog implementation into
`develop`; it does not permit production release without those reviews.

## Review priorities

1. Heimdall application existence is checked narrowly and no database lock spans
   the external call; an identity record alone never grants decryption.
2. Every target is owned, currently visible in the exact vault scope and bound to
   the current epoch, software identity, software key and owner author pin.
3. One normalized grant plus its complete record/collection target set commits or
   rolls back atomically, with deterministic locks and no vault-resource mutation.
4. Initial, duplicate-active and revoked-reactivation semantics use an explicit
   nullable revision and preserve concurrent winners.
5. Status classification and the minimal two-field success response do not expose
   unrelated accounts, application metadata, targets, keys or envelopes.
