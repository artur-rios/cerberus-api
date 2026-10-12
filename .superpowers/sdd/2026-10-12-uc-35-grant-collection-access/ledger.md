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

- [x] Task 1 - Domain and application contract (`e33b1a0d810575df69c3e972e41e28d520c02c4e`)
- [x] Task 1 review
- [x] Task 2 - PostgreSQL store and concurrency
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

### Task 1 independent review

- Reviewer model/effort: GPT-6 Astra, ultra.
- Exact range: `97b3040..e33b1a0d810575df69c3e972e41e28d520c02c4e`.
- Findings: Critical 0, Important 0, Minor 0; accepted.
- Independent focused rerun: 120 passed, zero failures/skips.
- Confirmed exact five-field input, required explicit-null handling, safe revision
  arithmetic, strict access/envelope/grant agreement, trusted-context exclusion,
  authentication/access precedence, cancellation, safe error allowlist, malformed
  store-output rejection and exact sorted six-field output without sensitive data.
- Authority, native pins/signatures, ordered account locking, grant lifecycle,
  ancestry and atomic rollback remain intentionally scoped to Task 2. Transport
  and route behavior remain scoped to Task 3.

### Task 2 implementation

- Added the dedicated PostgreSQL grant store and real PostgreSQL 18.6 tests;
  no migration, dependency, HTTP registration or unrelated production change.
- Applied test-driven development, systematic debugging and verification before
  completion. Initial executable scaffold supplied no grant behavior. Behavioral
  RED: `cerberus-uc35-task2-behavior-red.log`, 3/3 failed on missing successful
  initial RO/RW and revoked-regrant outcomes, exit 1. Expanded contract RED:
  `cerberus-uc35-task2-contract-red.log`, 55 failed / 13 passed / 68 total, exit 1.
  Compilation-only scaffold/harness diagnostics were corrected before these
  behavioral runs and are not claimed as behavioral red evidence.
- First contract GREEN: `cerberus-uc35-task2-contract-green2.log`, 68/68 passed.
  Subsequent fault RED: `cerberus-uc35-task2-race-red2.log`, 95 passed / 1 failed;
  a grant changed during final member projection escaped the earlier row check.
  Moved exact grant-row verification after projection. Expanded GREEN:
  `cerberus-uc35-task2-expanded-green.log`, 119/119 passed, zero skips.
- Final focused GREEN: `cerberus-uc35-task2-final-focused.log`, 127/127 passed,
  zero failures/skips, exit 0. The Linux runner is the ignored
  `task-2-linux.sh` beside this ledger; it copies the read-only checkout to a
  disposable SDK 10 container, restores in locked mode with a read-only NuGet
  fallback cache and runs the real PostgreSQL Testcontainers fixture.
- Store behavior: target-first authority classification; native-validated foreign
  denial without requested-recipient locks; distinct active protected recipient;
  ordered owner/recipient account `FOR UPDATE` in one statement before session,
  selected owned profile, owned collection and captured grant/collision locks;
  current authority rechecks after every wait; current native owner/recipient
  protection parsed once; guarded insert or same-ID exact-revision reactivation;
  exact direct active member projection with complete same-owner ancestry and no
  profiles; final grant/projection verification; atomic rollback and cancellation.
- Confirmed with parent: a self-recipient relationship returns 400
  `validation_failed`; missing/closing/terminal/unprotected recipients return 404;
  malformed present protection returns 503.
- Tests cover selected Master/PerProfile and wide scope; current lifecycle and
  native binding; hidden versus visible foreign targets; active duplicates,
  mismatched/stale regrant, collisions, typed terminal reservations, unsafe and
  exhausted revisions plus max-1 to max; opaque revoked-envelope replacement;
  exact unchanged account/protection/session/profile/collection/member state;
  sorted typed direct member roots; ancestry exclusion and active cycle failures;
  post-write insert/reactivation/projection/native faults and clean retry.
- Real concurrency evidence includes reciprocal grants, initial/regrant same-pair
  winners, each owner/recipient/session/profile/collection/grant wait expiry,
  current state loss after waits, before/after authorization expiry, actual
  recipient protection and owner membership/trash/rotation writers in both
  orders. Recipient lifecycle races use real account-lock/update transactions:
  this repository does not yet contain an account-closure store.
- An actual grant INSERT blocks on the recipient account's implicit FK lock;
  `pg_blocking_pids` establishes the blocker. Canonical-order verification waits
  until the lower recipient blocks the store, then proves the higher owner and
  target collection remain independently lockable with `NOWAIT`. Captured SQL
  proves the single ordered account statement and later phase order, exactly one
  grant-row mutation, no content/ancestor locks, and EXPLAIN ANALYZE reports one
  target with 100 unrelated collections.
- Exact-current-source full Data GREEN: `cerberus-uc35-task2-final-data.log`,
  3,558/3,558 passed, zero failures/skips in 8m05s, exit 0. Invocation:
  `wsl.exe -d Ubuntu -- bash
  /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-35-grant-collection-access/task-2-linux.sh
  cerberus-uc35-task2-final-data all`. The unfiltered project run includes all
  127 current grant cases. The runner's `-source.sha256` file matches the final
  workspace: store `7406004b408233bb4f524176ead7177b8c3f78a9b531965911a180aa7fa2f86c`,
  tests `f7c9845d578dea2342a7e919e8ed2470a400f94ffe5cc85889b166d2ee86cfac`.
- A preliminary full run also passed 3,550 tests with the earlier 119-case test
  file, but its outer Bash wrapper exited 1 because the ignored runner was edited
  while Bash still had it open. It is not the acceptance run; the exact-source
  rerun above completed with exit 0 and supersedes it.
- Self-review, source-hash comparison and working/staged whitespace verification
  completed. Commit subject: `feat: grant collection access atomically`.
  Independent task review remains pending and parent-controlled.

