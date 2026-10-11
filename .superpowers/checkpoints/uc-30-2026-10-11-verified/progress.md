# SDD ledger — plan: docs/superpowers/plans/2026-10-11-uc-30-list-collections.md

Scope: UC30/#31 only, resumed authorized unattended workflow. Merge only after green full verification and exact-head required CI; no bypasses.

Primary cleanup: 33 exact feature tips verified against merged PR heads and merge commits reachable from origin/develop; deleted local refs, retained clean detached worktrees. Remote feature refs already absent. Local develop fast-forwarded to ef869fd.

Pre-flight:
| Task | Contracts/tests/files consistency | Result |
| --- | --- | --- |
| 1 | Domain/query/store/HTTP share CollectionListRequest/Row/Page; output excludes KeyEpoch outside envelope; tests pin authority/cursor/projection/transport and all AFs | Consistent with spec; one task owns the shared interfaces |

Design self-check: FR-CL-02 exists; all AF01–04 mapped; no schema/dependency/business-rule changes; release deferral boundaries carried; test-first sequence explicit.

Baseline: Unit 2104 passed, zero failures/skips. Exploratory full baseline deliberately cancelled before any code changes to avoid duplicate long functional runs; not claimed as passing. Final fresh unfiltered suite and coverage mandatory.
Task1: started; BASE 1d01abd; implementer /root/uc30_implementation; task owns full list flow.
Ancillary verification: native protocol corpus322×4 pass; locked protocol153-package audit no findings; .NET vulnerability check exit0; Python helpers and protocol orchestration tests pass. Evidence /tmp/cerberus-uc30-{helpers,protocol-helpers,native,audit,dotnet-audit}.log.

Ruling: Commit after all three focused layers pass so read-only task review can overlap the long full coverage run — full fresh unfiltered verification remains mandatory before PR/delivery; avoids duplicate waiting — cost if wrong: an intermediate feature commit can require fixes, and any source change invalidates the full run.

Task1 stable implementation: 2c4bca6; Query101/Store91/HTTP71 =263 focused tests pass; transport and all AF01–04 mapped in report. Full coverage unfiltered running; tested-source manifest484 entries captured. Task review /root/uc30_task_review against1d01abd..2c4bca6 started concurrently, full verification required before task completion/PR. Project moved to Testing under authorized automated Gate2.

Task1 task-scoped review: /root/uc30_task_review approved spec and quality; no Critical/Important findings. Pending fresh suite/CI explicitly retained as controller gates.
Task1: minor (deferred): compressed added test formatting; readable block formatting follow-up.
Task1: minor (deferred): add explicit corrupt native evidence beyond an already-carried highwater; current SQL evidence is from entire visible inventory and nonpage corruption is tested, so no current defect.
Task1: minor (deferred): solution-wide focused filters emitted benign no-matching-test messages in three unrelated assemblies; 263 actual matched tests pass, none skipped. Use project-local focused commands going forward; no rerun solely for noise.
Task review cannot-verify resolution: existing authentication/no-store integration is exercised by new actual HTTP tests; native binding source was inspected by reviewer and independent native322×4 replay passed; independent cryptographic/client release qualification is intentionally outside development authorization. Existing OpenAPI metadata obligation remains tracked, new runtime shape/raw JSON and generated path checked. Whole-branch review and exact-head CI remain mandatory.

Task1: complete for focused task gate (commits1d01abd..2c4bca6; task spec+quality approved, 263 focused passes). Overall Gate3 full-suite/coverage remains pending and no delivery claim is made. Whole-branch review overlaps that pending gate on stable committed source; later closeout-only doc diff will be separately checked.

Final whole-branch review: /root/uc30_final_review; no Critical/Important; current code approved conditional on full verification, exact-head CI and scoped doc closeout. Compressed formatting and carried-boundary evidence scenario remain nonblocking deferred test improvements; benign filter noise informational. Stale checkpoint nested pause/preparation text is being reconciled.
Final declined-to-judge dispositions: shared OpenAPI metadata and independent protocol/client key/decryption qualification retain existing release gates; production-scale capacity/SLA lacks approved workload and is not claimed (whole-visible grant evidence remains required security behavior); pending full/category/helpers/native/audits and exact-head CI will be checked by controller; later doc/evidence changes go through scoped followup with this reviewer. No production source fix or duplicate broad review required.

Final local verification: coverage.py exit0; all7268 pass/0fail/0skip (Domain361, Shared125, Query512, Command1174, Data2753, WebApi2343). Production lines98.4%, branches92.1%; all six assemblies >=90% (Command100, Data98.4, Domain99.3, Query100, Shared97.2, WebApi94.9). Tested-source manifest484 entries unchanged. Python helpers23/protocol helpers36, native322×4, protocol153-package/no-findings and .NET vulnerability audits, OpenAPI and specs/diff checks pass.
Full unfiltered local tests/coverage and both code reviews complete. Gate3 category runs remain required in exact-head CI. Remaining controller gates: scoped final documentation/evidence review, exact final-head required CI, ordinary develop merge and nine-check issue/project/branch closeout. README Done is prepared on the feature branch to land with successful delivery. UC31 is outside this request.
