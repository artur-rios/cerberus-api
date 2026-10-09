> Superseded after user resume on 2026-10-08. Read [the current completed-implementation checkpoint](../protocol-harness-2026-10-08-complete/RESUME.md). The paused state below is historical; do not repeat Tasks 10–11.

# Paused protocol harness — 2026-10-08 UTC

The user explicitly asked: “Pause, and save the current state so I can continue exactly where we stopped.” Stay paused until the user resumes. Do not finish a task, commit, push, merge or run additional implementation checks while paused.

## Exact stopping point

Worktree: `/tmp/cerberus-foundation-worktree`
Branch: `feature/protocol-harness-design`
HEAD: `bb0483e21ebb87f17ca89d5b5e0770f414dc7097`
Plan merge base: `7f99403`

Tasks 1–9 are complete in the ledger. Task 10 is implemented, tested and staged, but **not committed or finalized with task-done**. Task 11 has not started. No fresh whole-branch reviewer has been dispatched. No new commit or implementation was performed while saving this checkpoint.

Exactly seven staged files, no unstaged worktree changes:

- docs/security/interoperability-vectors.json
- docs/security/protocol-harness-benchmarks.json
- tools/protocol-harness/README.md
- tools/protocol-harness/benchmark.py
- tools/protocol-harness/java/src/main/java/cerberus/protocol/Main.java
- tools/protocol-harness/python/cerberus_protocol/__main__.py
- tools/protocol-harness/tests/test_benchmark.py

Staged diff: 3,676 insertions, 3,409 deletions. Most lines are a genuinely regenerated randomized corpus after CLI source hashes changed. Staged whitespace check already passed.

The user's primary workspace `/root/repositories/cerberus-api` remains on `feature/changelog-and-contributing` with preexisting modifications to CONTRIBUTING.md, README.md, docs/requirements/Operations & Infrastructure Document.md, and untracked CHANGELOG.md. Preserve these; they are not protocol harness work.

## Authority and saved records

The user previously authorized the inline implementation plan. Continue autonomously on resume; do not request task authorization again. A subsequent permission complaint concerned sandbox approval, not withdrawal of authorization. The active sandbox mounts `.git` read-only: actual staging previously failed with “Read-only file system”. Git mutations therefore required sandbox escalation; some prefixes are already approved. Respect actual sandbox enforcement rather than trying alternative tools to bypass it.

Read the worktree's own instructions and these authoritative files:

- docs/superpowers/plans/2026-10-07-protocol-harness.md
- docs/superpowers/specs/2026-10-07-protocol-harness-design.md
- .superpowers/sdd/2026-10-07-protocol-harness/progress.md
- .superpowers/sdd/2026-10-07-protocol-harness/task-10-brief.md

Trust completed ledger tasks and Git history; do not redo Tasks 1–9. Implementation uses the executing-plans skill at `/root/.codex/plugins/cache/openai-curated-remote/superpowers/6.4.2/skills/executing-plans/SKILL.md`. Its task-start/task-done scripts save briefs and completion evidence. One fresh whole-branch reviewer is required at the end; the skill explicitly authorizes that delegation. No per-task implementation agents. Relevant skills already applied include TDD, systematic-debugging, verification-before-completion, requesting-code-review and using-git-worktrees. Keep the full ledger and its rulings until finishing.

## Observed verification

- Java native suite: 160 passed, no failures/errors/skips.
- Python native suite: 140 passed, no failures/errors/skips.
- Harness helper suite: 25 passed (including four benchmark tests).
- Cross-language corpus: 309 cases per producer, all four producer/consumer combinations passed. Each language passes six primitive known-answer families. Corpus binds 43 source/input/dependency hashes.
- Refreshed corpus replay and Task 9 task-done succeeded. Session 60914 was polled during checkpoint saving and exited 0; its tail confirms 25 helper tests and the Task 9 completion ledger line. No active execution session is intentionally left running.
- Actual benchmark: ten samples after one warm-up in each fresh process. Java median 165.0054735 ms, p95/max 185.183398 ms, peak RSS 394510336 bytes. Python median 91.4079585 ms, p95/max 124.200279 ms, peak RSS 97189888 bytes. Peak includes runtime/JIT baseline; no mobile, SLO or performance acceptance claim. Artifact includes actual environment/profile/UTC metadata.
- Repository helpers most recently 23/23 and specifications passed. Latest full production .NET run is older, at Task 4: 124/124, zero skips. Do not claim these were freshly rerun at Task 10.
- Last live dependency audit before this resumed turn: clean at 2026-10-08 00:46 UTC, 153 package versions. Final acceptance still needs a fresh live audit.

Task 9 originally had a task-done run that replayed the corpus successfully but discovered Task 10 tests before benchmark implementation existed, causing four expected missing-feature failures. It wrote no completion line. This failure is candidly preserved in the ledger, followed by the successful rerun; do not rewrite history.

## Qualified local environment

Preserve these paths; do not replace the adopted dependency graph with an older failed cache:

- Python: `/tmp/cerberus-protocol-deps.Xbxa52/venv/bin/python` (3.14.4)
- MAVEN_USER_HOME: `/tmp/cerberus-protocol-deps.Xbxa52/maven-user`
- Maven repository: `/tmp/cerberus-protocol-m2.io36vz`
- Wheels: `/tmp/cerberus-protocol-deps.Xbxa52/wheels`
- Maven ZIP: `/tmp/cerberus-protocol-deps.Xbxa52/maven.zip`

From the worktree, native Java uses:

```bash
MAVEN_USER_HOME=/tmp/cerberus-protocol-deps.Xbxa52/maven-user tools/protocol-harness/java/mvnw -o -Dmaven.repo.local=/tmp/cerberus-protocol-m2.io36vz -f tools/protocol-harness/java/pom.xml test
```

The canonical replay command runs both native suites itself:

```bash
MAVEN_USER_HOME=/tmp/cerberus-protocol-deps.Xbxa52/maven-user HARNESS_MAVEN_CACHE=/tmp/cerberus-protocol-m2.io36vz PYTHONPATH=tools/protocol-harness/python /tmp/cerberus-protocol-deps.Xbxa52/venv/bin/python tools/protocol-harness/verify.py --check docs/security/interoperability-vectors.json
python3 -m unittest discover -s tools/protocol-harness/tests -v
```

Export MAVEN_USER_HOME to subprocesses: omitting it previously selected an unrelated wrapper cache and correctly failed. The lock contains 94 Maven artifacts and four Python artifacts, plus audited embedded Maven dependencies. Audit accepts --maven-cache, --wheels and --maven-distribution arguments; use qualified Python so installed-version checks reflect the actual environment.

## Exact next actions on resume

1. Verify branch/HEAD/staging against snapshot.json and read the Task 10 brief and ledger. Append a ruling for including the refreshed corpus in Task 10: changing CLI source required genuinely regenerated/replayed evidence, not masking drift. Record the 1..1000 sample bound if needed.
2. Commit the seven already-staged files with `test: measure isolated password derivation` using the necessary Git sandbox escalation. Do not stage primary-workspace documentation edits.
3. Run Task 10 task-done with BASE `bb0483e`, canonical replay plus helper suite. The actual ten-sample benchmark already passed; the final contract should use the brief's necessary checks. Do not start Task 11 tests until task-done discovery has finished.
4. Run task-start for Task 11, read its exact brief, then RED→GREEN delivery tests, CI integration and review handoff. Intended commit: `chore: integrate protocol review evidence`.
5. Run complete acceptance commands from the plan with actual counts; then one fresh whole-branch review and a TDD fix pass for Important/Critical findings. Use the skill's review-package script with base 7f99403 and HEAD, code-reviewer.md, plan/spec, verbatim Review Focus and ledger rulings. Skill requires an explicitly selected most capable reviewer; gpt-6-astra is available. No second reviewer dispatch.
6. Follow repository PR/CI workflow and finishing skill within existing authorization. Never waive the pending independent security/client review or mark business use cases complete from harness success.

## Task 11 constraints and useful research

Files: new tools/protocol-harness/tests/test_delivery.py; modify harness README, .github/workflows/tests.yml, docs/security/protocol-review.md. Do not modify approval manifest, endpoint inventory or product requirements. Integrate JDK25/Python3.14, exact locked dependency installation, graph/hash audit, full native suites and `--check docs/security/interoperability-vectors.json` into the existing required `test` job. Preserve its name, existing protocol gate condition, .NET coverage and Docker job. Never regenerate evidence in CI or use benchmarks as acceptance.

New action tags were verified against official GitHub release pages/API:

- actions/setup-java v6.0.1: de7274f081f381c8f8158605e0321c36c376e2e6
- actions/setup-python v7.0.0: 5fda3b95a4ea91299a34e894583c3862153e4b97

Use isolated empty CI Maven/cache/wheel directories because audit rejects extra as well as missing artifacts. The approved graph is resolved by clean/test/dependency:tree; no new Maven plugin/packaging dependency. Install/download Python wheels with --require-hashes and only-binary, then install offline from that directory. Download/checksum the locked Maven ZIP and expose it for audit. Avoid concurrent .NET builds.

The production gate currently runs on main, PRs targeting main, tags and feature/uc-* branches; preserve its exact condition. `scripts/verify_protocol.py` must still exit 1 and print `BLOCKED: Protocol security and client review is pending.` This is expected release/dependent-UC blocking, not a harness failure. Preserve pending manifest; never manufacture reviewer/date/approval hash. Test parsed CI obligations, project-reference and Docker isolation, and actual pending/missing/stale gate behavior. Review doc must distinguish reference-model/public-fixture evidence from client/platform tests and independent review; include actual design changes (GUID registration binding, proof headers/raw-body hash, HPKE author signature, independent recovery key, no-expiry policy, restart tradeoff).

## Known review areas, not yet adjudicated findings

Whole-branch Review Focus covers exact numeric bounds before expensive work, malformed EC/PKCS8/role inputs and redaction, raw-body/signature ambiguity and idempotency, lease policy/clock overflow/regression/restart, and false-green orchestration. Potential areas for the reviewer: verify.py buffers child stdout before post-hoc size checks and timeout descendants; direct Java classpath checks existence while separate audit establishes hashes; corpus/native negative coverage boundaries; runtime version metadata in evidence; fixture Number.longValue coercion. Do not call these fixed or accepted without review/tests.

## Backup files

This checkpoint directory includes a branch Git bundle, working-tree source/ledger archive, exact staged and unstaged patches, status metadata and SHA-256 checksums. No private keys or credentials are intentionally added; protocol fixtures contain public test material only. Dependency caches/build outputs are not archived; preserve their live paths above. Existing worktree/index remain intact. On resume prefer the live worktree; backups are for recovery, not instructions to reset or overwrite user changes.
