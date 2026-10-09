# Resume the open-issues batch

The batch is paused at the user's request after finishing the current issue. Do not start another issue until the user asks to resume.

## Current state

- Repository: `artur-rios/cerberus-api`; delivery base: `develop`.
- Thirty use cases delivered; 25 remain.
- Last delivery: UC-29 Create collection, issue #30, PR #90.
- Merge commit: `2668b4a605e315307190da350016b9adf46efb9e`.
- Tested delivery commit: `e9c9116a02625b19763d6797534b8ce51a810e8a`.
- Issue #30 is closed, all nine Definition of Done checks are completed, and the project item is Done. The remote feature branch is deleted; the worktree is retained.
- The merged tree equals the tested tree. Required branch-policy, test and docker checks passed on the exact delivery commit.
- Fresh unfiltered verification: 7,005 passed, zero failures or skips; 98.4% line and 91.9% branch coverage. All six production assemblies meet the 90% line gate.
- One whole-branch review completed with no Critical or Important findings. All 27 rulings and two inherited Minor dispositions are archived.

## Where to continue

Next: **UC-30 List collections, issue #31**, which has not started. Preliminary read-only exploration is not an accepted design or implementation plan. No UC30 branch, worktree, production code, tests, design, plan or project transition was created.

On a later explicit resume:

1. Read `open-issues-2026-10-08.json`, `batch-paused-2026-10-09.md`, and `batch-review-decisions-2026-10-08.md` in this directory.
2. Read applicable repository instructions and the batch/workflow skills. Reload issue #31 and its requirements and use-case specification from the current repository; do not reuse preliminary assumptions.
3. Fetch and verify the current `develop` state. Use a separate UC30 worktree/branch, preserving the primary workspace and earlier worktrees.
4. Follow the authorized per-issue design, test-first implementation, full verification, ordinary review, exact-head CI, normal merge and nine-check closeout process. Do not repeat UC29 implementation or review.
5. Update the existing checkpoint and aggregate review dispositions as each issue finishes.

The earlier user authorization to implement all open issues, push feature branches, open and normally merge verified PRs into develop, close issues, update project status and delete delivered remote branches remains recorded. The pause remains active until the user resumes. Do not request the same authorization again.

The user deferred **independent security and client-operability qualification for development only**. Ordinary review, native protocol suites, dependency audits, full tests, coverage and required CI remain mandatory. Main/tag/release approval gates remain in effect. Do not use admin merges, force pushes, self-approval or disabled tests.

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
