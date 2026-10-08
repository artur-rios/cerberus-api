# UC17 list permitted records

Issue 18 / FR-RC-02 / BR08, BR09 and BR28. Return active encrypted records visible
through current owned or explicitly granted scope, with bounded opaque pagination.
Implement the main flow and AF01–04. Existing unattended batch authorization replaces
repeated stage menus; ordinary review, tests and required CI remain mandatory.
Independent protocol-security and real-client approvals remain development-only
deferred and release-gated.

## Contract

`GET /api/records` accepts only single `pageSize` and `cursor` query values, one
canonical `X-Cerberus-Vault-Access` header, and current Heimdall identity. Reject GET
bodies, unknown/duplicate query names, duplicate headers and noncanonical decimal
page sizes. Default size is min(50, configured MaxPageSize), positive and bounded by
that configuration. Do not introduce a public profile override or plaintext search.

The success data contains `items` and nullable `nextCursor`. Each item has exactly
`recordId`, `revision`, `serverSequence`, `editedAt`, `envelope`. Do not disclose
internal ownership IDs, owner profile links, parent folders outside the current
scope, recipient key material, totals or plaintext content. Native envelope contents
remain opaque; clients enforce encrypted name/template/field rules. Validate each
returned native envelope and metadata before returning any item: positive safe
counters, nonzero public ID, supported native format, UTC edit time of at least ten
ticks and exact microsecond precision. Corrupt stored results return503 without a
partial payload.

Domain contracts: RecordListRequest(Actor,AccessVerifier,PageSize,After,Boundary?);
RecordListRow(RecordId,Revision,ServerSequence,EditedAt,byte[] Envelope);
RecordListPage(Items,Boundary,HasMore); IRecordListStore.ListAsync(request,ct).
Query: ListRecordsQuery(actor,access,pageSize?,cursor?), ListRecordsHandler,
RecordListItem and RecordListOutput(items,nextCursor), RecordListCursor and stable
RecordListMessages. Data validates current authority; Query validates public output.

## Visibility and lifecycle

Account-wide access sees all active owned records and records included in active
collections granted to the recipient. Selected access sees owned direct record
links, records in the selected profile's owned directly linked folders and their
owned descendants, and records in owned or currently granted collections actually
linked to that selected profile. Collection membership includes directly linked
records and records under directly included folders and their descendants. Both
read-only and read/write grants permit reads. Deduplicate overlapping admission
routes before pagination. A grant never transfers ownership or exposes another
owner's private profile organization.

The current actor account must be active and nonterminal. The current session must
match policy/generation, not be revoked, have issuedAt <= database statement time,
and meet exclusive expiry or disabled-renewal/null-expiry policy. A selected
ProfileId must still identify a current active nonterminal profile owned by that
actor. Null continues to mean account-wide authority; purge must never widen a
selected session by nulling its profile ID.

Every record, collection and resource-owning account must be active and nonterminal.
Current grants must bind the correct recipient and collection and have an admitted
RO/RW access value, positive safe revision, active state and nonterminal grant ID.
All folder ancestors must be active, nonterminal, owned by the record owner and
acyclic. Hidden or trashed ancestry omits the record; a relevant corrupt cycle
returns503 without hanging. Corruption outside the permitted scope must not create
an availability/existence oracle. No foreign account/profile locks are taken.

## Membership schema

The existing repository has profile links but no representation for collection
members. Add CollectionRecord and CollectionFolder typed association rows now;
otherwise recipient listing could not implement its specified main flow. Each has
AccountId, CollectionId and its typed target ID, a unique collection/target tuple
and composite same-owner foreign keys to collection and target. Deleting a target
or collection removes association rows, never the other resources. Existing active
public IDs of different resource kinds remain independent. Introduce one additive
EF migration and snapshot, with no dependency changes. UC34 later implements public
membership mutation; no speculative membership endpoint belongs here. Deploy the
additive migration before serving the new read path.

## One-snapshot read and native grants

Use one PostgreSQL statement for actor/session admission, eligible grant/member
scope, iterative folder ancestry, permitted inventory, ordering integrity, initial
boundary, bounded page and HasMore. A recursive CTE tracks visited internal folder
IDs, matches owners and terminates cycles. Apply all visibility and trash predicates
before keyset filtering or Take; never fetch a page and remove unauthorized entries
in memory. Reject zero/unsafe/duplicate sequence values in the permitted inventory
before pagination because keyset filtering would otherwise hide corruption.

Snapshot applicable native grant evidence with the same SQL statement. Validate
current visible foreign grants that contribute permitted content using the existing
CollectionGrantBinding.TryReadPins/IsBound: collection epoch/envelope, grant public
ID/current revision, owner public ID/author pin and recipient identity/recipient pin.
Read current public protection material and native wrapper bytes in that snapshot;
no post-query permission lookups. Only current in-scope grants contributing active
content are inspected; revoked/hidden/unselected unrelated grants are omitted
without parsing their crypto metadata. Invalid relevant native bindings return503
and no data. Do not return grant blobs or claim client decryptability approval.

The internal snapshot transport may use typed JSON aggregates with hexadecimal
binary fields, decoded strictly inside Data. It is not an external protocol and
never includes raw private keys. All user values use SQL parameters. The final page
contains only the five public item fields.

## Pagination and failures

Use the existing profile-list cursor construction with a separate versioned record
purpose: AES256-GCM over actor, hashed handle, page size, after sequence and initial
permitted maximum sequence; derive its key from the dedicated registration
fingerprint secret by HMAC-SHA256 and authenticate the purpose. Canonical bounded
base64url, strict tuple parsing and actor/handle/size binding reject substitution,
tampering and cross-purpose profile cursors. Every page rechecks current visibility
and authority. Later inserts exceed the initial high watermark; concurrent edits
can move records outside this ordinary list and require a refresh. This is not a
durable synchronization snapshot; UC48 defines synchronization separately. Key
rotation invalidates old cursors with400, then callers restart page1. Avoid size+1
overflow by querying HasMore separately within the same statement.

200 includes an empty list when nothing is visible. Invalid visible input400;
missing/invalid identity or missing access401; revoked/expired/stale/invalid selected
access403; absent/closing/terminal actor account404; identity/persistence unavailable
or relevant corrupt stored state503. The common payload limit can return an empty413
for an oversized request before GET-body rejection. Lists omit hidden targets rather than exposing
their existence. Error messages are allowlisted; null/error-with-data/corrupt store
results never escape. Cancellation propagates. Reads never mutate content, sessions,
proofs or memberships; all responses are no-store.

## Verification and delivery

Test typed membership constraints and upgrade from the previous migration while
preserving existing ciphertext/metadata. Real PostgreSQL tests cover owner/selected
direct and descendant scope, native RO/RW grants and selected collections, same-owner
membership, lifecycle omission, grant revocation between pages, single-statement
snapshot/no N+1, current expiry/policy/generation, scope-relevant cycles, unrelated
corruption, deduplication, boundary/gaps and corrupt ordering before pagination.
Query tests cover purpose-separated cursor/context/tampering/limits, strict opaque
projection, safe metadata and error/cancellation handling. Actual HTTP tests use
records created through UC16 and native selected handles issued through UC15,
covering all visible syntax and AF failures, pagination, sharing and no-store.

Update API/operations docs, changelog, README UC17 Done/M03 nine and actual OpenAPI.
Run all six families unfiltered with zero failures/skips and >=90% line coverage;
report branches. Locked restore, migration evidence, NuGet/native dependency audit,
native corpus, helper/spec/OpenAPI/diff checks remain required. One fresh ordinary
whole-branch review, one TDD blocking-fix pass if needed, exhaustive declined-scope
rulings with costs, exact-head required CI and normal verified merge/DoD precede UC18.

Gate1: fresh mergedUC16 base26ec7e2 equals reviewed/tested2832tree; UC17main/all4AF, FRRC02/BR08,09,28, commonmetadata/typedmembership/grantmodel/authorization/testing/workflow/tech/ops freshlyloaded. Preferred design and four-task test-first plan resolve scope, persistence, grants and cursor choices before tests. Existing unattended authorization applies.
