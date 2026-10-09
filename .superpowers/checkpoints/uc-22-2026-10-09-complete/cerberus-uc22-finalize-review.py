import json
from pathlib import Path
w=Path('/tmp/cerberus-uc22-worktree')
p=Path('/root/repositories/cerberus-api/.superpowers/checkpoints')
ledger=w/'.superpowers/sdd/2026-10-09-uc-22-permanently-delete-record/progress.md'
decisions='''
UC22 final ordinary review: independently regraded all three findings by actual client/operator effect; no Critical/Important findings for the authorized develop integration. Permission failure classification remains Minor: the caller receives a safe failure rather than false success, committed intent/work remains durable, reads remain denied and worker recovery continues. The cost is misleading outage classification, not lost erasure or widened authority. No blocking fix pass or second review.
Final: Ruling: Independent formal security approval remains development-only deferred — the user explicitly deferred this approval while current ownership and authorization received ordinary review and tests — cost if wrong: latent security defects await mandatory release qualification.
Final: Ruling: Independent formal protocol and cryptographic qualification remains development-only deferred — existing native binding and runtime rejection were reviewed, but that does not establish formal qualification — cost if wrong: protocol defects must be discovered and resolved before release.
Final: Ruling: External-client interoperability, authenticated decryption and operability remain release prerequisites — server/native fixtures cannot establish real-client approval and the user explicitly deferred it for development — cost if wrong: clients may remain unable to operate until qualification and any required provisioning are complete.
Final: Ruling: Production deployment, production restore drills and release qualification are outside this develop integration — main/tag/release gates remain mandatory, and no production deployment or customer deletion is performed — cost if wrong: operational restore or release defects remain unqualified until those gates run.
Final: Ruling: Durable ordered terminal synchronization remains later integration before release — current reads deny the terminal record immediately, but recordId/deletedAt is not an ordered synchronization event — cost if wrong: offline clients can retain stale local visibility until terminal synchronization is implemented and qualified.
Final: Ruling: Physical purge/replay for account, profile, folder, collection and grant plus general trash expiry/restore remain their later use cases — current typed fail-closed visibility and exact record purge preserve other resource kinds — cost if wrong: other kinds retain restored ciphertext until their own typed physical handlers are implemented and tested; selected ProfileId must never be cleared to widen access.
Final: Ruling: Implicit recipient-account FK and deadlock audit is mandatory BEFORE UC35 grant writers — current implemented writer interactions were reviewed and exercised, while nonexistent writers cannot be certified — cost if wrong: future sharing operations may deadlock if the prerequisite or established lock order is ignored.
Final: Ruling: The inherited UC21 trailing-space blank remains its existing deferred formatting finding — this branch does not change that line and its entire new range passes whitespace verification — cost if wrong: formatting debt remains visible until authorized cleanup, with no current runtime effect.
Final: Ruling: The executor completes remote exact-head CI and all delivery gates before declaring UC22 done — the read-only reviewer could not certify future remote actions; normal merge, nine DoD, issue/project state, branch deletion and tested tree equality will be verified — cost if wrong: claiming completion before those gates would leave unintegrated or unverified work.
Final: minor (deferred): Ledger UnauthorizedAccessException escapes to 500/internal_failure instead of documented dependency 503/persistence_unavailable; terminal intent/outbox survives, reads stay denied and recovery retries. Correct the permission-error mapping with a deterministic regression before release; current cost is wrong outage classification without false erasure success.
Final: minor (deferred): Shared OpenAPI omits required vault-access header, request body and recordId/deletedAt output requiredness; runtime rejects incomplete requests and enforces exact success output. Correct the shared generator before client/release qualification to avoid incomplete generated requests and unnecessarily optional models.
Final: minor (deferred): Shared OpenAPI advertises ProblemDetails content for an actually empty 413; runtime rejection is correct, but generated clients may attempt to deserialize an absent body. Correct shared response metadata before client/release qualification.
'''
l=ledger.read_text(); assert 'UC22 final ordinary review:' not in l
ledger.write_text(l+decisions)
a=p/'batch-review-decisions-2026-10-08.md';a.write_text(a.read_text()+'\n'+decisions)
l=ledger.read_text();rulings=[x for x in l.splitlines() if 'Ruling:' in x];minors=[x for x in l.splitlines() if 'minor (deferred):' in x]
assert len(rulings)==26 and len(minors)==3,(len(rulings),len(minors))
validation='''- Fresh unfiltered full suite: **4,596 passed, zero failed/skipped** across Domain 313, Shared 125, Command 783, Query 259, Data 1,634 and WebApi 1,482.
- Coverage: **98.4% line / 91.7% branch**, with all six production assemblies at least 90% line coverage.
- New focused coverage: 47 typed-identity Data tests, 141 permanent-deletion Data tests, 66 Command tests and 107 actual native HTTP tests. Missing APIs/routes and real fencing/history/clock/time defects were observed RED before correction, then GREEN.
- Locked restore, NuGet direct/transitive audit, 153 native OSV entries, 322 native corpus cases in four directions, 36 native helpers, 23 Python helpers, requirements and OpenAPI write/drift/shape checks passed.
- Complete branch range whitespace check passed, including every added file. The inherited UC21 formatting minor remains unchanged.
- Single fresh whole-branch review found no blocking defects after grading actual effects; all three Minors are retained below. Exact-head branch-policy/test/docker CI remains mandatory before merge.'''
body=Path('/tmp/cerberus-uc22-pr-body-draft.md').read_text().replace('{{VALIDATION}}',validation).replace('{{RULINGS}}','\n\n'.join('- '+x for x in rulings)).replace('{{MINORS}}','\n\n'.join('- '+x for x in minors))
assert '{{' not in body;Path('/tmp/cerberus-uc22-pr-body.md').write_text(body)
Path('/tmp/cerberus-uc22-tested-head.txt').write_text('cc2b8bb1983950ec45e8f2c7e0252d9e88e2dd88\n')
c=p/'open-issues-2026-10-08.json';d=json.loads(c.read_text())
v='UC22 review complete; all 26 rulings and 3 deferred minors recorded. No blocking fix pass or second review. Tested clean HEAD cc2b8bb; 4596 PASS, 98.4% line / 91.7% branch. Next push/PR for issue23, Testing, exact-head three CI checks, fresh base/rules, normal squash merge, nine DoD, Done, branch deletion and archive; then UC23. Independent formal/client qualification remains development-only deferred.'
for k in ('currentTask','resumeInstruction','requiredToResume'):d[k]=v
next(x for x in d['useCases'] if x['id']=='UC-22').update({'currentTask':'Review complete; remote delivery next','reviewVerdict':'ready_for_development_CI','rulingCount':26,'deferredMinorCount':3,'rangeDiffCheckExitCode':0})
c.write_text(json.dumps(d,indent=2)+'\n')
print('Recorded 26 rulings, all 3 deferred minors and all 9 declined-review dispositions; PR body ready.')
