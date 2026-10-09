# UC-26 Update folder design

## Intent and authorization

Implement issue27/UC-26/FR-FD-04: an owner or current read/write collection recipient replaces one permitted folder's encrypted envelope using an expected revision. Preserve BR07 same-owner acyclic hierarchy, BR22 hidden deleted ancestry, BR27 read/write sharing, selected-profile isolation and opaque ciphertext. Content replacement never changes parent, associations, ownership, descendants or sharing administration.

This is architectural work across a new Domain/Command/store/HTTP contract. The authorized all-open-issues batch replaces repeated design/plan approvals with explicit self-checks, TDD, fresh full verification, one ordinary whole-branch reviewer and exact-head CI. Independent formal security/protocol/client approval remains development-only deferred; release gates remain intact.

## Approach and alternatives

Use dedicated folder update contracts, following RecordUpdateStore transaction/optimistic guards and UC25 target+self/upward folder authority. This preserves established response and conflict semantics while keeping record-only scope out of folder navigation. A multi-read unguarded UPDATE risks applying an edit after permission changes; locking entire foreign ancestor/account chains risks reciprocal and existing leaf-to-root writer cycles. Lock only own account/session/selected profile, current eligible collections/grants in public-ID order and target folder; recompute complete ancestry and authority inside the guarded UPDATE. No schema/dependency/algorithm changes or plaintext parsing.

## Transport and contract

PUT `/api/folders/{id}` requires current Heimdall identity, one canonical X-Cerberus-Vault-Access header, canonical lowercase dashed nonzero GUID path, no query parameters and strict bounded UTF8 JSON. Existing VaultProofBody middleware validates the body transport; no new signed challenge is needed for content replacement, matching record update. Body contains exactly required expectedRevision/envelope/editedAt; unknown/duplicate/case-forged properties and numeric strings are invalid. No parent/profile/collection/owner overrides.

ExpectedRevision positive safe integer <=9007199254740991; envelope must match existing supported strict native schema. EditedAt UTC >=10ticks and floors to microseconds before persistence. Revision governs conflicts rather than client-time ordering; older/equal representable timestamps are valid. Replacement retains current keyEpoch; changed envelope requires both fresh keySalt and nonce. Identical envelope is allowed for metadata edits. Unsupported/invalid visible input400 validation_failed without mutation.

200 folder_updated returns exactly folderId/revision/serverSequence/editedAt, matching existing folder-create metadata. Target identity matches request, revision expected+1 safe, sequence positive safe and strictly increases in storage, editedAt exactly normalized UTC. Validate full result before attaching payload. No envelope/grant/usable key/private owner metadata returned; no-store success/failure.

Missing identity401 authentication_required, missing handle401 vault_access_required; current access or visible read-only denial403 vault_access_denied; inactive/terminal/missing actor or hidden target404 not_found. Stale/exhausted expected revision, new unheld authority, winning concurrent edit or exhausted global sequence409 revision_conflict. Necessary native/persisted metadata/ancestry/dependency corruption503 persistence_unavailable. Caller cancellation propagates; internal cancellation/Db/Timeout/Json/Format exceptions fail503. Shared actual empty413 and its known schema Minor remain.

## Current folder authority

Target-first candidate publicID at most1; complete same-owner ancestry includes target itself and all parents, finite cycle path. Actor/account/session/selection, eligible owner collections/current grants, active folders and typed terminals use current statement_timestamp. Session issuance/renewal expiry/current positive policy+generation and owned active selected profile must pass; a disappearing selection never becomes account-wide.

Own wide access; selected own direct ProfileFolder target/ancestor or selected eligible ProfileCollection with actual CollectionFolder target/ancestor; foreign current RO/RW actual CollectionFolder target/ancestor. ProfileRecord/CollectionRecord NEVER grant folder authority. Hidden/incomplete ancestry404 before content/native; relevant fully active scoped cycle503, unrelated/unselected cycles remain hidden. Read-only scope403 only after nonrevealing visibility and necessary native evidence; stale expected revision cannot expose hidden content or override denied operation.

Every eligible foreign contributor, including overlapping RO alongside a valid RW route, is native-verified against current collection/grant/envelope/epoch/revision/owner/recipient pins. Parse one owner and recipient pins once per party. At least one current native RW included grant enables foreign write. Unselected/unrelated/revoked corrupt grants are excluded; no unrelated owned profile/collection/ancestor ciphertext inspection.

## Transaction and concurrency

Lock own account FOR NO KEY UPDATE; current own session FOR UPDATE; selected own profile if present FOR UPDATE. Initial authority snapshot resolves target. Lock current eligible target-owner collections ordered publicID FOR SHARE, recipient grants ordered publicID FOR SHARE, then only target folder FOR NO KEY UPDATE. No foreign account/profile locks or ancestor/descendant locks. Target lock can legitimately wait for creation editing that parent, but no lock is acquired upwards afterwards; test actual FolderCreateStore interleaving rather than asserting all folder locks absent.

Reload current authority after lock waits, reject missing/new unheld collections/grants, verify every native contributor and write permission before stored corruption and stale revision checks. Validate stored envelope/revision/sequence/time, preserve winning state. Guarded UPDATE includes full current recursive authority, writable target, expected revision, previous sequence and original envelope plus subset of verified/held contributors. Advance target revision, global serverSequence and concurrencyStamp; update only target envelope/time. Verify result and commit once. Guard miss reloads current error/permission then409. Failed verification/post-write faults rollback all row changes; PostgreSQL sequence gaps are allowed and not transactional content mutations.

Content update does not advance unchanged parent/profile/collection/child metadata or alter membership. Current monotonic sequence is the existing synchronization-change foundation, not a claim of durable offline delivery; UC48 supplies durable ordered visibility integration before release. Concurrent same-revision updates have exactly one winner; lost-success retry409 preserves winner. Current expiry/revoke/downgrade/member/ancestor movement/terminal or owner protection rotation must win at final guarded statement. Lock-set growth409 rather than accepting newly unverified native authority.

## Verification and limits

Main: owned wide/Master/PerProfile direct and inherited scope; actual native RO/RW root/target/descendant folder memberships; exact output/preserved ownership/parent/associations/children; identical envelope/time normalization.
AF01: strict visible transport/body/envelope/path and reused salt/nonce/epoch400; no mutation.
AF02: actual record-only granted/selected access succeeds first, folder update404; hidden/terminal folders/ancestors/owner/collections/grants; hidden corrupt cipher with stale revision404.
AF03: identity/handle/current account/session/selection paths, natural expiry at all lock waits, RO403; no selected widening.
AF04: stale/exhausted revisions, concurrent owners/recipients, lost response, rotation/permission races, final authority growth/sequence exhaustion409 and winner preserved.
AF05: post-write injected fault rollback/retry, necessary native/strict stored numeric/pin corruption503, unavailable DB/internal cancel versus caller cancellation, scoped cycles503.

Actual target+self ancestry EXPLAIN ANALYZE against100 unrelated deep folders; exactly one target folder lock and own account lock, ordered collections/grants and no foreign account/profile/ancestor locks. Reciprocal native folder writes, actual creation target-lock interleaving and native owner rotation compete without lock cycles or stale overwrite. Fresh whole Command/Data and actual native HTTP, final unfiltered six families >=90% production line/branch reported, locked restore/audits/native corpus/helpers/spec/OpenAPI/range whitespace and one fresh review/exact-head threeCI.

Shared OpenAPI requiredness/nullable refs/empty413; inherited minor fixtures/classification/name cleanup remain recorded follow-ups. Dedicated integrity-bypass incomplete-ancestry fixtures are optional confidence, not claimed demonstrated by normal-FK fixtures. BeforeUC35 implicit recipient-account FK lock audit stays mandatory. Production workload/client keys/decryption, future move/trash/restore/purge and durable synchronization have separate gates.
