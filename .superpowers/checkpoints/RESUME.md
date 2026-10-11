# UC-30 delivery in progress

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
- Design/plan committed at 1d01abd; implementation committed at2c4bca6 under SDD for its one
  coupled read operation. Query101/Data91/HTTP71 focused tests pass; full
  verification/review/CI are pending. Do not infer completion from these focused runs.
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

## Workspace and evidence

The primary workspace remains on the user's `feature/changelog-and-contributing` branch. After pausing, the user explicitly requested a commit of `CONTRIBUTING.md`, `README.md`, `docs/requirements/Operations & Infrastructure Document.md`, `CHANGELOG.md`, and the saved `.superpowers/` state, followed by a PR and merge after green CI. The original local versions are preserved in commit `04db423538c3dcaea869ce8680a7d5295115c756`. This branch was then updated from current `develop` to retain all delivered code, backlog status, changelog entries and development-only review deferral. The README status now records 30 delivered use cases and 25 remaining. Original documentation SHA256 values are historical; `primaryWorkspaceAfterDocsIntegrationSha256` in the active JSON checkpoint records the reconciled versions. Check Git status on resume rather than assuming the earlier uncommitted state still applies. Preserve the current contents and user's branch; do not reset or switch the primary workspace as part of batch work.

UC29 retained worktree: `/tmp/cerberus-uc29-worktree`, local branch `feature/uc-29-create-collection`. Its owned SDD directory was removed only after archiving and SHA256 verification. Do not remove sibling worktrees, SDD directories or unrelated caches.

Durable evidence: `uc-29-2026-10-09-complete/` in this directory, with 112 SHA256-verified files, all six coverage XML inputs, coverage summary, full review, ledger, task evidence, remote delivery proof and paused snapshot. Verify `sha256-manifest.json` before relying on the archive. The active checkpoint and this resume note may be newer than the archived snapshot; prefer the active checkpoint for current batch state.

Historical UC14 and UC15 checkpoint statuses use `complete`; count these as delivered alongside `done`.
