# UC-31 — Get collection

## Intent and traceability

Implement issue #32, UC-31 and FR-CL-03: an owner or native collection
recipient retrieves one currently accessible encrypted collection by public
identifier, with only relationship identifiers visible to that actor. Preserve
BR-05 opaque client ciphertext, BR-06 authenticated versioned envelopes, BR-28
included-content isolation, FR-PR-10 selected profile scope, FR-CL-07/08 typed
direct membership and descendant visibility, the authorization matrix, and the
current native collection-grant binding.

The authorized all-open-issues batch replaces repeat approval gates with written
design/plan self-checks, TDD, fresh full verification, ordinary independent review
and exact-head CI. Independent formal security/protocol and external-client
qualification remain deferred only for `develop`; release, `main`, tag and
deployment gates remain mandatory.

## Approach

Follow the existing collection-list admission/projection and record/folder detail
contracts with dedicated collection-read types. Reuse `CollectionListRow`,
`CollectionProjection` and unchanged `CollectionGrantBinding`. One target-first
PostgreSQL statement captures current authority, the requested collection, its
actor-visible direct relationships, relevant member ancestry and native evidence.
No migration, new package, algorithm, lock, mutation, whole-inventory traversal or
plaintext parsing is introduced.

## HTTP and output

`GET /api/collections/{id}` requires current Heimdall identity and exactly one
`X-Cerberus-Vault-Access` value. The route ID must be a nonzero canonical lowercase
dashed GUID. No query parameter or GET body is accepted, including unknown-length
HTTP/2 and transfer-encoded bodies. Duplicate/malformed/empty access headers,
noncanonical IDs and extra query/body inputs fail with 400 `validation_failed`.
The shared request limit keeps its empty 413 response. Every response is no-store.

Success is 200 `collection_found` with exactly eight data fields:
`collectionId`, `revision`, `serverSequence`, `editedAt`, `envelope`, `profileIds`,
`recordIds`, and `folderIds`. The first five fields use the collection-list
projection: target-matching nonzero ID, positive safe revision and server sequence
not greater than 9007199254740991, UTC nondefault microsecond edit time, and a
strict supported native envelope whose epoch matches the stored collection epoch.

Each relationship array is nonnull, unique within its resource kind, nonzero and
sorted by public GUID. The same GUID may appear in different kinds without
conflating their types. The handler validates the entire store result before
attaching any payload. Key epoch, owner/recipient identity, internal IDs, grant
metadata, wrappers, protection material and usable keys are never returned.

Missing or invalid identity returns 401 `authentication_required`; missing access
returns 401 `vault_access_required`; malformed access or route transport returns
400; current denied access returns 403 `vault_access_denied`; an inactive/missing
actor or absent/hidden target returns nonrevealing 404 `not_found`; required
persistence, native-evidence, ancestry or projection corruption returns 503
`persistence_unavailable`; an unavailable current identity dependency returns 503
`identity_unavailable`. Caller cancellation propagates. Internal dependency
cancellation is a safe 503.

## Current admission and native evidence

The statement captures the actor, active nonterminal account, unrevoked access
session, current positive policy/revocation generation, issue time, database-time
expiry, and selected active owned nonterminal profile. Renewal-enabled sessions
expire exclusively at `statement_timestamp()`; disabled renewal requires null
expiry. A missing/dangling selected profile denies the session and never widens it.

The requested collection is active and nonterminal beneath an active nonterminal
owner. Account-wide access admits an owned collection or a current native read-only
or read/write recipient grant. Selected access additionally requires the exact
current `ProfileCollection` link for that selected profile. Direct profile record
or folder links, collection members, and member visibility never imply permission
to the collection itself. Revoked/terminal/wrong-recipient/invalid-access or
unsafe-revision grants are excluded before inspecting collection ciphertext.

For a foreign admitted target, capture only that target's current collection
envelope/epoch, grant wrapper/revision, owner public ID and protection pins,
recipient Heimdall identity and protection pins. Parse each party's pins once and
bind the grant with `CollectionGrantBinding`. Missing or malformed relevant
evidence fails 503. Owned targets need no grant proof. Unrelated collections,
grants, owners and corrupt native evidence cannot deny this target.

## Visible direct relationships

`profileIds` contains only active nonterminal profiles owned by the actor and
directly linked to the target collection. Account-wide access returns all such
actor profiles; selected access returns only the selected profile. A recipient may
therefore see its own direct profile links on a foreign collection, but never the
owner's or another recipient's profiles.

`recordIds` and `folderIds` contain only direct typed collection members that still
belong to the collection owner and are active/nonterminal with complete active
same-owner ancestry. Explicit record membership is preserved even when the record
also descends from a directly linked folder. The arrays do not expand inherited
descendants, reveal record containers, or fabricate relationships. Hidden,
terminal or incomplete-ancestry members are omitted while the accessible
collection still returns 200, including when every relationship array is empty.

Walk upward only from direct folder members and direct records' containing
folders, carrying visited IDs. A fully active cycle affecting a direct member is
relevant structural corruption and fails 503 after hidden members are filtered.
Unrelated folder trees and cycles remain uninspected. Member ciphertext,
revision, edit time and sequence are not parsed merely to return a public ID;
UC-29 already permits grouping damaged opaque member ciphertext.

Aggregate profile, record and folder links independently to avoid Cartesian
multiplication. The one statement returns at most the requested collection and
only the relevant ancestry/native evidence. No subsequent authority read, lock,
write, decryption, downward traversal or whole-vault scan occurs.

## Flow and evidence

Main flow: owner/native RO/native RW, Master and PerProfile sessions, exact eight
fields, opaque ciphertext, current sorted direct relationships, both typed links
when overlapping, and no mutation.

AF-01: strict canonical route/header/no-query/no-body transport and malformed
visible input return 400 before persistence; corrupt stored target projection is
503 rather than accepted output.

AF-02: missing/foreign/unselected/revoked/trashed/terminal collection returns
nonrevealing 404; selected member-only access cannot expose the collection;
hidden/incomplete direct members are omitted without exposing private structure.

AF-03: missing/current-rejected Heimdall identity, missing access, stale/revoked/
expired/future/wrong-policy/generation access, and dangling/hidden selection return
401/403 without mutation or scope widening.

AF-04: required database/native pins/signature/epoch/wrapper, target ordering,
relevant active ancestry or projection corruption returns 503 without payload;
caller cancellation remains cancellation. Authority, grant, profile and member
changes after the statement preserve one coherent response; the next GET observes
the new current state.

Unit evidence covers context validation, hash/token forwarding, exact projection,
sorted typed arrays, invalid store contracts and safe errors. Real PostgreSQL
evidence covers targeted admission, direct relationship filtering, relevant
ancestry, native binding, current lifecycle changes, snapshot consistency, no
mutation and a bounded `EXPLAIN ANALYZE` against unrelated inventory. HTTP evidence
uses actual UC-29 creation, actual Master/PerProfile access and native sharing
fixtures, strict transport and error mapping. Fresh full Query/Data/WebApi families,
the unfiltered solution, coverage at or above 90% for every production assembly,
OpenAPI/spec/helpers/native/audit checks and exact-head CI remain required.
