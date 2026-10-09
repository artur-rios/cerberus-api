# UC-25 Get folder

## Intent and traceability

Implement issue26/UC25 and FR-FD-03: an owner or native collection recipient retrieves one currently accessible encrypted folder by public identifier, with only visible organization references. Preserve BR07 acyclic same-account nesting, BR22 hidden deleted ancestry, BR28 included-content isolation, FR-PR-10 selected scope, FR-CL-08 member descendants, the authorization matrix and existing opaque-content rules.

The authorized all-open-issues development batch replaces repeat approval gates with written design/plan self-checks, TDD, fresh full verification, one ordinary whole-branch review and exact-head CI. Independent formal security/protocol and external-client qualification remain deferred only for develop; release/main/tag gates remain mandatory.

## Approach

Follow existing GetRecord contracts/projection/one-statement snapshot with dedicated folder types and folder-specific permission/parent logic. Reuse unchanged FolderListRow/FolderProjection and native CollectionGrantBinding. A multi-read implementation can mix current permissions with stale references; traversing all inventory can inspect unrelated corruption. First admit at most the public-ID target, then walk its complete same-owner self/upward ancestry in one statement. No migrations, new packages, algorithms, locks, mutations or plaintext parsing.

## HTTP and output

GET `/api/folders/{id}` requires current Heimdall identity and one canonical X-Cerberus-Vault-Access header. The path ID must be nonzero canonical lowercase dashed GUID. No query parameter or GET body, including unknown-length HTTP2 and chunked transfer. Duplicate/malformed/empty headers, noncanonical IDs and extra query/body inputs fail400 validation_failed. Shared limit middleware retains actual empty413. No owner/profile override.

200 folder_found returns exactly eight data fields: folderId/revision/serverSequence/editedAt/envelope/profileIds/parentFolderId/collectionIds. The five content fields match FolderListItem, with nonzero target-matching GUID, positive safe integers <=9007199254740991, UTC >=10ticks aligned to microseconds and strict supported native envelope. ProfileIds/CollectionIds are nonnull unique nonzero sorted public GUID arrays; ParentFolderId is null or a nonzero public GUID different from FolderId. Validate the entire result before attaching a payload. Cross-kind identical GUIDs remain valid; a profile/collection ID may equal a folder ID without conflating types.

This mirrors the existing eight-field record-detail contract. Direct child/record inventories remain their permitted list/detail endpoints rather than adding unbounded child payloads or a new navigation protocol. No private profiles, unrelated collections, hidden parent/ancestor/sibling IDs, internal IDs, owner IDs, grant envelopes or usable keys. The returned parent is immediate only, never a skipped ancestor. No-store applies to success and failure.

Missing/invalid identity401 authentication_required; missing access401 vault_access_required; current denied access403 vault_access_denied; missing/inactive/typed-terminal actor or absent/hidden target404 not_found; necessary persistence/native corruption or dependency503 persistence_unavailable, safe allowlist only. Caller cancellation propagates. Unsupported stored content is persistence corruption503; callers cannot submit envelopes on this GET.

## Permission snapshot and complete ancestry

One SQL statement at statement_timestamp captures active actor/session, selected profile, eligible collections/grants, target, ancestry, visible references and every contributing foreign native grant. Current session must be unrevoked, positive current policy/generation, issued no later than now, exclusive expiry if renewal enabled or null expiry if disabled. Selection remains owned/active/nonterminal, never null or account-wide on disappearance.

Candidate is only the requested public GUID, active/nonterminal under an active owner, admitted for own account or an eligible current foreign collection owner. Walk candidate itself then same-owner parents, detecting cycles with a finite path. Complete active ancestry must reach a null parent. Hidden or incomplete ancestry returns404 before content/native inspection; a fully active actually scoped cycle503. Unselected/ungranted cycles stay404 and unrelated deep/corrupt inventories remain uninspected.

Account-wide includes all active owned folders plus current foreign collection folder members/descendants. Selected scope includes direct own ProfileFolder links and descendants and current selected ProfileCollection folder members/descendants. ProfileRecord/CollectionRecord must never expose containing folders. Included collections have actual CollectionFolder membership at target or an ancestor. Member leaves reveal descendants, never otherwise unshared parents/siblings. Foreign grants require current active RO/RW, positive safe revision, no typed terminal marker, active owning account and collection, current selection association if selected.

All eligible foreign collections contributing to the target are native-verified, including overlapping routes even if one valid route already grants access. Parse one owner's and one recipient's current pins once, then verify every signature, owner/collection/grant/recipient binding, current epoch/revision and strict collection envelope. Corrupt unrelated or unselected native evidence cannot deny this target. Do not parse unused owned collection/profile/ancestor content. No native evidence is returned.

## Visible organization

ProfileIds includes current active direct ProfileFolder links belonging to the actor's account: all own direct links account-wide, only the selected profile when selected. Foreign content never reveals its owner's profiles. Inherited visibility does not fabricate a direct link.

CollectionIds includes current eligible collections whose folder membership at target or an ancestor actually exposes target; selected scope includes only collections selected by that profile. Unrelated/trashed/terminal collections and private ungranted owner collections stay absent. Every foreign included reference is covered by native verification.

ParentFolderId appears only if the immediate parent independently has a folder authority route in this same snapshot: account-wide own access; selected direct ProfileFolder at parent or higher ancestor; or eligible CollectionFolder at parent or higher ancestor. A direct target link or target member grant alone must not reveal parent. Since target ancestry is complete and active, no separate ancestry query is needed for parent. Parent permission can come from a different included ancestor route; all contributing native evidence remains validated.

## Flow and evidence

Main: exact eight fields and ciphertext, owned/selected Master and PerProfile, direct versus inherited refs, actual native RO/RW memberships at root/leaf, overlapping contributing routes, current references and no mutation.
AF01: canonical target/header/no query/no body strictness400 before persistence, native stored envelope/metadata503 rather than accepting corrupted output.
AF02: missing/foreign/unselected/direct-record-only/shared-record-only target404; hidden/terminal target/ancestors/owner/collection/grant omitted before content/native inspection; typed cross-kind identities preserved.
AF03: current Heimdall identity and upstream errors, missing access401, stale/revoked/expired/future/wrong-policy/generation and dangling/hidden selection403, no scope widening or mutation.
AF04: required dependency/native binding/pins/ordering/ancestry/content/time corruption503 without payload, caller cancellation unchanged. Expiry before SQL uses database time; authority/link changes after statement preserve one snapshot and next call sees removal.

Actual EXPLAIN ANALYZE must show candidates=1 and ancestry only the target plus its actual parents against at least100 unrelated deep folders. Fresh whole Query/Data families, actual native HTTP and final unfiltered six-family coverage >=90% production line, branch reported; locked restore/NuGet/native audit/corpus/helpers/spec/OpenAPI/range-whitespace and exact-head CI remain. Shared OpenAPI requiredness/nullability/empty413 and other recorded inherited Minors remain release follow-ups. Production load/client provisioning/decryptability/durable sync/future writers and erasure paths need their own gates.
