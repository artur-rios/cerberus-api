# UC-30 — List collections

Issue #31; base `develop`; implements FR-CL-02 and BR-05/06/28. The user resumed
the previously authorized unattended workflow on 2026-10-11 for this issue only.
That existing authorization covers design, implementation, testing, feature push,
PR creation, normal merge after required CI, issue/project closeout and deletion.
No release, main, tag, admin merge, self-approval or protection bypass is authorized.

## Intended behavior

GET `/api/collections` returns encrypted active collections permitted by the current
human identity and `X-Cerberus-Vault-Access`. Follow the existing folder/record list
patterns: only canonical `pageSize` and opaque `cursor` query keys; no request body,
duplicate headers/queries, unknown filters or selected-profile override. Default
size min(50, MaxPageSize); positive size bounded by MaxPageSize. No-store responses.
The page has exactly `items` and nullable `nextCursor`; each item has exactly
`collectionId`, `revision`, `serverSequence`, `editedAt`, `envelope`. No database IDs,
owner identities, profile/member inventories or foreign protection data are returned.

## Authorization and storage

Capture actor, active nonterminal account, current nonrevoked access session,
policy/generation, statement-time expiry, selected active owned profile, visible
collection inventory and native grant evidence in ONE PostgreSQL statement.
Wide sessions admit active owned collections plus active RO/RW recipient grants;
selected sessions additionally require an exact ProfileCollection link. Direct
profile record/folder links do not admit unrelated collections. Collections and
owners in trash/closure or with typed terminal markers are omitted, as are revoked,
terminal, invalid-access/revision grants. UUID aliases in other terminal kinds do
not hide a collection. No contents or relationships are changed.

For every admitted foreign collection validate its native collection envelope,
key epoch, grant revision, signed recipient envelope and current owner/recipient
protection pins via CollectionGrantBinding. Required malformed/missing evidence
fails closed with503; out-of-selection/hidden evidence cannot poison a page.
Use the snapshot only, without subsequent authority reads or resource locks.
Do not acquire foreign account/profile locks or widen selected access.

Order strictly by safe positive unique serverSequence after permission filtering;
capture visible max sequence as a highwater boundary on the first page, carry it
unchanged in continuation, request pageSize+1 logically for hasMore. Reapply current
visibility each page. This bounds subsequent inserts, not a frozen multi-request
snapshot: edits/revocations can remove/move rows. Continuations use a unique
collection purpose with authenticated encryption, binding actor, verifier, size,
after and boundary. Folder/record cursors cannot be replayed here.

Projection validates nonempty unique public IDs, positive safe revision/sequence,
UTC normalized nondefault microsecond edit time, strict supported opaque envelope
and consistent stored collection key epoch. Handler validates store errors/data,
ordering, item count, fixed boundary and hasMore before disclosing any row.

## Flow mapping

Main: validate transport/context -> one authorized snapshot -> native evidence and
safe opaque projection -> bounded encrypted continuation ->200.
AF-01: malformed visible query/access/cursor ->400 validation_failed; oversized
transport remains413 under existing middleware. No decryption or mutation.
AF-02: inaccessible collections omitted; unknown/inactive acting account404
not_found; empty valid scope200 empty items.
AF-03: missing/invalid/current-rejected identity401; missing access401;
expired/revoked/stale-policy/foreign-profile vault scope403 vault_access_denied.
AF-04: database/timeout/current identity dependency or required visible native
evidence/projection corruption503 persistence_unavailable/dependency_unavailable.
Caller cancellation propagates; internal dependency cancellation becomes503.

## Verification

Use Given/When/Then family UnitFact/UnitTheory and FunctionalFact/FunctionalTheory.
Observe failing behavior tests before production implementation. Unit coverage:
validation, cursor tampering/binding/cross-purpose, safe projection/store contracts.
Real PostgreSQL: wide/selected owned and native RO/RW sharing, omissions before
pagination, revocations between pages, pins/signature/epoch corruption only inside
scope, typed terminal aliases, safe ordering, no mutations and cancellation/failure.
HTTP: actually create collections through POST, strict GET shape and transport,
fresh identity failures, native grants and selected profiles, no-store and403/503.
All four AFs need evidence. Run full unfiltered .NET suite, Unit and Functional,
fresh coverage >=90% production lines (all six assemblies), OpenAPI/spec/Python
helpers, native corpus/suites and dependency audits. Required branch-policy/test/
docker CI must pass on the exact delivered head before normal merge.
Independent security/client qualification remains deferred for develop only;
shared OpenAPI requiredness corrections and release obligations remain tracked.
