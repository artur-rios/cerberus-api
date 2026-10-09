from pathlib import Path
import json, subprocess

w=Path('/tmp/cerberus-uc23-worktree')
p=Path('/root/repositories/cerberus-api/.superpowers/checkpoints')
ledger=w/'.superpowers/sdd/2026-10-09-uc-23-create-folder/progress.md'
report=Path('/tmp/cerberus-uc23-final-review.md').read_text()
assert 'None found for current development behavior.' in report
assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=w,text=True).strip()=='3bf6b37c211447cfc74ca277defa75910bcae7ec'
assert not subprocess.check_output(['git','status','--porcelain'],cwd=w,text=True).strip()
decisions='''
UC23 final ordinary review: ONE fresh gpt-6-astra reviewer, base d72d920..3bf6b37, report /tmp/cerberus-uc23-final-review.md. No Critical/Important findings after checking actual client/operator effects against source; four Minors deferred. No blocking fix pass, no second review and no product/test modifications after the tested HEAD.
Final: Ruling: Independent formal security audit remains development-only deferred — explicit owner instruction permits this develop batch while ordinary current ownership and safe handling were reviewed — cost if wrong: latent security defects require the mandatory independent release gate before production.
Final: Ruling: Independent protocol/cryptographic approval remains development-only deferred — native proof integration and ordinary corpus evidence are not independent certification — cost if wrong: cryptographic or protocol defects remain possible until release qualification.
Final: Ruling: Real external-client key provisioning, encryption/decryption and usability remain release prerequisites — fixtures exercise native proof/wrapper helpers and opaque envelope bytes, not a full client — cost if wrong: client flows may fail even though current server admission tests pass.
Final: Ruling: Ordered durable offline inherited visibility/synchronization remains later integration before release — direct metadata changes are implemented but do not establish a synchronization protocol — cost if wrong: offline clients can retain stale local visibility until that integration is implemented and qualified.
Final: Ruling: Future folder list/get/update/trash/restore/move/purge writers require their own implementation and lock-interaction gates — creation cannot certify currently nonexistent writers — cost if wrong: future operations may violate scope, lifecycle or lock ordering if those gates are skipped.
Final: Ruling: Other resource physical-erasure paths remain their later use cases — current creation does not implement new physical handlers — cost if wrong: those resources retain ciphertext until their own purge/replay gates are completed; selected ProfileId must never be cleared to widen access.
Final: Ruling: Implicit recipient-account foreign-key/deadlock audit remains mandatory BEFORE UC35 — current creation locks only owned resources and cannot certify future sharing writers — cost if wrong: sharing operations can deadlock against older account FOR UPDATE writers.
Final: Ruling: Inherited UC22 permission-outage 500 classification remains its deferred release Minor — unchanged deletion handling is outside this creation diff, with terminal intent/recovery preserved — cost if wrong: operators receive misleading outage classification until its mapping and deterministic regression are corrected.
Final: Ruling: Inherited UC21 formatting blank remains its recorded formatting Minor — this branch leaves that line unchanged and its entire new range passes whitespace checks — cost if wrong: formatting debt persists with no current UC23 runtime effect.
Final: Ruling: Executor verifies prior merged baseline and new exact-head CI, mergeability, normal merge, issue/project closure and all nine DoD conditions — remote actions were prohibited for the read-only reviewer; actual previous evidence was preserved and new remote gates remain mandatory — cost if wrong: premature completion could leave work unintegrated or unverified.
Final: Ruling: Develop integration does not authorize deployment/main/tag/release or certify production — user authorized the development batch and retained release qualifications — cost if wrong: deploying without those gates can expose unqualified behavior.
Final: Ruling: Production-scale deep-ancestry latency/load qualification remains before release — finite sequential traversal detects cycles but no workload/latency target or load evidence was supplied for creation — cost if wrong: very deep hierarchies can exceed an eventual service latency target.
Final: Ruling: Unused ancestor/profile/collection ciphertext and client times/counters remain opaque and separately repairable — additive creation parses only required structural metadata under initial ruling 6 — cost if wrong: independent corruption persists until the corresponding read/edit/rotation or repair flow addresses it.
Final: Ruling: Broader selected-profile attachment and foreign-content administration remain separately authorized product operations — selected creation links exactly its selection and never transfers foreign ownership under initial rulings 2/3 — cost if wrong: broader client workflows need later association/administration features rather than this endpoint.
Final: Ruling: Shared OpenAPI generation repair remains deferred before client/release qualification — runtime requiredness and empty-413 behavior are enforced, and current generated-client effects are explicitly graded as Minors — cost if wrong: generated clients can send incomplete/null requests or mishandle output/error bodies until metadata is corrected.
Final: minor (deferred): Data and HTTP RO/RW foreign-grant fixtures omit CollectionFolder membership, proving foreign-parent rejection with an unrelated grant rather than a genuinely granted folder. Owner-constrained parent queries still reject all foreign folders; add actual membership and fixture visibility assertions before release. Cost: reduced regression confidence for foreign folders visible through grants; no observed current ownership bypass.
Final: minor (deferred): afterParent failure interceptor fires after UPDATE cerberus.profile, before the immediate folder UPDATE. This proves linked-profile rollback, not post-folder-mutation rollback; current folder update still shares the transaction before commit. Add precisely named profile and actual folder-update fault cases before release. Cost: narrower rollback regression confidence, with no observed partial-commit path.
Final: minor (deferred): Shared OpenAPI lacks required access-header/body/output metadata and advertises nullable profileIds despite runtime rejecting null. Correct shared generation/nullability and regenerate before client/release qualification. Cost: generated clients can produce avoidable 400/401 requests or unnecessarily optional success models; runtime enforcement remains correct.
Final: minor (deferred): Shared OpenAPI advertises ProblemDetails content for the actually empty 413. Correct shared response metadata before client/release qualification. Cost: generated error parsers may try to deserialize an absent body; runtime bounded-body rejection remains correct.
'''
l=ledger.read_text(); assert 'UC23 final ordinary review:' not in l
ledger.write_text(l+decisions)
a=p/'batch-review-decisions-2026-10-08.md';a.write_text(a.read_text()+'\n'+decisions)
l=ledger.read_text();rulings=[x for x in l.splitlines() if 'Ruling:' in x];minors=[x for x in l.splitlines() if 'minor (deferred):' in x]
assert len(rulings)==24 and len(minors)==4,(len(rulings),len(minors))
validation='''- Fresh unfiltered suite: **4,919 passed, zero failed/skipped** across Domain 361, Shared 125, Query 259, Data 1,740, Command 841 and WebApi 1,593.
- Coverage: **98.4% line / 91.7% branch**; all six production assemblies at least 90% line coverage.
- New focused tests: 48 Domain, 106 Data, 58 Command and 111 actual HTTP passes. Missing types/routes were observed RED before implementation; the earlier zero-match HTTP installation attempt is excluded from RED evidence.
- Locked restore, direct/transitive NuGet audit, native OSV 153 packages/no findings, native corpus 322 cases in four directions, 36 native helpers, 23 Python helpers, requirements and OpenAPI write/drift/shape checks passed.
- Complete branch-range whitespace check passed, including added files.
- One fresh whole-branch review: no blockers; all four Minors retained below. The foreign-grant fixtures omit collection membership and the afterParent interceptor fires after profile mutation, so neither is claimed as stronger evidence.
- Exact-head branch-policy/test/docker CI is mandatory before normal develop merge.'''
body=Path('/tmp/cerberus-uc23-pr-body-draft.md').read_text().replace('{{VALIDATION}}',validation).replace('{{RULINGS}}','\n\n'.join('- '+x for x in rulings)).replace('{{MINORS}}','\n\n'.join('- '+x for x in minors))
assert '{{' not in body;Path('/tmp/cerberus-uc23-pr-body.md').write_text(body)
c=p/'open-issues-2026-10-08.json';d=json.loads(c.read_text())
v='UC23 ONE review complete; no blockers, all24 rulings (9initial+15declined dispositions) and4Minors recorded exhaustively. Tested clean HEAD3bf6b37,4919PASS98.4line91.7branch. No fix pass/rereview. Next normal push/PRissue24Testing/exactHEADthreeCI/freshbased72d920+rules/MERGEABLE+CLEAN/normalmatchheadsquash/nineDoD/Done/remoteabsent/testedtree/primarySHA/archive; then UC24issue25. Independent formal/client checks remain development-only deferred.'
for k in ('currentTask','resumeInstruction','requiredToResume'):d[k]=v
next(x for x in d['useCases'] if x['id']=='UC-23').update({'currentTask':'Review complete; remote delivery next','reviewVerdict':'ready_for_development_CI','rulingCount':24,'deferredMinorCount':4,'rangeDiffCheckExitCode':0})
c.write_text(json.dumps(d,indent=2)+'\n')
print('Recorded all24 rulings,4 deferred minors and15 declined-review dispositions; complete PR body ready.')
