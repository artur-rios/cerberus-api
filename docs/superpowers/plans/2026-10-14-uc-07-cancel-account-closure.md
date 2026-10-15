# UC07 Cancel account closure implementation plan

**Scope:** issue8; UC07 main and AF01–06; FR-AC-14, BR24; dedicated fresh
identity/pending closure authorization. Existing unattended batch authorization
covers design, implementation, testing, own PR/develop merge, status/issue closure
and delivered branch cleanup. Security/client release approval remains separate.

## Design

- Start `feature/uc-07-cancel-account-closure` from verified clean synchronized
  develop5d05c722f301041f3b0e38d9145b266f135ca0c2 (UC06 PR109).
- Ordinary authentication still rejects inactive accounts. Add explicit restricted
  `/api/auth/closure/login` and `/api/auth/closure/2fa/verify` entry points that
  delegate credentials/MFA to Heimdall and return its existing human token plus
  minimal unexpired own pending-closure metadata. Challenges return no account.
  Validate token signature/scope/human actor and single fresh iat, then recheck
  pending state/deadline with the database clock. Never issue vault access.
- POST `/api/accounts/me/closure/cancel` accepts exactly required positive safe
  `expectedRevision`, derives current actor from Heimdall identity, requires single
  signed iat less than60 seconds old and no future issue time. No vault handle/key
  is required. Strict bounded UTF-8 JSON/query/media/header and no-store contracts
  follow closure's established transport.
- Capture connected account roots and acquire canonical roots then affected
  collections before work/child rows. Recheck ownership, typed terminal evidence,
  original fixed30-day pending times, safe counters, graph and reduced authority.
  A coherent pending account has revoked sessions/grants/software provisioning
  and consumed unlock challenges; reject corrupt state rather than reviving it.
- One guarded database-time account/work CAS restores Active, advances account
  revision/revocation generation, clears closure times and completes/cancels due
  purge work while clearing claims. Preserve encrypted resources/protection,
  reduced grants/sessions/challenges and unrelated accounts. Validate exact final
  owner/work and retained collateral before commit. Permit only natural expiry
  after successful authorization, not before lock waits/CAS.
- Valid pending deadline is strictly greater than current database authorization
  time. Expired own closure returns410; terminal/absent account404; Active or
  revision/counter conflict409; malformed retained metadata/storage fault503.
  Concurrent cancellation has one winner; lost-response retry reloads through
  ordinary freshly authenticated metadata and does not revive old authority.
- Existing lifecycle snapshot SQL can be factored into a shared internal helper
  with explicit reduction versus preservation normalization. UC06's behavior is
  unchanged and its existing meaningful regression tests run with the full suite.

## Test-first flow mapping

- [x] Domain/Command RED/GREEN: strict revision, fresh identity, minimal output,
  failure whitelist/dependency/cancellation and restricted auth/MFA behavior.
- [x] PostgreSQL RED/GREEN: predeadline cancellation, fixed work completion,
  retained ciphertext, no old access revival, reclosure scheduling, current
  closure graph/state/counters, absent/typed-erased/expired/corrupt evidence.
- [x] Real races: concurrent cancellation/closure/purge fencing, account/collection/
  work lock waits crossing deadline/freshness, partial suppression/faults/caller
  cancellation and exact collateral rollback before clean retry.
- [x] Missing HTTP endpoints RED then GREEN; strict input/media/query/body bounds,
  current Heimdall identity/dependency, default inactive login rejection, password/
  MFA restricted recovery, expired/active/foreign/terminal denial, old session and
  provisioning denial after cancellation, fresh new unlock/regrant only.
- [x] AF01 invalid400; AF02 absent/hidden404; AF03 auth/freshness401/known403;
  AF04 revision/state409; AF05 safe503 and rollback; AF06 deadline410/erased404.
- [x] API/operations/changelog/OpenAPI docs and actual HTTP contract validation;
  fresh whole-change read-only review, resolve all material findings.
- [x] Full unfiltered Release, cleared fresh>=90production line coverage,
  zero-warning build/lockedrestore/audits/helpers/spec/native corpus/serverleases/
  OpenAPI/Docker. README only after local verification.
- [ ] Own PR closes8, exact-head branch-policy/test/dockerSUCCESS, normal merge,
  Closed/projectDone/merged source-tree proof/branchcleanup/hashverified archive/
  clean synchronized develop, then UC08/50–55.

Verification 2026-10-15: full unfiltered Release11340 passed/0failed/0skipped;
fresh production coverage98.8%line/89.6%branch. All auxiliary gates passed.
