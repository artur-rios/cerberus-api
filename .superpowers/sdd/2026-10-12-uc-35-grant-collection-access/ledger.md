# UC-35 SDD ledger

## Authorization and binding

- User authorization: continue the previously authorized unattended sequential
  use-case batch through design, TDD implementation, task review, final review,
  exact-head CI, normal squash merge, closure and branch cleanup.
- Issue: #36, UC-35 Grant collection access.
- Base: `e6ea84cebd166d897fa5613f3e2c3ab7171f213a`.
- Branch: `feature/uc-35-grant-collection-access`.
- Project item: `PVTI_lAHOAgOUtM4BmFmuzg_LyxY`, In Progress.

## Mandatory preflight audit

- PostgreSQL 18.6 reproduced a reciprocal deadlock when each grant transaction
  locked only its owner account before insert.
- The PostgreSQL diagnostic named the implicit recipient FK check as `FOR KEY
  SHARE` on `account`.
- One `ORDER BY public_id FOR UPDATE` over both account rows allowed both reciprocal
  inserts to commit; exactly two rows remained.
- Adopted invariant: both accounts ordered and locked before session, collection
  or grant. No recipient-account lock after a collection/grant.

## Design rulings

1. Request is exact five fields with required nullable `expectedRevision`: null is
   fresh revision 1; positive current revision is explicit revoked regrant.
2. Regrant preserves grant public identity and advances revision once; active
   duplicate grants conflict and UC-36 owns active permission changes.
3. Success returns grant identity/access plus direct record/folder visibility roots,
   never profiles, wrappers, pins or keys.
4. Recipient must be distinct, active, nonterminal and protected; grant creation
   creates no recipient profile association.
5. Caller envelope binding failures are 400; malformed required stored native
   evidence is 503; stale/duplicate/reserved state is 409.
6. Only the grant row mutates. Collection/member and both accounts' protection
   graphs remain byte-for-byte and metadata exact.

## Task status

- [x] Task 1 — Domain and application contract
- [ ] Task 1 review
- [ ] Task 2 — PostgreSQL store and concurrency
- [ ] Task 2 review
- [ ] Task 3 — HTTP/docs/OpenAPI/full verification
- [ ] Task 3 review
- [ ] Whole-branch review
- [ ] Exact-head delivery and closeout

## Review findings and evidence

Record task commits, focused/full counts, review findings, corrections, rulings,
CI head/base and merge audit here as the work advances.

### Task 1 implementation

- Base: `97b3040`; implementation commit: `feat: define collection access grant contract`.
  Independent task review remains pending and parent-controlled.
- Read the complete binding design, implementation plan, tracked ledger, and
  adjacent collection command/domain/test and recipient-envelope patterns.
  Applied test-driven development and verification-before-completion.
- Test-first RED: the new focused tests preceded every production file. The
  Linux runner `wsl.exe -d Ubuntu -- bash
  /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-35-grant-collection-access/task-1-linux.sh
  cerberus-uc35-task1-red` restored successfully, then exited 1 with exactly six
  CS0246 diagnostics for the absent grant command, handler, details and store
  types (`cerberus-uc35-task1-red.log`). A single-node Windows build independently
  produced the same six errors; ordinary sandbox Windows test attempts returned
  exit 1 before execution without diagnostics and are not passing evidence.
- Minimal production change: immutable collection grant input/request/details
  and store interface; exact five-field command; trusted actor/access/path context;
  required nullable revision, strict RO/RW access and RecipientEnvelope shape plus
  exact grant/revision matching; authority precedence and access-handle hashing;
  existing safe-error allowlist; fail-closed store output; exact six-field output
  with sorted, unique nonzero typed record/folder arrays.
- First compiled focused run: 118 passed, two failed before test execution because
  xUnit cannot convert int InlineData to nullable long. Corrected only those two
  test literals to `1L`/`8L`; production behavior was unchanged.
- Focused GREEN: same Linux runner with `cerberus-uc35-task1-green`, exit 0,
  120/120 passed, zero failures/skips (`cerberus-uc35-task1-green.log`).
- Full Command GREEN: same runner with `cerberus-uc35-task1-command all`, exit 0,
  1,557/1,557 passed, zero failures/skips (`cerberus-uc35-task1-command.log`).
  The runner uses a read-only checkout and NuGet fallback cache, disposable `/work`,
  excludes old bin/obj/TestResults, restores with locked mode, and runs the
  unfiltered Command project. Scripts and raw logs remain ignored task evidence.
- Tests cover all five required members, explicit null versus missing revision,
  initial/regrant/max-1-to-max advancement, both access values, every envelope
  field, strict JSON/canonical IDs, trusted context and auth/access precedence,
  error allowlist and statuses, null/error-plus-data/malformed store outcomes,
  exact request/output JSON fields, sorted arrays without mutation and caller
  cancellation propagation.
- No store, schema, HTTP, DI, OpenAPI or dependency change. Current pin/signature,
  recipient lifecycle, authority selection, atomic storage, direct membership and
  ancestry validation belong to Task 2. Transport and full six-family validation
  belong to Task 3; external-client qualification remains the release gate.
- Verification: focused and full Linux runs above, self-review of all changed
  files, and working/staged whitespace checks passed before commit.

