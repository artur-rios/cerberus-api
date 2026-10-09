from pathlib import Path
import json, subprocess
w=Path('/tmp/cerberus-uc24-worktree'); p=Path('/root/repositories/cerberus-api/.superpowers/checkpoints')
ledger=w/'.superpowers/sdd/2026-10-09-uc-24-list-folders/progress.md'
report=Path('/tmp/cerberus-uc24-final-review.md').read_text()
assert 'None found in the reviewed UC24 behavior.' in report
assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=w,text=True).strip()=='484d7b80e0c79b114959c507c019e639f64ba291'
assert not subprocess.check_output(['git','status','--porcelain'],cwd=w,text=True).strip()
dispositions=[
('Independent formal security, threat-model and protocol/cryptographic certification remain development-only deferred','explicit user instruction permits this develop batch while ordinary native/ownership/snapshot checks remain required','latent security/protocol defects require independent qualification before production'),
('Real external-client operability and independent client approval remain release prerequisites','no real external client is supplied and the user expressly deferred independent approval for develop','deployed clients may fail despite passing native HTTP fixtures'),
('Client content-key provisioning, folder decryption and offline inherited visibility delivery remain release integration obligations','the endpoint returns opaque ciphertext and cannot certify the complete client flow','clients can lack keys or retain stale visibility until that integration is qualified'),
('Durable ordered synchronization and immutable cross-request content snapshots remain later use cases','this endpoint implements current-permission high-water pagination only','pages can shrink and offline reconciliation remains incomplete until synchronization is implemented'),
('Future folder detail/update/trash/move/restore/purge and writer locking/lifecycle/races require their own gates','this read-only branch cannot certify nonexistent writers','future writers can violate scope or lock ordering if those gates are skipped'),
('Other physical erasure paths and the implicit recipient-account foreign-key audit BEFORE UC35 remain mandatory later gates','current typed-terminal filtering is covered but listing adds neither physical handlers nor sharing writers','resources can retain ciphertext or future sharing writers can deadlock if their gates are skipped'),
('Production workload capacity, latency and availability qualification remain before release','no workload targets or production load evidence exist; bounded-admission EXPLAIN tests prove only their narrower claims','full visible-grant signature work can be material on large inventories'),
('Inherited UC23 narrowed foreign-grant and post-parent-failure fixtures retain their recorded release follow-ups','unchanged UC23 fixtures are outside this diff; UC24 itself installs real folder memberships and proves record-only visibility before negative assertions','UC23 regression confidence stays narrower until its fixture corrections'),
('Inherited UC22 permission-outage 500 classification retains its deferred release Minor','unchanged deletion mapping is outside this diff; current folder-list necessary dependency failures map safely to 503','operators can receive misleading deletion outage classification until that correction'),
('Inherited UC21 formatting blank retains its recorded Minor','unchanged line has no UC24 runtime effect and the complete UC24 range passes whitespace checks','formatting debt remains'),
('Prior baseline reuse and historical CI provenance remain executor-verified rather than independently re-certified','ruling 1 reuses verified UC23; current UC24 unfiltered tests were freshly inspected and reviewers were read-only','incorrect historical/environment assumptions require separate diagnosis'),
('Executor must verify exact-head branch policy/CI, mergeability, normal merge, issue/project closure and merged-tree equivalence; develop does not authorize release/tag/deployment','remote actions were excluded from read-only review and README Done is intended merged state only','premature completion can leave unverified or unintegrated work; deployment before release gates exposes unqualified behavior'),
('Independent OS/native-runtime vulnerability certification remains separate release qualification','ordinary package audit/corpus/helper evidence is useful but not formal operating-system/runtime certification','latent runtime or OS defects can persist until independent qualification'),
]
declined=report.split('### Declined to judge',1)[1].split('### Assessment',1)[0]
assert len([x for x in declined.splitlines() if x.startswith('- ')])==len(dispositions)==13
decisions='\nUC24 final ordinary review: ONE fresh gpt-6-astra reviewer, base fc4bda9..484d7b8, report /tmp/cerberus-uc24-final-review.md. No Critical/Important findings after checking actual effects against source; two Minors deferred. No blocking fix pass, no second review and no product/test modifications after tested HEAD.\n'
decisions+=''.join(f'Final: Ruling: {what} — {why} — cost if wrong: {cost}.\n' for what,why,cost in dispositions)
decisions+='Final: minor (deferred): Shared OpenAPI omits required access-header/output metadata and advertises nullable items despite runtime requiring access and always emitting the five item fields plus nonnull items and nextCursor. Correct shared generation before external-client release qualification. Cost: generated clients can omit access and receive 401 or model successful data as missing/null; runtime authorization and serialization remain correct.\n'
decisions+='Final: minor (deferred): Shared OpenAPI advertises ProblemDetails for the actual empty 413. Correct shared response metadata before external-client release qualification. Cost: generated decoders can try to deserialize an absent body; bounded-body rejection and valid folder listing remain correct.\n'
l=ledger.read_text();assert 'UC24 final ordinary review:' not in l;ledger.write_text(l+decisions)
a=p/'batch-review-decisions-2026-10-08.md';a.write_text(a.read_text()+'\n'+decisions)
l=ledger.read_text();rulings=[x for x in l.splitlines() if 'Ruling:' in x];minors=[x for x in l.splitlines() if 'minor (deferred):' in x];assert len(rulings)==21 and len(minors)==2
validation='''- Fresh unfiltered suite: **5,209 passed, zero failed/skipped** across Domain 361, Shared 125, Query 356, Data 1,844, Command 841 and WebApi 1,682.
- Coverage: **98.4% line / 92% branch**; all six production assemblies at least 90% line coverage.
- New focused Query 97, actual PostgreSQL Data 104 and native HTTP 89 passes. Genuine missing-contract/store RED and actual missing-GET 405 failures precede implementation; an earlier zero-match installation attempt is explicitly excluded from RED evidence.
- Locked restore, direct/transitive NuGet audit, native OSV 153 packages/no findings, native corpus 322 cases in four directions, 36 native helpers, 23 Python helpers, requirements and OpenAPI write/drift/shape checks passed.
- Complete branch-range whitespace check passed, including added files.
- One fresh whole-branch review found no blockers; both concrete OpenAPI Minors are retained below. Every declined-review judgment has a reasoned final ruling.
- Exact-head branch-policy/test/docker CI is mandatory before normal develop merge.'''
body=Path('/tmp/cerberus-uc24-pr-body-draft.md').read_text().replace('{{VALIDATION}}',validation).replace('{{RULINGS}}','\n\n'.join('- '+x for x in rulings)).replace('{{MINORS}}','\n\n'.join('- '+x for x in minors))
assert '{{' not in body;Path('/tmp/cerberus-uc24-pr-body.md').write_text(body)
c=p/'open-issues-2026-10-08.json';d=json.loads(c.read_text());v='UC24 ONE review complete; no blockers, all21 rulings (8initial+13declined dispositions) and2Minors recorded exhaustively. Tested clean HEAD484d7b8,5209PASS98.4line92branch. No fix pass/rereview. Next normal push/PRissue25Testing/exactHEADthreeCI/freshbasefc4bda9+rules/MERGEABLE+CLEAN/normalmatchheadsquash/nineDoD/Done/remoteabsent/testedtree/primarySHA/archive; then UC25issue26. Independent formal/client checks remain development-only deferred.'
for k in ('currentTask','resumeInstruction','requiredToResume'):d[k]=v
next(x for x in d['useCases'] if x['id']=='UC-24').update({'currentTask':'Review complete; remote delivery next','reviewVerdict':'ready_for_development_CI','rulingCount':21,'deferredMinorCount':2,'rangeDiffCheckExitCode':0})
c.write_text(json.dumps(d,indent=2)+'\n');print('Recorded all21 rulings,2 deferred minors and13 declined-review dispositions; complete PR body ready.')
