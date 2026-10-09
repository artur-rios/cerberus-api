import json
from pathlib import Path
w=Path('/tmp/cerberus-uc21-worktree')
p=Path('/root/repositories/cerberus-api/.superpowers/checkpoints')
ledger=w/'.superpowers/sdd/2026-10-09-uc-21-move-record/progress.md'
decisions='''
UC21 final ordinary review: no Critical/Important runtime findings; ready for develop after exact-head CI/delivery. Regraded by actual effects, not spec silence. No blocking correction pass or re-review.
Verification correction: the earlier blanket diffPASS referred only to an unstaged git diff --check, which omitted the then-untracked HTTP file. Fresh full-range git diff --check a59310d..1c6e9ad exits2 for four spaces on blank RecordMoveHttpTests.cs:143. Full-range whitespace verification is NOT PASS; formatting Minor retained below. No runtime or configured CI gate is affected.
Final: Ruling: Independent formal protocol/cryptographic/security qualification remains development-only deferred — user explicitly deferred that approval; ordinary ownership/native binding/concurrency review and all release gates remain — cost if wrong: latent protocol or security defects await mandatory release qualification.
Final: Ruling: Real-client authenticated decryption, trusted root availability and any additional opaque client provisioning remain mandatory before release qualification — server/native fixtures cannot demonstrate external-client decryption and no usable keys are introduced — cost if wrong: clients may be unable to decrypt moved content until required provisioning is implemented and qualified; Important release limitation.
Final: Ruling: Durable offline inherited-visibility synchronization remains later integration before release — current get/list recomputes ancestry and scope immediately; this operation does not add the separate sync protocol — cost if wrong: offline clients may retain stale visibility until synchronization integration ships; Important release limitation.
Final: Ruling: Typed terminal-GUID repair and same-GUID/different-kind survival verification MUST precede ANY UC22 physical purge, and selected ProfileId must never be nulled — UC21 adds no purge; inherited global exclusions conservatively fail closed — cost if wrong: live cross-kind resources can be falsely hidden or selected access accidentally widened if the prerequisite is ignored; Important integration prerequisite.
Final: Ruling: Future grant-management deadlock audit remains mandatory BEFORE UC35, including implicit recipient-account FK locks — current create/association/edit/trash/rotation interactions were reviewed and tested, but nonexistent writers cannot be certified — cost if wrong: future reciprocal sharing writers may deadlock if the established lock protocol or prerequisite is ignored.
Final: Ruling: Cached test service-token lifetime correction does not certify production renewal behavior — nine real expiry failures justified extending only fixture lifetime while production zero-skew validation remains intact — cost if wrong: renewal defects require dedicated expiry/adapter coverage rather than this move fixture.
Final: minor (deferred): Shared OpenAPI omits mandatory vault-access header, request body and guaranteed four-field output requiredness; runtime rejects omissions safely and actual HTTP tests pass, but generated clients may produce rejected requests or optional response models. Correct shared generator and contract checks before client/release qualification.
Final: minor (deferred): Shared OpenAPI advertises ProblemDetails content for an actually empty413; runtime correctly rejects oversized input but generated clients may attempt invalid deserialization. Correct shared response generator before client/release qualification.
Final: minor (deferred): RecordMoveHttpTests.cs:143 has four trailing spaces on an added blank line; full branch diff --check exits2. No runtime impact; retain for authorized formatting cleanup and report actual verification result, never blanket diffPASS.
'''
l=ledger.read_text();assert 'UC21 final ordinary review:' not in l
ledger.write_text(l+decisions)
a=p/'batch-review-decisions-2026-10-08.md';a.write_text(a.read_text()+'\n'+decisions)
l=ledger.read_text();rulings=[x for x in l.splitlines() if 'Ruling:' in x];minors=[x for x in l.splitlines() if 'minor (deferred):' in x]
assert len(rulings)==20 and len(minors)==3,(len(rulings),len(minors))
body=Path('/tmp/cerberus-uc21-pr-body-draft.md').read_text().replace('returns404','returns 404').replace('deny403','deny 403').replace('deny404','deny 404').replace('after60seconds','after 60 seconds').replace('of26','of 26')
validation='''- Fresh unfiltered full suite: **4,235 passed, zero failed/skipped** across six families (Domain313, Shared125, Query259, Command717, Data1446, WebApi1375).
- Coverage: **98.3% line / 91.7% branch**, all six production assemblies above90% line.
- New focused move coverage:142 Data,76 Command,139 actual native HTTP cases; observed RED before implementation, then GREEN. Nine cached-service-JWT expiry failures observed before test-only fixture correction, then full suite GREEN.
- Locked restore, NuGet direct/transitive vulnerability audit,153 native OSV entries,322 native corpus cases in four directions,36 native helpers,23 Python helpers, requirements verification and OpenAPI write/drift/shape checks passed.
- Unstaged whitespace check passed; **full branch range whitespace check exits2** for one trailing-space blank line. Retained formatting Minor; no claim that the branch range is whitespace-clean.
- Single fresh ordinary whole-branch review: no Critical/Important runtime findings; three Minors retained. Exact-head branch-policy/test/docker CI is still required before merge.'''
body=body.replace('{{VALIDATION}}',validation).replace('{{RULINGS}}','\n\n'.join('- '+x for x in rulings)).replace('{{MINORS}}','\n\n'.join('- '+x for x in minors))
assert '{{' not in body
Path('/tmp/cerberus-uc21-pr-body.md').write_text(body)
c=p/'open-issues-2026-10-08.json';d=json.loads(c.read_text());v='UC21 final ordinary review ready for develop; no blocking findings; all20 Rulings and3 Minors recorded. Full4235PASS98.3line91.7branch unchangedHEAD1c6e9ad. Full-range diffcheck exits2 trailingblank143 (earlier unstaged-only PASS clarified). Next push/PR22/Testing/exacthead3CI/freshbase/normalmerge/nineDoD/archive; UC22 typed terminal repair BEFORE ANY purge.'
for k in ('currentTask','resumeInstruction','requiredToResume'):d[k]=v
next(x for x in d['useCases'] if x['id']=='UC-21').update({'currentTask':'Review complete; remote delivery next','reviewVerdict':'ready_for_development_CI','rulingCount':20,'deferredMinorCount':3,'rangeDiffCheckExitCode':2})
c.write_text(json.dumps(d,indent=2)+'\n')
f=Path('/tmp/cerberus-uc21-complete.py');f.write_text(f.read_text().replace('actualHTTP137','actualHTTP139').replace("'followUp':'sharedgeneratorbeforeRelease'","'followUp':'shared generator before release; formatting blank143 deferred; fullrange diffcheck exit2 explicitly recorded'"))
print('Recorded all20 rulings,3 minors and corrected whitespace evidence; PR body ready.')
