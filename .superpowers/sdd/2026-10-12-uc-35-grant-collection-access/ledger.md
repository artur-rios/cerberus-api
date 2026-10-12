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
- [x] Task 2 review
- [x] Task 3 - HTTP/docs/OpenAPI/full verification
- [x] Task 3 review
- [x] Whole-branch review
- [ ] Exact-head delivery and closeout

## Review findings and evidence

Record task commits, focused/full counts, review findings, corrections, rulings,
CI head/base and merge audit here as the work advances.

### Task 1 implementation

- Base: `97b3040`; implementation commit: `feat: define collection access grant contract`.
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

### Task 2 independent review

- Reviewer model/effort: GPT-6 Astra, ultra.
- Exact range: `9b43722..0cd9f5fe0edf6f31e00024b379fc1e6471956354`.
- Findings: Critical 0, Important 0, Minor 2; accepted. The nonblocking
  test-strength notes are that some same-pair/writer-order outcome tests do not
  independently prove a database wait, and the EXPLAIN assertion proves target
  cardinality rather than bounded executor work. Existing stronger tests do use
  `pg_blocking_pids` for the implicit-FK/canonical-order waits, while static
  review confirmed that the current member and ancestry queries are rooted in
  the single target. These notes do not identify a production defect and are
  retained as future hardening rather than changing accepted Task 2 code.
- Independent focused PostgreSQL 18.6 rerun: 127/127 passed, zero failures/skips,
  exit 0. The reviewer confirmed the runner hashes match the final store and test
  sources; `git diff --check` passed and the tracked tree remained clean. Evidence:
  `cerberus-uc35-task2-review.log`.
- Confirmed target-first authority, exact selected-profile handling, hidden 404
  versus native-validated visible 403, one ordered dual-account `FOR UPDATE`
  before every later lock, no weak-lock upgrade, current protection/envelope
  binding, insert/reactivation conflict rules, one-row mutation, direct-root
  ancestry projection, final saved-row guard, rollback and cancellation.

### Task 3 implementation and verification

- Base: `694362b678829b57305e3eb21eabf0622b465af4`. Applied test-driven
  development and verification-before-completion. Read the binding design/plan,
  ledger, accepted Task 1/2 code/tests and adjacent HTTP, strict-body, DI,
  documentation and OpenAPI patterns. No repository `AGENTS.md` was present.
- Executable behavioral RED preceded the production edit. Invocation:
  `wsl.exe -d Ubuntu -- bash
  /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-35-grant-collection-access/task-3-linux.sh
  cerberus-uc35-task3-red`. The new HTTP suite compiled and ran against real
  PostgreSQL 18.6: 104 failed / 1 passed / 105 total, zero skips, exit 1.
  Success cases expected 201 and received the absent route's empty 404; error
  cases exposed the same missing endpoint. Only shared oversized transport
  already passed. Raw evidence: `cerberus-uc35-task3-red.log`.
- Minimal production edit: one POST controller action reusing `VaultProofBody`,
  canonical route/no-query/single-header checks, trusted actor/path context and
  the accepted handler's status map; scoped grant store/validator/handler DI.
  No Task 1/2 implementation, lock protocol, schema or dependency changed.
- Focused GREEN: the same runner with `cerberus-uc35-task3-green`, exit 0,
  105/105 passed, zero failures/skips. Tests are unchanged from RED. The suite
  proves initial RO/RW wide/selected Master/PerProfile grant creation, exact
  six-field sorted direct roots, real current collection/record/folder GET/list
  visibility including future descendants, no profile/key leakage, and unchanged
  collection/member/account/protection/session/profile state. Selected recipients
  stay isolated until their own actual profile-association HTTP operation.
- HTTP alternatives include protected recipients without sessions/profiles,
  explicit same-ID revoked regrant with opaque corrupt-old-envelope replacement,
  preserved row identity and once-only revision, active/stale/wrong-ID/terminal
  conflicts, native-validated foreign RO/RW denial and corrupt-evidence 503,
  owner/collection/epoch/identity/pin/signature/grant/revision binding failures,
  current actor/recipient lifecycle and session state, strict five-field required
  nullable-CAS JSON, canonical IDs, media/UTF-8/BOM/compression/header/query
  rejection, identity errors, no-store and empty 413. Real post-insert/reactivation
  trigger faults and database outage prove atomic rollback and clean retry.
- Generated OpenAPI using the actual application generator. The Windows sandbox
  attempt exited 1 before useful diagnostics; the authorized native invocation
  succeeded. Only the new route and its three schemas changed. Request required
  properties and nullable expected revision are represented; shared broader media,
  output requiredness/nullability/wrapper and empty-413 limitations remain explicit.
- Updated collection API, foundation status, changelog and README UC35/M04
  progress. README Done/1-of-6 is the intended merged feature state; remote issue
  and project state are untouched. Completed-FK-audit references replace old
  pending reminders within these updated documents. Removed only a trailing blank
  EOF line from each inherited design/plan for the original-base whitespace gate.
- Full matrix runner: `task-3-full.sh cerberus-uc35-task3-full2`, with a fresh
  disposable source copy, read-only checkout/NuGet fallback cache, actual Docker
  PostgreSQL fixtures and unfiltered Release `dotnet test` with TRX plus XPlat
  coverage. Its initial attempt (`cerberus-uc35-task3-full`) failed before restore
  because a login shell replaced the image SDK PATH; using a non-login shell
  preserves the configured runtime. No product/test change was needed.
- Locked solution restore, live direct/transitive NuGet audit (zero known
  vulnerabilities), Release build (zero warnings/errors), 23 Python helper tests,
  specifications and generated OpenAPI drift all passed in the full runner.
  Final documentation specification validation was rerun directly through WSL:
  13 Markdown files, 7 template structures, 107 FRs/55 UCs, 12 features, 29 BRs,
  6 milestones and 56 backlog entries. Full Web family passed 2,832/2,832 with
  zero failures/skips in 3m30s; full-suite/coverage evidence follows.
- Native verification ran independently in a fresh disposable Python 3.14.8 /
  Temurin 25.0.4.1+1 / Maven 3.10.0 environment with fresh hash-locked wheel and
  Maven downloads and a dedicated fresh dependency cache. Maven: 173/173;
  Python: 142/142; native helpers: 36/36; repository helpers: 23/23; no failures,
  errors or skips. Committed corpus: 322 cases in all four directions, passed.
  Live OSV audit at `2026-10-12T07:24:05.231501+00:00`: 153 package coordinates,
  zero findings, lock SHA-256
  `1e9a47097b9a2a8ee17855c5cc75b71b7b01d8fae86a8b248eb7d2805e969bab`.
- Native evidence: `native-results.md`, `native-summary.json`, raw `native-*.log`,
  JUnit XML and source manifest beside this ledger; reproducible commands are in
  `native-run.sh` and `native-in-container.sh`. The first native attempt stopped
  before tests on Windows CRLF in `mvnw`. Only the disposable copy was normalized:
  44 corpus-provenance files had to equal committed LF hashes, and the wrapper
  separately matched committed Git blob `bd8896bf2217b46faa0291585e01ac1a3441a958`
  / qualified SHA-256
  `cae96cef89ebea3531221f4ae17c23cf8edf67d00eae8306d4186ae1bbed4d02`.
  Repository protocol bytes stayed exact. The complete test/audit runner then
  exited 1 only in its final evidence summarizer because buffered helper
  diagnostics followed unittest's OK line. Corrected evidence validation against
  that preserved snapshot exited 0; passed tests were not rerun or hidden.
- Native corpus result SHA-256:
  `f9a06769d2bd7e2022ed7ff123ef0f4b5740e903d53ceedce07bc6e82ace636e`;
  OSV evidence SHA-256:
  `f38d5a1a7e3a8636f8ebd5fc0ea50959edd112063f6afb74558f12a01ac6b64a`.
  Native snapshot is preserved as `cerberus-uc35-native-evidence:latest`.
  OSV is a dated package-version observation, not runtime/OS security approval.
  `verify_protocol.py` remains the expected exit 1 with exactly
  `BLOCKED: Protocol security and client review is pending.`
- Exact 634-file source/test/helper/tool/OpenAPI hash manifest matched the
  worktree with zero mismatches. Controller SHA-256:
  `9342b1e1436238cf5b11ee8d42e27052275e600e54b84d40b85d17a13ec487b3`;
  Startup: `77829684bbc521c76b129537339d541903b4f3816ae1ef9e9fbc5e0330c19121`;
  HTTP tests: `8dac5052682b9077aded4c6273e9c7f196c8e59e48a58622c5a5dd701a55e85d`;
  OpenAPI: `46ff9407b12009e9b4140acb5e5fcfc5d196efb1c910bc6baf3d3dd53165e011`.
- Authoritative full command completed with exit 0:
  `wsl.exe -d Ubuntu -- bash
  /mnt/d/Repositories/cerberus-api/.superpowers/sdd/2026-10-12-uc-35-grant-collection-access/task-3-full.sh
  cerberus-uc35-task3-full2`. The unfiltered Release test command was
  `dotnet test src/ArturRios.Cerberus.sln --configuration Release --no-build
  --logger trx --collect:"XPlat Code Coverage"`. All 9,039 tests passed, zero
  failures/skips: Command 1,557; Query 606; Shared 125; Domain 361; Data 3,558
  (8m11s); WebApi 2,832 (3m30s). All six fresh TRX/Cobertura artifacts are saved
  in `cerberus-uc35-task3-full2-test-results.tar.gz`.
- ReportGenerator 5.5.11 merged the six fresh collectors at
  `2026-10-12T07:32:48Z`: line 98.5% (15,398/15,627), branch 92.3%
  (6,670/7,220). Exact six-production-assembly inventory and individual 90%
  floors passed: Command 1,178/1,178 (100%); Query 580/580 (100%); Shared
  311/321 (96.88%); Domain 500/503 (99.40%); Data 12,086/12,258 (98.60%);
  WebApi 743/787 (94.41%). Counts reflect the Release build; reference native
  harness code and foreign/test assemblies are excluded from production coverage.
- OpenAPI shape check passed the exact 201/400/401/403/404/409/413/503 statuses,
  exact five required input fields with nullable `expectedRevision`, and exact
  six output properties. Complete OpenAPI drift passed independently. Runtime:
  .NET SDK 10.0.401, .NET/ASP.NET 10.0.12, Debian 12 Linux x64, PostgreSQL
  18.6-alpine. Independent release-approval gate was again the expected exit 1.
- Full-suite log SHA-256:
  `aa3e1cf1e00f45f46d553f7d95a294726e1efbcb48547d5a8a9a05f87c1e61e4`;
  coverage summary:
  `4fd795fe905774132a4479e5a950f776a4568ffb1b61c927f399d66b38a26380`;
  634-file source manifest:
  `0d99fe73e6213b922c8289e779a5ca4df16ef02aa51aeba65d5b6c8897857d06`.
- Self-reviewed the exact controller/DI, new HTTP tests, generated OpenAPI and
  document diff. Working, staged and original-base whitespace gates passed.
  Task 3 implementation is complete; commit subject:
  `feat: expose collection access grants`. Task 3 independent review,
  whole-branch review, exact-head CI and delivery remain unchecked and under
  parent control. No push, PR, issue/project mutation, merge or closure occurred.
- No requested Task 3 behavior was deferred. Remaining limitations are the
  explicitly documented shared generated metadata, independent protocol/security/
  client approval, external recipient-key discovery/decryptability qualification,
  and later UC36/37/48 behavior. Native and .NET tests establish server behavior,
  not client decryptability or production qualification.

### Task 3 review correction

- Reviewed Task 3 head: `ed7dcba7a8d69c2a5eca0de2d64eb4a8446554b4`.
- Independent Task 3 review findings: Critical 0, Important 0, Minor 1;
  accepted subject to the README count correction, which is now complete.
- Accepted finding: the README headline retained stale totals of 33 implemented
  and 22 remaining business use cases despite the updated backlog tracker.
  Corrected the headline to 36 implemented and 19 remaining, matching the
  business-use-case rows (the separate foundation row is excluded).
- Verification: README tracker counts are 36 Done and 19 Todo; exact diff review
  and `git diff --check` passed. Only README and this tracked ledger changed;
  no feature behavior changed or additional runtime tests were needed.
- Task 3 review is complete after this single accepted correction. Whole-branch
  review and exact-head delivery/closeout remain unchecked under parent control.
  No push or delivery action occurred.

### Whole-branch independent review

- Reviewer model/effort: GPT-6 Astra, ultra. Exact reviewed range:
  `e6ea84cebd166d897fa5613f3e2c3ab7171f213a..6554b45674f8c93336e0016d7b85ab90b8b42704`.
- Findings: Critical 0, Important 0, Minor 2; accepted and delivery-ready from
  the implementation/review perspective. The two nonblocking observations are
  the Task 2 test-strength notes already retained above: some same-pair/writer
  outcome tests do not themselves prove a database wait, and the bounded-query
  assertion proves one target result rather than bounded executor work. Existing
  stronger FK/canonical-order tests prove actual blocking, and static review
  confirms current member/ancestry queries originate from the single target.
- Confirmed all five design priorities: ordered dual-account `FOR UPDATE` before
  later locks; owner-only wide/exact-selected authority with native-validated
  visible-foreign denial; current pin/identity/epoch/grant/revision binding;
  conflict-preserving initial/regrant lifecycle; and exactly one grant mutation
  with complete direct roots, no profile leakage and atomic rollback.
- Independently reran Command 120/120, Data 127/127 and HTTP 105/105 with zero
  failures/skips. Specification validation and base-to-head whitespace passed;
  the tracked tree remained clean at the reviewed head.
- Independently audited all six retained TRX files: 9,039 passed, zero
  failures/skips. Raw coverage recomputed to 98.5% line (15,398/15,627) and
  92.3% branch (6,670/7,220), with all six production assemblies above 90% line.
  All 634 full-run, 93 native-source and 17 native-evidence hashes matched.
  Native Java 173, Python 142, helpers 36, repository helpers 23 and the 322-case
  four-direction corpus passed; locked restore, build, dependency audit and
  OpenAPI drift evidence support the exact reviewed sources.
- The Task 3 correction was independently re-reviewed at
  `6554b45674f8c93336e0016d7b85ab90b8b42704` with Critical 0, Important 0,
  Minor 0. README totals are 36 implemented / 19 remaining, matching 55 business
  rows. No unresolved task-review correction remains.
- Exact-head GitHub/CI delivery is still pending and parent-controlled. Independent
  protocol/security/client approval remains the documented release gate.
