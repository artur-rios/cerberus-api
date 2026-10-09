# UC22 research notes (read-only preparation while UC21 CI runs)

Intent: implement explicit permanent deletion of active or trashed OWNED records,
prevent stale create/edit/sync and backup resurrection, preserve other kinds with
the same public GUID, and keep selected profile handles nonnull/fail-closed.
Architectural change: typed terminal identity plus durable deletion handoff across
PostgreSQL and external fsynced ledger; requires written design and TDD plan.
Existing all-issues authorization covers automated design/plan gates and per-UC
delivery. No production deletion or operator deployment is being performed.

Sources loaded freshly: UC22 main/AF01–05; FR-RC07/08; authorization owner-only
create/delete and internal narrowly scoped retention; lifecycle/backup rules;
Testing/Technology/initial+formal workflow/Operations; existing RecordTrashStore,
RecordMoveStore, FileErasureLedger/TerminalErasureStore, RestoreReconciler and
RetentionRunner/Executor/WorkStore. No AGENTS detected in earlier UC21 inspection;
recheck before new branch. One UC22 branch/issue23/PR from fresh develop after UC21.

Approaches evaluated:
1. Single DB transaction then external ledger: unsafe acknowledgment/crash window
   if DB data removed before external record; a backup can resurrect.
2. External ledger inside uncommitted authorization transaction: rollback can
   leave externally erased resource visible in the live database.
3. Recommended: irreversible authorized database intent and typed tombstone plus
   durable outbox/work commit; then external ledger durable write; then physical
   purge in a separate idempotent/fenced transaction. Intent immediately hides
   target in every typed current reader/writer. A dependency failure after intent
   returns503 without deletion confirmation but cannot undo terminal intent;
   worker durably retries and no unsafe create reuses the same record GUID.

Typed prerequisite BEFORE ANY purge implementation:
- Change terminal unique index from ResourceId to (ResourceKind, ResourceId).
- Add kind validation (account/profile/record/folder/collection/grant) for stored
  rows and ledger; existing legitimate five-kind legacy entries remain valid.
- Change IsErasedAsync API to require kind; never offer untyped lookup.
- File ledger uses kind-GUID.json for new writes; reads/validates both existing
  GUID.json legacy names (kind comes from authenticated local protected file)
  and typed names. Never overwrite/delete legacy entries. Same-kind duplicates
  deduplicate earliest deletion time consistently; distinct kinds coexist.
- Qualify ALL current SQL/LINQ terminal predicates with correct kinds. Inventory
  /tmp/cerberus-uc22-terminal-inventory.txt; aliases a/owner/recipient account,
  p profile,r record,f folder,c collection,g grant; special x.PublicId queries
  depend on actual entity. Compound OR requires separate parenthesized typed
  terms, never a single broad kind condition that misses another term.
- Existing four test sites using ResourceKind="fixture" must use actual kind
  under test, preserving all denial assertions. Invalid legacy DB kind fails
  migration closed rather than silently authorizing or reclassifying erasures.
- Same-GUID cross-kind behavior tests cover real account/profile/record/parent/
  collection/grant readers, writers, stale-create reservation and ledger/replay.
  Migration from old uniqueness is tested against a real old-schema database.

Proposed DELETE /api/records/{id}/permanent: canonical nonzero route, current
identity+single canonical vault access, no query, strict VaultProofBody, exactly
JsonRequired ExpectedRevision safe-positive. Owner only, account-wide active or
retained record; selected active follows current complete ancestry/direct/owned
collection scope. Selected trash uses restricted historical operation membership
and still-active owned selected profile, never blindly revive access or grants;
explicitly document and test profile membership visibility semantics. Foreign
native-visible active RO/RW403 before stale/content/parent; hidden404 before bad
native. Trashed foreign records remain hidden404. Corrupt active ciphertext can
still be erased (no decryption/content parse), safe revision metadata required.

Intent transaction follows own-account→session→own selected/direct profiles→own
direct collections→active immediate folder→record→existing typed links order.
Recompute current authority after every wait/finalstatement; new unheld earlier
parents or changed membership409. Active immediate/direct parents bump exactly
once with safe revision/sequence, ciphertext/client times unchanged; trashed
record has already detached, no duplicate bumps. Sequence/max failures roll back
before irreversible intent. Final authority exclusive statement-time expiry.
Intent stores minimal typed terminal marker and immediate-due record-purge/ID
work, logically removes links/trash snapshot if necessary atomically. Preserve
selected ProfileId, no profile deletion or access widening. Record owner/content
identity unchanged until purge. No meaningful permission changes occur after
intent: narrow worker acts only on previously authorized persisted intent.

Finalizer durably records record erasure first, then deletes exact record and all
direct associations/typed trash membership. Keep shared collections/folders,
other records/profiles/grants and cross-kind sameGUID resources intact. Remove
empty trash operation/work safely, preserve nonempty cascades for future UC53.
Current worker claim token+exclusive expiry fence EVERY purge write; caller path
can safely claim existing queued work via RetentionWorkStore. Lost response or
terminal target reload404; durable worker idempotence is separate from HTTP retry.
503 after intent is documented irreversible pending deletion, never false success.
Cancellation before intent no mutation; cancellation/crash after intent retains
outbox for worker. Ledger errors cannot authorize data, leak secrets or mark work
complete. Native/client/security qualification remains develop-only deferred.

Restore replay must upsert typed erasure and physically remove matching RECORD
rows/ciphertext/associations before RestoreTrafficGate opens. Test actual pg_dump
before deletion then restore/replay, sameGUID across kinds, corrupt ledger and
dependency failure keep traffic closed. Other kinds retain conservative typed
tombstone exclusion until their own purge use cases; no profile-session nulling.

Unresolved details to settle in binding spec after examining complete patterns:
- Minimal terminal response shape (prefer recordId/deletedAt, no invented revision
  for a physically absent record; future durable sync has separate integration).
- Exact selected trashed membership rules and active current profile revalidation.
- Reuse existing terminal table as intent vs separate minimal outbox entity; work
  key+terminal marker likely suffice without retained request secrets.
- Parent structural updates and recoverable trash operation cleanup must happen
  only once across retries; typed terminal insertion is irreversible linearization.
