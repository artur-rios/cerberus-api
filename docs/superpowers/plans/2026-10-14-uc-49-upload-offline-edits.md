# UC49 Upload offline edits implementation plan

> Execute inline with test-driven development and one fresh independent whole-change reviewer.

**Goal:** Apply bounded encrypted offline edits with current native permissions, timestamp/sequence ordering and durable operation idempotency, returning non-content per-item outcomes.

**Specifications:** UC49 main flow and AF01–08; FR-SY-11, FR-SY-12, FR-SY-13, FR-SY-14; BR18, BR26, BR28; existing authorization matrix and native resource contracts.

**Architecture:** A strict polymorphic encrypted edit batch dispatches to existing native create/update stores inside an externally owned transaction. A savepoint-aware mutation transaction helper preserves each native store's rollback behavior while atomically committing accepted resource writes with metadata-only SyncOperation records. Trusted internal latest-edit mode compares normalized timestamps after current native permission/resource locks, preserving strict expected revisions for online calls. Native server sequences provide equal-time ordering.

## Design rulings

- Issue50, feature/uc-49-upload-offline-edits from clean verified developfa5bf326fd0a4506432c059a2eaac00b16833424. Unattended authorization persists for verification, PR/develop merge, closure and branch cleanup. UC43 remains blocked on Heimdall178. Development only; genuine release/client review remains gated.
- POST /api/sync/edits requires current human identity and current account-wide or selected-profile vault authority under the established native rules. Scope/ownership comes from persisted authority, never a client owner foreign key.
- Strict bounded edits array, unique nonempty stable operation IDs, supported discriminator, canonical GUIDs, UTC times and native envelope/creation structure. Support encrypted create/update for profiles, records, folders and collections. Native creation owns resources in the actor's account; foreign recipient writes remain limited to existing granted records/folders. Parent/member/sharing/deletion/key-policy operations remain their separately authorized APIs.
- New resources use client-generated public identifiers and existing native create payloads/atomic associations. Updates carry target ID, envelope and edited time, with no public expected-revision override. Trusted offline mode inside the native update stores ignores the online revision precondition only after locking/revalidating the target and current permission. Online behavior stays strict.
- Normalize UTC timestamps to PostgreSQL microseconds. An older structurally/native-valid update is superseded, returning only visible winning revision/sequence/time. Newer updates apply. Equal timestamps apply with a newly persisted sequence strictly above the prior sequence, giving a deterministic accepted order. Distinct operations tie in serialized server order; exact replay never allocates another write/sequence. Client time is an ordering input, not proof of real chronology.
- SyncOperation unique(account, operation ID) persists scope, canonical request fingerprint, typed target/action and minimal applied/superseded/denied/permanently-deleted outcome metadata. No ciphertext, wrapper, access token or secret is retained. Bind replay to the same scope and exact request; changed reuse conflicts and rolls back the batch. Metadata remains for active-account idempotency and cascades on account erasure; SyncRetention does not silently expire operation IDs.
- Serialize each batch's actor/account ledger and current scope. Lock all captured owners' collections in canonical public-ID order before any child target lock, preserving standalone online recipient collection-before-target order. Existing native stores retain their authority, grant/protection/ancestry and nonce/epoch checks, current locks and generated-sequence postconditions. Externally owned nested calls use savepoints; a denied/failing native call rolls back its partial writes while the batch can record a safe per-item denial. Dependency/semantic validation/conflicting operation reuse/caller cancellation rolls back all batch writes and operation records.
- Recheck current authority after waits and before final commit. Cached operation results are reauthorized against current target visibility before returning winning metadata; lost access returns denied without replaying content or rewriting history. Permanent typed identifiers are never resurrected. Distinct permanent-deleted outcomes require previously disclosed/owned-operation proof; unknown hidden targets return generic denied and no winning metadata.
- Native creation identifier conflicts never overwrite another resource; disclose current winning metadata only if currently visible. Per-item denied/permanently-deleted contain only submitted operation/typed target identifiers and status. Successful endpoint responses contain no encrypted payloads.
- AF01 strict400/native validation rollback; AF02 actor absent/inactive nonrevealing404 and hidden item denied; AF03 identity401/current vault denial403; AF04 conflicting idempotency409 or specified item outcome; AF05 dependency503/cancellation rollback; AF06 older superseded; AF07 documented literal timestamp policy; AF08 current lost-write-access denied.

## Task1: Contracts, ordering and operation schema

- [x] Write failing Domain/Command tests for eight strict edit variants, native creation validation, bounds/duplicate IDs, UTC normalization, timestamp comparisons, current context, per-item output integrity and safe errors.
- [x] Define polymorphic request, minimal outcomes and upload store/handler contracts; keep resource kinds/IDs/operation IDs bound and reject unsafe counters or outcome data on denied items.
- [x] Add metadata-only SyncOperation with account cascade, unique stable ID, scoped hash and checked result shape; generate reversible migration and verify rollback/reapply/no unrelated drift.

## Task2: Reuse native mutations atomically

- [x] Write real PostgreSQL failing regressions for outer transaction success/rollback/savepoint denial, timestamp older/newer/equal, stable replay and changed operation reuse.
- [x] Introduce savepoint-aware native mutation transaction helper and shared external transaction factory. Adopt only in the eight existing create/update stores; retain ordinary standalone transaction semantics.
- [x] Add trusted latest-edit mode to four native update requests/stores after current permission/locks/native validation, compare normalized timestamp and use the locked current revision; preserve online expected-revision paths.
- [x] Implement batch scope/operation binding, dispatch, safe denial/known typed terminal detection, durable minimal outcomes and current replay checks. Recheck authority at final statement time and roll back dependency/cancellation/conflict faults.

## Task3: Transport, concurrency and documentation

- [x] Observe absent POST, then wire strict JSON bounded body/header/query behavior, Command handler/DI, canonical identifiers and no-store response/errors.
- [x] Cover all eight variants with valid native fixtures, owner/selected/read-only/read-write scopes, missing/trashed/terminal IDs, crypto epochs/nonces, clock extremes, replay after authority changes, same-ID and equal-time concurrent writers, sequence regression/exhaustion, natural expiry/lock waits, faults and clean retries.
- [x] Document exact input/result/ordering/idempotency/privacy/retention rules and update OpenAPI, foundation/operations/changelog. Validate real HTTP JSON including nullable/typed contracts.
- [x] Run locked restore/audits/zero-warning build, full unfiltered Release with fresh >=90% line coverage, helper/spec/native corpus/server leases/OpenAPI/Docker and fresh whole-change review. Resolve Critical/Important findings before delivery.

## Task4: Authorized delivery

- [ ] Update README issue50/M05 counts; commit/push/open own PR targeting develop with Closes50. Require exact-head branch-policy/test/docker SUCCESS, then normal merge, issue/projectDone, reviewed/merged tree proof, owned evidence archive and clean synchronized develop before M06.
