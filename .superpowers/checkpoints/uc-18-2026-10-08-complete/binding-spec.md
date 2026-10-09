# UC18 Get Record Design

Purpose: fulfill issue19/FR-RC-03 for an owner or collection recipient retrieving a
native encrypted record by public ID, with only the relationships visible through
current access. Existing batch authorization covers automated design/plan gates,
implementation and verified develop integration; independent security and actual
client approvals remain deferred for development only and required before release.

This is architectural work: a new read contract crosses Data, Query and HTTP, and
relationship projection depends on current selected/recipient permissions. Execution
starts only after UC17's verified normal merge and complete integration DoD in a new
isolated feature/uc-18-get-record worktree from fresh develop. Preserve primary user
files. No extra dependency, migration, write endpoint or plaintext is needed.

Sources: issue19 main/allfourAF; SystemRequirements3.3 FRRC03, common4.2/4.5/4.6/4.8/4.9,
authorization7; UseCaseSpecification UC18; BR08/09 encrypted client requirements and
BR28 recipient isolation; current workflow/testing/tech/operations. Carry UC17 native
binding, complete active ancestry, hidden-before-corruption and one-snapshot rules.

## Approaches

1. Filter full record listing for the requested ID. Reject: unnecessary permitted
inventory traversal/paging, no visible relationship snapshot, and unrelated corrupt
inventory could block a targeted get.
2. Fetch target then query authority and each relationship. Reject: mixed snapshots,
N+1 and revocation races, or locking foreign owners for a read.
3. One parameterized targeted SQL snapshot, then strict native projection. Prefer:
lookup at most one potentially owned/eligible-shared target before full upward
ancestry; derive exact current permission and visible references in that statement.
This retains single-read consistency without listing the installation or a vault.

## Public contract

GET /api/records/{id}, canonical lowercase nonzero D UUID. Exactly one canonical
X-Cerberus-Vault-Access and current Heimdall identity. No query parameter, GET body or
transfer encoding. No client owner/profile override. All responses no-store. Common
oversize guard can return empty413 before operation-specific body400.

Success200 record_found, exactly eight data fields: recordId/revision/serverSequence/
editedAt/envelope/profileIds/folderId/collectionIds. envelope is native encrypted
content, with no plaintext name/template/field information. profileIds/collectionIds
are non-null distinct nonzero public GUID arrays, ordered deterministically; folderId
is a nullable visible public GUID. No owner/internal IDs, grant blobs, pins, handles,
counts, passwords or owner organization. Existing strict RecordProjection validates
the five content fields: safe positive revision/sequence, UTC >=10ticks aligned to
microseconds and strict native envelope JSON/format/epoch/canonical binary fields.

## Authority and targeted ancestry

Revalidate actor account active/nonterminal, hashed current session, positive policy/
generation matching account, issued<=statement_timestamp, exclusive expiry or explicit
renewal-disabled/null expiry, and current selected owned active/nonterminal profile.
Never convert a dangling selectedProfileId to null/account-wide. 404 missing/closing/
terminal actor;403 invalid current access;401 missing identity/handle;400 syntax/input;
404 absent/hidden target;503 dependencies or relevant corrupt native evidence/content.

Lookup target by its public ID with at most one potential record candidate. Owner
account must be active/nonterminal. Admit ownership or a current eligible collection
owner as potential routes; selected exact direct/folder/collection permission may
require that one target's upward path. Full same-owner ancestry uses visited IDs to
terminate cycles. Exact visibility: account-wide owned, selected direct ProfileRecord,
selected ProfileFolder root in ancestry, or CollectionRecord/CollectionFolder ancestry
under an eligible owned/shared collection, with ProfileCollection link when selected.
Eligible foreign grants require current recipient, active RO/RW, safe positive
revision, nonterminal grant and active/nonterminal collection/owner. Overlaps dedup.

Hidden target/ancestor omits the record404 even for direct links, before any relevant
cycle or native inspection. A fully active cycle yields503 only if the target has a
current permission route; out-of-selection/foreign/missing/revoked content remains404
and cannot become a corruption oracle. Do not inspect another record's metadata or
ordering: targeted get validates its one sequence; list/sync enforce inventory order.

For visible foreign content, snapshot all contributing current grants, collection
native envelopes/epochs, owner public ID/author pins and recipient identity/key pins
in the SAME SQL. Reuse CollectionGrantBinding.TryReadPins/IsBound; invalid relevant
signature/binding/revision/epoch/material fails whole503, never partial data. Snapshot
owner and recipient protection rows once for this single-owner target and validate
pins once per request, then validate each grant. No later DB queries or foreign locks.
Fixture/native admission is not independent client decryptability qualification.

## Visible relationships

profileIds: only active/nonterminal actor-owned DIRECT ProfileRecord links to target;
account-wide may see all those actor profiles; selected sees only that selection.
Foreign target always has [] here, never another owner's profile IDs. Dynamic folder
or collection visibility does not invent a direct profile link.

folderId: actual parent only if that folder is itself currently visible. Account-wide
owner sees its active parent. Selected owner needs its ProfileFolder root/descendant
route or an eligible linked collection's CollectionFolder root/descendant route.
Foreign recipients need a contributing CollectionFolder route covering the parent.
A direct record link/grant alone does not disclose an otherwise hidden parent; emit
null while returning the visible record. Complete active ancestry remains mandatory.

collectionIds: only currently eligible collections actually including target directly
or through an active folder ancestor, restricted to selected ProfileCollection links
when selected. Foreign IDs require the same validated native contributing grants.
Owned metadata references do not require decrypting/validating unrelated collection
ciphertext. Same public GUID across kinds remains allowed; dedup only within each
kind. No unrelated owner graph can leave Data.

## Internal interfaces and failure behavior

Domain/Records/RecordReadContracts.cs:
RecordReadRequest(Guid Actor,string AccessVerifier,Guid RecordId);
RecordReadDetails(RecordListRow Record,Guid[] ProfileIds,Guid? FolderId,Guid[] CollectionIds);
IRecordReadStore.ReadAsync(request,ct) -> VaultResult<RecordReadDetails>.
Data/Records/RecordReadStore.cs: one SqlQuery snapshot with typed internal JSON/hex
transport if useful, parameterized actor/verifier/target and enum/safe bounds. Current
authority, one candidate/ancestry/scope, visible references and native evidence share
one statement. No writes, locks, post-page filtering, further DB reads or new schema.
Query/Records/GetRecordQuery/GetRecordHandler/RecordDetailsOutput/RecordReadMessages:
reuse RecordProjection; validate target equality, arrays/nullable parent, null result,
error-with-data and allowlist not_found/vault_access_denied/persistence_unavailable.
Any broken store contract or corrupt visible payload503 with no data; cancellation
propagates. Controller/Startup reuse QueryMediator and explicit scoped handler/store DI.

## Verification and delivery

Tests first at each layer. Data: owned/shared RO/RW, selected modes, direct/descendant,
exact visible references, lifecycle/session/native corruption, hidden/oracle/cycle,
no mutation, one captured SQL with post-statement grant/link changes, pre-execution
expiry, provider/caller failure and analyzed plan with unrelated/deep inventory.
Query: strict context/target/envelope/metadata/publicshape/referenceIDs/storecontracts/
errors/cancellation. HTTP: actual UC16 creations/UC15 native Master+PerProfile handles,
actual native recipient grants, canonical ID/header/no query/body, all states/errors,
no graph leaks/no-store/no mutation/corruption/outages and relationship changes.
Full Data/Query/Web named suites and final unfiltered six-family >=90line/no fail/skip;
report branches. Native/audits/lockedrestore/OpenAPI/helpers/spec/diff. One ordinary
fresh review/one blocking TDD correction pass; all declined scopes reasons/costs and
minors recorded. Exact-head branch-policy/test/dockerSUCCESS+MERGEABLE+CLEAN and fresh
unchanged testedbase before normal matchhead merge; issue/project/9DoD/remote branch/
develop tree/primarySHA/archive. Then UC19. No release gate weakening or extra approval.
