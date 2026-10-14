# UC48 Download synchronization changes implementation plan

> Execute inline with test-driven development and one fresh independent whole-change reviewer.

**Goal:** Return bounded current permitted profile changes, removals and minimal permanent-deletion tombstones through durable opaque cursors.

**Architecture:** A profile-scoped synchronization stream persists only checkpoint metadata, known disclosed identifiers/fingerprints and monotonic SyncChange descriptors. Materialize net changes against its acknowledged inventory; fetch payloads from current native-validated visibility on every page. Never persist or replay old encrypted payloads. Reuse existing profile/record/folder/collection stores within one shared database transaction protected by canonical account/session/profile and complete owner-collection locks. Keep the existing native permission and grant/protection validation rules.

**Spec:** UC48 all flows; FR-SY-08, FR-SY-09, FR-SY-10; System Requirements sync cursor/SyncChange data model; configured SyncRetention; native permission/provisioning and no-cache contracts.

## Design rulings and constraints

- Issue49, branch feature/uc-48-download-synchronization-changes from clean develop4447804. User's explicit unattended batch and next-milestone authorization persist; UC43 remains blocked on Heimdall178. PR develop, no release authorization.
- GET /api/sync/changes accepts optional opaque cursor and bounded pageSize. Require current human identity and a current selected-profile vault handle; the existing UC15 profile context supplies the profile scope. Account-wide handles cannot synchronize a wider inventory. This follows the use case's selected access precondition and avoids changing established list-store authorization.
- Cursor binds actor/profile/stream/window/after/boundary/page size, with AES-GCM and a distinct HMAC-derived purpose from the existing durable fingerprint key. It does not bind an expiring online handle; fresh valid unlock of the same profile can continue. Tampered, wrong-scope, stale-window and expired/missing retained checkpoints return explicit resynchronization-required/clear-scope with no old payload.
- Add SyncStream, SyncInventory and SyncChange tables. Stream is account/profile scoped with fixed configured retention expiry; minimal metadata cascades on stream/account/profile deletion. SyncChange uses the existing global monotonic sequence and typed identifiers/event type. Inventory contains only identifiers/revision metadata and fingerprints of previously disclosed encrypted/native data. No vault content, wrapper or secret is stored in synchronization history.
- First snapshot clears previous local scope before applying its pages. Compare current visible inventory against acknowledged data to create upserts/removals; classify terminal deletions only by joining typed identifiers already disclosed in this stream. Never enumerate unrelated terminal rows. Emit all removals before new data. Grant/key provisioning metadata must remain native-bound and current.
- A window has a stable event boundary; every page rechecks current visibility and uses current payloads. Membership/permission changes during unfinished pagination require clear-scope resynchronization before further disclosure. Within a current window, cursor replay revalidates access and preserves monotonic progress; a superseded completed window is explicitly stale. No audit-history or point-in-time historical payload claim.
- Discover all associated foreign owner accounts, lock the complete set in public-ID order, then current session/profile and stream checkpoint. Recheck after waits. Shared recipient payload writers lock only their editing account; additionally lock all collections of every captured owner in global public-ID order FOR NO KEY UPDATE OF c. This covers alternate collection routes and directly selected owned resources without upgrading root account SHARE locks. Reuse native list/read stores using an external-connection/transaction factory; finalize with fresh current session/permission checks before any data leaves the transaction.
- Retention expiry is enforced at read time; expired stream metadata is deleted when encountered. UC53 must include scheduled cleanup of abandoned expired streams. Account/profile erasure cascades eliminate their private sync state.
- Document client removal/clear/expiry/reconnection obligations without claiming external platform certification. Full unfiltered fresh Release >=90% line coverage, native corpus/server leases/OpenAPI/Docker, independent review and exact-head CI gate delivery.

### Task1: Typed synchronization contract, opaque cursor and persistence schema

**Files:** Domain/Sync stream/inventory/change and contracts; Query/Sync cursor and handlers/output; AppDbContext; migration/model snapshot; corresponding Domain/Query/Data tests.

- [x] Write failing tests for strict query/current scope, opaque actor/profile/window binding, safe counters, tampering/expiry/resynchronization and bounded ordered output.
- [x] Define typed current resource/grant payload and minimal descriptor/page/continuation results; explicitly validate native envelope/wrapper metadata before public output.
- [x] Add scoped sync entities, unique inventory/event indexes, monotonic event source, cascades and retention metadata through a reversible migration. Verify migration roundtrip and no unrelated schema drift.
- [x] Implement domain-separated AES-GCM cursor and strict query handler; invalid retained continuation produces explicit clear/resynchronize result rather than old content.

### Task2: Durable current-authority download pipeline

**Files:** Data/Sync current inventory reader and transactional download store; SyncController, Startup; real PostgreSQL and HTTP tests.

- [x] Observe absent GET route; write initial/empty/multipage/delta/current profile/shared grant/removal/terminal privacy/retention/renewed-session regressions.
- [x] Resolve selected profile and complete foreign-account set, canonical locks and fresh metadata rechecks. Use a shared transaction factory for existing native profile/resource visibility stores; validate current data and include only current recipient provisioning.
- [x] Materialize minimal monotonic net-change events against known inventory; page removals before upserts, update acknowledged metadata without exposing hidden targets. Revalidate current permissions on replay and enforce clear-scope when unfinished window visibility changes.
- [x] Wire strict GET/no-body/query/header behavior and current authority/dependency errors; propagate cancellation and roll back storage faults without partially advancing checkpoints.

### Task3: Races, documentation and full verification

**Files:** concurrency/HTTP/contract tests; sync API/security/operations docs, OpenAPI, foundation status and changelog.

- [x] Cover policy/generation/session/profile/grant and ancestry changes after waits, expiry during work, no stale revoked payload, same-cursor concurrency/replay, typed terminal identity collisions, storage faults and malformed checkpoint metadata.
- [x] Document selected-profile scope, exact cursor/result/error/clear semantics, current encrypted payloads, ordering/boundary behavior, retention/privacy and client application rules. Regenerate OpenAPI.
- [x] Run locked restore/audit/zero-warning build, full unfiltered Release fresh coverage, helper/spec/native corpus/server lease compatibility/OpenAPI and Docker. Resolve all Critical/Important independent review findings.

### Task4: Authorized delivery

**Files:** README and delivery ledger.

- [ ] Update issue49 backlog/counts; commit/push/open PR, require exact-head branch-policy/test/docker CI and merge normally; close/projectDone, verify reviewed merge tree and clean synchronized develop before UC49.
