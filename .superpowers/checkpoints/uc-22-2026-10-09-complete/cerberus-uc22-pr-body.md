Permanently deleting an owned active or trashed record now commits a terminal intent, flushes its external erasure event, removes its ciphertext and associations, then returns confirmation. A ledger outage after intent leaves the record inaccessible with durable purge work; it does not roll deletion back. Selected trash access revalidates restricted history against current owned scope.

Terminal IDs and ledger files now use resource kind plus UUID. Existing readers, writers, native grants and ID reservation use that identity, so deleting a record preserves a folder, collection or other kind sharing its UUID. The migration preserves legitimate legacy markers, the ledger still reads legacy files, and record replay physically removes restored ciphertext before traffic opens. Pending terminal records leave key-rotation inventory while recoverable trash remains.

`DELETE /api/records/{id}/permanent` accepts exactly `expectedRevision` and returns only `recordId` and `deletedAt`. Current owners can erase damaged opaque content; both native recipient access modes are denied. Persisted work IDs, tokens, keys and exclusive database-time expiry fence every purge mutation and completion.

Closes #23.

## Validation

- Fresh unfiltered full suite: **4,596 passed, zero failed/skipped** across Domain 313, Shared 125, Command 783, Query 259, Data 1,634 and WebApi 1,482.
- Coverage: **98.4% line / 91.7% branch**, with all six production assemblies at least 90% line coverage.
- New focused coverage: 47 typed-identity Data tests, 141 permanent-deletion Data tests, 66 Command tests and 107 actual native HTTP tests. Missing APIs/routes and real fencing/history/clock/time defects were observed RED before correction, then GREEN.
- Locked restore, NuGet direct/transitive audit, 153 native OSV entries, 322 native corpus cases in four directions, 36 native helpers, 23 Python helpers, requirements and OpenAPI write/drift/shape checks passed.
- Complete branch range whitespace check passed, including every added file. The inherited UC21 formatting minor remains unchanged.
- Single fresh whole-branch review found no blocking defects after grading actual effects; all three Minors are retained below. Exact-head branch-policy/test/docker CI remains mandatory before merge.

## Rulings I made

- Ruling: Repair typed terminal identity across ALL existing predicates/schema/ledger before adding any physical purge — prior UC09–21 obligations make cross-kind availability a prerequisite, not an optional cleanup — cost if wrong: broad predicate edits require full current-family regression; omitted or misclassified kinds can falsely hide or expose restored resources.

- Ruling: Support terminal grant kind alongside account/profile/record/folder/collection — existing grant visibility already consults terminal metadata and must have its own identity namespace — cost if wrong: later grant erasure must retain this exact kind and record minimal events consistently.

- Ruling: Validate legitimate legacy GUID filenames using the stored kind and preserve them while writing typed filenames — existing external erasures must survive upgrade and backup replay; no ledger rewrite/deletion — cost if wrong: duplicate same-kind legacy/new events may require idempotent earliest-time replay and extra reads.

- Ruling: Invalid legacy terminal kinds fail migration closed; fixture-only kinds become actual types with denial assertions unchanged — production previously valid ledger kinds remain supported and unknown erasures cannot silently acquire a guessed type — cost if wrong: operators must repair invalid existing metadata before migration/traffic, rather than automatic unsafe reclassification.

- Ruling: Commit authorized typed terminal intent and durable work BEFORE external ledger, then fsync ledger BEFORE physical removal — neither DB-first physical deletion nor rollbackable ledger-first authorization is crash-safe across two systems — cost if wrong: a503/cancellation after intent is irreversible pending deletion and requires durable worker recovery; clearly documented and tested, no false rollback promise.

- Ruling: Permanent-delete success returns only recordId/deletedAt — physically absent records need no invented live revision; terminal identity/time is minimal erasure metadata — cost if wrong: later synchronization must add durable ordered visibility/tombstone integration independently, not infer sequence from this response.

- Ruling: Selected trashed scope uses restricted typed operation snapshot plus current active owned profile/collection/folder authority — UC20 removed active links but retained these exact historical IDs; never blindly revive links/grants or return snapshots — cost if wrong: lost historical paths require account-wide access, while newly current valid organizational links can expose deletion authority over corresponding retained historical membership.

- Ruling: Owned permanent deletion ignores opaque ciphertext/client-time validity but validates required revision/sequence and parent capacity — removing damaged encrypted content must not require decryption or parsing it; optimistic concurrency still protects winners — cost if wrong: corruption of unused client content metadata does not block erasure, but damaged required counters require repair.

- Ruling: Matching maximum safe target revision remains deletable without target increment; active directly affected parent metadata advances once at intent — no record content remains after purge, while live parent structure must reflect removal — cost if wrong: later sync must consume terminal events rather than expect a final live record revision; exhausted live parents still409.

- Ruling: Current actor/session/own profiles/own collections/immediate folder/content lock protocol and final current scope guards apply; no foreign account/profile locks or late earlier locks — matches actual current writers and avoids reciprocal deadlocks — cost if wrong: newly contributing authority causes retry and future grant writers require the recorded BEFORE UC35 implicit FK audit.

- Ruling: Existing retention work schema and exact typed terminal marker are the purge intent/outbox; workers require matching current claim token and exclusive expiry — no credential or extra encrypted snapshot must persist for an already authorized erasure — cost if wrong: crashes require safe lease recovery; unknown/stolen work must never produce physical deletion or false completion.

- Ruling: Record backup replay physically removes exact record content/links before traffic opens; other kinds retain typed fail-closed tombstones until their purge UCs — this UC must prove record resurrection prevention without pretending all future deletion handlers exist — cost if wrong: later resource erasure still requires its own physical replay handler and must never clear selected ProfileId.

- Ruling: Fresh UC21 required CI/unfiltered4235PASS and verified identical merged tree provide the unchanged UC22 baseline — fresh fetch/clean worktree/tree equality and locked restore verified; no redundant identical full-suite run before new tests — cost if wrong: any new baseline failure found by required subsequent whole-family/full-suite checks remains blocking and must be diagnosed.

- Ruling: Independent formal security/protocol/client approvals remain expressly development-only deferred — ordinary review/native/full suite/CI and main/tag/release gates remain mandatory under user instruction — cost if wrong: latent qualification defects await mandatory release review; no server fixture certifies client decryption.

- Ruling: Immediate request purge claims use the operator-configured RetentionInterval as lease, with injected CerberusOptions/TimeProvider, matching the existing worker — do not invent a hidden purge lease default or service dependency — cost if wrong: an interval too short for durable ledger I/O may produce pending terminal deletion503 until a fresh worker claim completes; persisted database expiry always fences removal.

- Ruling: Complete protection rotation excludes typed terminal records awaiting physical purge but still includes all recoverable trash — a committed irreversible terminal record is no longer recoverable content and ledger outage must not block rotation of surviving ciphertext — cost if wrong: future terminal resource kinds need equivalent inventory integration in their purge UCs; stale supplied extra inventory retains the existing validation_failed response, never consumes proof or mutates content.

- Ruling: Record-purge completion fences the persisted claim using fresh database statement time after its row-lock wait, rather than caller-supplied clock time; existing other retention-operation clock contracts remain unchanged — terminal deletion cannot be falsely acknowledged after database expiry — cost if wrong: future purge kinds require equivalent completion integration, and record-purge tests cannot use an artificial caller time to prolong a real lease.

- Final: Ruling: Independent formal security approval remains development-only deferred — the user explicitly deferred this approval while current ownership and authorization received ordinary review and tests — cost if wrong: latent security defects await mandatory release qualification.

- Final: Ruling: Independent formal protocol and cryptographic qualification remains development-only deferred — existing native binding and runtime rejection were reviewed, but that does not establish formal qualification — cost if wrong: protocol defects must be discovered and resolved before release.

- Final: Ruling: External-client interoperability, authenticated decryption and operability remain release prerequisites — server/native fixtures cannot establish real-client approval and the user explicitly deferred it for development — cost if wrong: clients may remain unable to operate until qualification and any required provisioning are complete.

- Final: Ruling: Production deployment, production restore drills and release qualification are outside this develop integration — main/tag/release gates remain mandatory, and no production deployment or customer deletion is performed — cost if wrong: operational restore or release defects remain unqualified until those gates run.

- Final: Ruling: Durable ordered terminal synchronization remains later integration before release — current reads deny the terminal record immediately, but recordId/deletedAt is not an ordered synchronization event — cost if wrong: offline clients can retain stale local visibility until terminal synchronization is implemented and qualified.

- Final: Ruling: Physical purge/replay for account, profile, folder, collection and grant plus general trash expiry/restore remain their later use cases — current typed fail-closed visibility and exact record purge preserve other resource kinds — cost if wrong: other kinds retain restored ciphertext until their own typed physical handlers are implemented and tested; selected ProfileId must never be cleared to widen access.

- Final: Ruling: Implicit recipient-account FK and deadlock audit is mandatory BEFORE UC35 grant writers — current implemented writer interactions were reviewed and exercised, while nonexistent writers cannot be certified — cost if wrong: future sharing operations may deadlock if the prerequisite or established lock order is ignored.

- Final: Ruling: The inherited UC21 trailing-space blank remains its existing deferred formatting finding — this branch does not change that line and its entire new range passes whitespace verification — cost if wrong: formatting debt remains visible until authorized cleanup, with no current runtime effect.

- Final: Ruling: The executor completes remote exact-head CI and all delivery gates before declaring UC22 done — the read-only reviewer could not certify future remote actions; normal merge, nine DoD, issue/project state, branch deletion and tested tree equality will be verified — cost if wrong: claiming completion before those gates would leave unintegrated or unverified work.

## Deferred minors

- Final: minor (deferred): Ledger UnauthorizedAccessException escapes to 500/internal_failure instead of documented dependency 503/persistence_unavailable; terminal intent/outbox survives, reads stay denied and recovery retries. Correct the permission-error mapping with a deterministic regression before release; current cost is wrong outage classification without false erasure success.

- Final: minor (deferred): Shared OpenAPI omits required vault-access header, request body and recordId/deletedAt output requiredness; runtime rejects incomplete requests and enforces exact success output. Correct the shared generator before client/release qualification to avoid incomplete generated requests and unnecessarily optional models.

- Final: minor (deferred): Shared OpenAPI advertises ProblemDetails content for an actually empty 413; runtime rejection is correct, but generated clients may attempt to deserialize an absent body. Correct shared response metadata before client/release qualification.

Independent formal security/protocol and real-client qualification remain development-only deferred under the owner's instruction; main/tag/release gates remain mandatory. No production deployment or live customer deletion is performed.
