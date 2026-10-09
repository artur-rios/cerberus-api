# UC-24 — List folders

## Intent and traceability

Implement issue25/UC24 and FR-FD-02: owners and collection recipients obtain only currently permitted active encrypted folders and pagination metadata. Preserve BR07's acyclic owned hierarchy, BR22's hidden deleted ancestry, BR28's included-content sharing boundary, FR-PR-10 selected scope, FR-CL-08 member descendants, NFR01/03/04/05/08/09 and the current authorization matrix. No content decryption or mutation occurs.

The authorized all-open-issues development batch replaces repeat design/plan and delivery approvals with self-checks, real tests, one fresh ordinary review and exact-head CI. Independent formal security/protocol and external-client qualifications remain development-only deferred; main/tag/release gates remain mandatory.

## Approach and boundaries

Use a dedicated Domain list contract, Query handler/projection/cursor and one SQL-snapshot Data store, then expose GET on the existing FoldersController. This follows the UC17 record-list convention while applying folder-specific admission and complete ancestry. A multi-read implementation could combine stale scope with new content; an inventory-wide traversal could inspect unrelated corruption. Admit permitted candidates first, then inspect their complete structural ancestry inside the same statement.

No migration, new dependency, change to cryptographic algorithms or new client application. Folder create/get/update/trash/move/restore/purge and durable synchronization are separate use cases. Account/record/profile/collection ciphertext, keys, sessions, relationships and revision metadata remain unchanged by reads.

## HTTP and output contract

GET `/api/folders` accepts only optional single exact-case `pageSize` and `cursor` query parameters and the single `X-Cerberus-Vault-Access` header. Page size is canonical unsigned positive decimal, at most configured MaxPageSize; default min(50, MaxPageSize). Reject unknown/duplicate/case-changed parameters, whitespace/sign/leading-zero/noninteger/overflow page sizes, malformed/empty cursors, duplicate/malformed headers and any GET body including unknown-length HTTP2/chunked bodies. Shared bounded-body middleware retains empty413 for oversized bodies. Current Heimdall identity supplies the actor; no caller owner/profile or plaintext search selector.

200 `folders_found` returns DataOutput containing exactly `items` and nullable `nextCursor`. Each item contains exactly `folderId`, `revision`, `serverSequence`, `editedAt`, `envelope`. GUID nonzero, revision/sequence positive <=9007199254740991, UTC edit time >=10ticks and exact microsecond precision, strict native supported encrypted envelope. No internal/owner/profile/collection/grant IDs, key wrappers or parent references. Parent and other visible relationships belong to UC25's detailed lookup. Validate the complete returned page before exposing any item.

No-store applies to empty, success and failures. Visible malformed input400/validation_failed; failed identity401/authentication_required; missing handle401/vault_access_required; current invalid session403/vault_access_denied; missing/inactive/typed-terminal account404/not_found; required persisted/native corruption or dependency503/persistence_unavailable. Hidden list targets are omitted. Safe error allowlist never exposes dependency details. Caller cancellation propagates.

## Current permission snapshot and ancestry

Resolve active actor account and current session at database statement_timestamp. Session must be unrevoked, positive current policy/generation, issued no later than now, exclusive expiry when renewal enabled or null expiry when disabled; selected profile must remain owned, active and nonterminal. Do not reinterpret a missing/trashed/terminal/dangling selection as account-wide.

Account-wide scope includes all active owned folders plus folders exposed through current recipient collections. Selected scope includes owned direct ProfileFolder members and descendants, and current selected ProfileCollection member folders and descendants (owned collection or native foreign RO/RW grant). A ProfileRecord or CollectionRecord link does not expose its parent folder, ancestors or siblings. Collection access requires active owning account/collection, active valid known-access recipient grant for foreign content, positive safe grant revision and typed terminal exclusions. Selected foreign collections require a current profile association.

Admit own-wide candidates or direct/member reachable folders before upward traversal. Reachability terminates overlapping/cyclic paths with recursive UNION; descendants must share the member's owning account and be active/nonterminal. Walk each admitted active candidate itself and its full same-owner parent path. A hidden ancestor or incomplete ancestry omits the candidate before native/corruption inspection; a fully active scoped cycle fails503 without hanging. Unselected/ungranted cycles remain omitted rather than an availability oracle. Member leaves expose only themselves/descendants, never otherwise unshared ancestors or siblings.

Scope/link/grant/lifecycle, complete ancestry, ordering, high-water boundary, page content and native evidence are captured by one SQL statement. No locks or writes. A revocation after the statement cannot mix new authority with old content; the next call observes the revocation. Validate every contributing foreign grant of every visible candidate before returning a page, including overlapping/nonpage contributions. Native signature, collection identity/epoch/envelope, grant identity/revision/recipient and owner/recipient protection pins must all bind. Pins stay internal and are parsed once per distinct account in this snapshot; no signature validation is skipped.

## Pagination and corruption

Use global increasing serverSequence keyset, initial boundary max visible folder sequence (0 when empty), ordered ascending, continuation `after < sequence <= boundary`, LIMIT size and OFFSET size existence for hasMore without size+1 overflow. Diagnose nonpositive/unsafe/duplicate sequences across the complete visible set before keyset filtering. Validate revision/time/envelope of page rows in Query; no hidden-row content parsing.

Dedicated authenticated encrypted cursor purpose `cerberus-folder-list-cursor-v1` derives its key from the existing dedicated RegistrationFingerprintKey. Cursor binds actor, hashed access handle, size, after and boundary; canonical base64url length<=2048, strict tuple/JSON. Reject profile/record cursors, key/actor/handle/size substitutions, tampering, unknown/duplicate/case-changed/numeric-string JSON, invalid integers and after>=boundary. Existing issued handles identify immutable scope; every continuation independently rechecks current session/selection/policy/grants. Permission removal may shrink/empty a page; later sequence>boundary is excluded. Key rotation invalidates with400 and callers restart. This is current-permission keyset listing, not an immutable content snapshot or durable sync.

## Flow verification

Main: owned and actual native foreign RO/RW member roots/descendants, selected modes Master/PerProfile, overlapping links, exact opaque output, no mutations and stable initial boundary.
AF01: strict query/header/body, cursor substitution/strict tuple/native page metadata ->400 before store; current identity checked through real host.
AF02: unrelated owners, unselected profiles/collections, record-only routes, trashed/typed-terminal owner/collection/grant/folder/ancestor omitted before pagination and native corruption; missing account404.
AF03: current identity/provider errors, missing/invalid/revoked/expired/future/stale-policy/stale-generation session and hidden/dangling selection ->401/403 without mutation, even empty results.
AF04: table/network/internal timeout, visible corrupt ordering/ancestry/envelope/time, every relevant native binding/pin failure ->503 with no partial payload; caller cancellation unchanged. Real snapshot revocation and statement-time expiry cases.

EXPLAIN ANALYZE cases with 100 unrelated deep folders must admit at most one selected/permitted leaf and traverse only its actual ancestry. Record-only grants/links never widen folder scope. Cross-kind terminal IDs preserve folders unless kind=folder/account/necessary authority kind. Shared OpenAPI requiredness/nullability and empty413 metadata remain recorded release Minors rather than false runtime claims. Production workload certification, client key provisioning/decryptability and later writer/lock/lifecycle/sync qualifications remain their explicit gates.
