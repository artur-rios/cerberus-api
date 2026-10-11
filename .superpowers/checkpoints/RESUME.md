# UC-30 verified; delivery gates remain

The user resumed the existing unattended authorization on 2026-10-11 for the next
open issue only, after requesting branch merges and cleanup. Do not start UC31/#32
as part of this scope.

## Current state

- Delivery base: develop at ef869fd when UC30 started.
- All existing 33 local feature tips matched already-merged PR heads; their merge
  commits are reachable from origin/develop. Remote feature branches were already
  absent. Local refs deleted; clean linked worktrees retained detached at their tips.
- Primary workspace is now clean develop, matching origin/develop.
- UC30/#31 Testing on feature/uc-30-list-collections in /tmp/cerberus-uc30-worktree.
- Design/plan committed at 1d01abd; implementation committed at 2c4bca6 under SDD for its one
  coupled read operation. Focused263 and fresh unfiltered7268 tests pass with zero
  failures/skips; production line coverage98.4%, branch92.1%, every assembly >=90%.
  Both scoped task and whole-branch reviews found no Critical/Important.
  Separate Unit/Functional category runs remain required in exact-head CI, alongside
  its complete suite/coverage. Normal develop merge and issue/project/branch closeout
  remain delivery gates. README Done is prepared to land with that merge.
- Ledger: /tmp/cerberus-uc30-worktree/.superpowers/sdd/2026-10-11-uc-30-list-collections/progress.md.

## Execution scope

Earlier explicit authorization covers design/implementation/testing transitions,
feature pushes, PR creation and ordinary merge into develop after required checks,
issue/project/README closeout and delivered feature branch deletion. This request
resumes that workflow for UC30 only. No admin merge, force push, self-approval,
disabled tests, production deployment or main/tag/release authorization.

Complete main and all four AFs; fresh full unfiltered six-family tests and coverage,
ordinary code review, native corpus/suites, dependency audits, helper/spec/OpenAPI
checks, exact-head branch-policy/test/docker CI, normal merge and nine-check closeout.
Independent security/client-operability review remains deferred only for development.

## Preserved obligations

Read the aggregate dispositions for the full history. In particular:

- Fix shared generated OpenAPI required header/body/output and wrapper nullability metadata, and the advertised ProblemDetails body for actual empty 413 responses, before external-client release.
- Complete independent security/protocol/client qualification and real usable-key provisioning/decryption validation before release.
- Audit implicit recipient-account foreign-key locking before UC35 public grant creation.
- Preserve kind-plus-GUID terminal identity and cross-kind aliases; selected-profile scope must never widen through null handling.
- Preserve complete native protection-rotation inventories, including retained trash and future grants/wrappers.
- Normalize UTC edit times to microseconds before PostgreSQL binding, including pre-2000 and upper-bound cases.
- UC48 must implement durable inherited visibility and offline removals. Current reads and structural counters do not fulfill that requirement.
- UC50–53 must implement restoration and physical expiry, preserving earlier trash deadlines and revalidating authority without reviving revoked grants. Missing handlers that retry are not completed behavior.

## Historical workspace and evidence

The primary workspace previously used feature/changelog-and-contributing. Its
original documentation and saved-state work was merged by PR91. Original versions
remain in commit 04db423538c3dcaea869ce8680a7d5295115c756. The user explicitly requested
merge/cleanup on 2026-10-11; all 33 local feature tips were verified against merged
PRs before deleting refs, and linked worktree contents were retained detached.
Primary develop was fast-forwarded to origin/develop. Do not treat historical
checkpoint branch names or document hashes as instructions to restore stale work.

UC29 retained worktree /tmp/cerberus-uc29-worktree is detached at its original tip.
Its earlier SDD directory was removed only after archiving and SHA256 verification.
Do not remove sibling worktrees, SDD directories or unrelated caches.

UC29 historical durable evidence remains in uc-29-2026-10-09-complete/, with 112
SHA256-verified files, all six coverage XML inputs, coverage summary, full review,
ledger, task evidence, remote delivery proof and paused snapshot. Prefer active
checkpoint state over archived snapshots when deciding where to resume.

Historical UC14/UC15 checkpoint statuses use complete; count these as delivered
alongside done. UC30 verified local evidence is archived in uc-30-2026-10-11-verified/ with a
SHA256 manifest, six coverage XML inputs, full summary, reviews and tested-source
manifest. Two nonblocking follow-ups remain: added test formatting and explicit
corrupt native evidence beyond a carried highwater.

Before repeating delivery, inspect GitHub issue31/its collection-list PR and the
local delivery receipt at .superpowers/checkpoints/uc-30-2026-10-11-delivery/.
The receipt is written after actual remote merge/closeout; this committed snapshot
truthfully records pre-delivery verification. Remove only this plan's owned SDD
directory after verifying the archive and completing its review; retain worktrees.
After UC30 delivery, stop: UC31/#32 is not authorized by this request.
