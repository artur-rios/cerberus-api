from pathlib import Path
import json,subprocess
w=Path('/tmp/cerberus-uc25-worktree'); p=Path('/root/repositories/cerberus-api/.superpowers/checkpoints');ledger=w/'.superpowers/sdd/2026-10-09-uc-25-get-folder/progress.md'
report=Path('/tmp/cerberus-uc25-final-review.md').read_text()
assert 'None found for the authorized develop merge.' in report
assert subprocess.check_output(['git','rev-parse','HEAD'],cwd=w,text=True).strip()=='36f93bc185fac8c8e31d47956216146101ad9c1d'
assert not subprocess.check_output(['git','status','--porcelain'],cwd=w,text=True).strip()
dispositions=[
('Independent formal security/protocol/cryptographic approval remains development-only deferred','explicit user instruction permits develop while ordinary ownership/native/output/snapshot checks remain required','latent defects require independent qualification before production'),
('Real external-client key provisioning/decryption/end-to-end operability remains mandatory release qualification','native server fixtures do not independently qualify an external client','clients may lack keys or fail decryption despite server fixtures passing'),
('Main/tag/release/deployment readiness remains outside this develop verdict','batch authorization targets develop and preserves release gates','premature release exposes unqualified behavior'),
('Durable ordered offline inherited-visibility delivery remains later synchronization work','a current GET snapshot cannot certify durable synchronization','offline clients can retain stale visibility until synchronization is implemented'),
('Future folder update/trash/move/restore/purge writers require their own gates','direct database mutations prove current read re-evaluation only','future writers can violate scope, lifecycle or lock ordering if their gates are skipped'),
('Other physical erasure paths and implicit recipient-account FK audit BEFORE UC35 remain mandatory later gates','this read endpoint changes neither physical handlers nor sharing writers','retained ciphertext or future sharing deadlocks remain possible until those gates'),
('Production workload/load qualification remains before release','bounded target/ancestry EXPLAIN checks are narrower than unspecified production throughput targets','overlapping contributor signature work can affect latency and availability'),
('Historical UC24 provenance remains executor-verified rather than independently re-certified','review was read-only while fresh UC25 full-suite evidence was inspected','incorrect historical/environment assumptions need separate diagnosis'),
('Executor must verify exact-head CI/rules/mergeability, normal merge, issue/project closure and archive cleanup','these are post-review delivery gates excluded from reviewer actions','premature completion can leave unverified or unintegrated work'),
('Inherited UC23 narrowed grant/post-parent-failure fixtures retain recorded follow-ups','unchanged fixtures are outside this diff and UC25 real membership/native fixtures were reviewed','UC23 regression confidence remains narrower until fixture correction'),
('Inherited UC22 permission-outage 500 classification retains its deferred release Minor','unchanged deletion mapping is outside this diff and UC25 necessary dependencies have tested 503 mapping','operators can receive misleading deletion outage classification'),
('Inherited UC21 formatting blank retains its recorded Minor','unchanged line has no UC25 runtime effect and this complete branch range passes whitespace checks','formatting debt remains'),
('Child inventories remain excluded from the eight-field detail payload','binding ruling uses permitted list/detail calls for navigation','clients incur extra navigation requests'),
('Private ancestry and inherited-as-direct profile links remain excluded','independent parent authority and direct organization protect private owner metadata','clients must tolerate null parent and distinguish inherited visibility'),
('Record-only authority never expands into folder navigation','binding folder authority uses actual folder membership only','record-only clients navigate without private container metadata'),
('Corrupt eligible overlapping contributors deliberately cause whole-response 503','every contributor must pass native validation under ruling 6','repair is required and overlapping routes add signature work'),
('Incomplete/cross-owner ancestry defensive coverage is source/schema assessed; a future corruption fixture may bypass integrity constraints explicitly','same-owner recursion requires a null-parent terminus and normal FK constraints prevent these invalid states, but no dedicated broken-FK integration fixture exists','regression confidence in this defensive guard remains narrower until a dedicated fixture')]
assert len([x for x in report.split('### Declined to judge',1)[1].split('### Assessment',1)[0].splitlines() if x.startswith('- ')])==16
s='\nUC25 final ordinary review: ONE fresh gpt-6-astra reviewer, base 955ca9d..36f93bc, report /tmp/cerberus-uc25-final-review.md. No Critical/Important after checking actual effects against source; three Minors deferred. No blocking correction pass, no second review, no product/test changes after tested HEAD.\n'
s+=''.join(f'Final: Ruling: {what} — {why} — cost if wrong: {cost}.\n' for what,why,cost in dispositions)
s+='Final: minor (deferred): Shared OpenAPI omits required vault-access header/output metadata and advertises nullable profileIds/collectionIds although runtime requires access and always returns all eight fields with nonnull arrays. Correct shared generation before external-client release. Cost: clients can omit access and receive 401 or unnecessarily model missing/null fields; runtime authorization/output are correct.\n'
s+='Final: minor (deferred): Shared OpenAPI advertises ProblemDetails for actual empty 413. Correct shared metadata before client release. Cost: generated decoders may attempt an absent body; bounded rejection remains correct.\n'
s+='Final: minor (deferred): GetFolderQuery constructor parameter recordId populates FolderId. Rename to folderId in later maintenance. Cost: named callers/maintainers see a misleading name; current positional calls and HTTP behavior are correct.\n'
l=ledger.read_text();assert 'UC25 final ordinary review:' not in l;ledger.write_text(l+s)
a=p/'batch-review-decisions-2026-10-08.md';a.write_text(a.read_text()+'\n'+s)
l=ledger.read_text();rulings=[x for x in l.splitlines() if 'Ruling:' in x];minors=[x for x in l.splitlines() if 'minor (deferred):' in x];assert len(rulings)==25 and len(minors)==3
validation='''- Fresh unfiltered suite: **5,474 passed, zero failed/skipped** across Domain 361, Shared 125, Query 411, Data 1,971, Command 841, WebApi 1,765.
- Actual production coverage: **98.4% line / 92.1% branch**; all six assemblies at least 90% line.
- Focused Query 55, PostgreSQL 127 and native HTTP 83 passed. Missing-contract/store RED and 82 missing-GET HTTP failures precede implementation; the temporary installer assertion correction is excluded from product RED evidence.
- Locked restore, NuGet direct/transitive audit, native OSV 153 packages/no findings, native corpus 322 cases in four directions, 36 native helpers, 23 Python helpers, specs and OpenAPI write/drift/shape passed.
- Complete branch-range whitespace passed including all added files.
- One fresh whole-branch review found no blockers. All 16 declined judgments and the narrower source/schema-only incomplete-ancestry evidence have explicit final rulings; all three Minors retained below.
- Exact-head branch-policy/test/docker CI must pass before normal develop merge.'''
body=Path('/tmp/cerberus-uc25-pr-body-draft.md').read_text().replace('{{VALIDATION}}',validation).replace('{{RULINGS}}','\n\n'.join('- '+x for x in rulings)).replace('{{MINORS}}','\n\n'.join('- '+x for x in minors));assert '{{' not in body;Path('/tmp/cerberus-uc25-pr-body.md').write_text(body)
c=p/'open-issues-2026-10-08.json';d=json.loads(c.read_text());v='UC25 ONE review complete; no blockers; all25 rulings (8initial+16declined+1narrowevidence) and3Minors recorded exhaustively. Clean tested HEAD36f93bc/5474PASS98.4line92.1branch. Next normalpush/PRissue26Testing/exactHEADCI/freshbase955ca9d/rules/CLEAN/normalmerge/nineDoD/Done/remoteabsent/testedtree/primarySHA/archive; then UC26issue27.'
for k in ('currentTask','resumeInstruction','requiredToResume'):d[k]=v
next(x for x in d['useCases'] if x['id']=='UC-25').update({'currentTask':'Review complete; remote delivery next','reviewVerdict':'ready_for_development_CI','rulingCount':25,'deferredMinorCount':3,'rangeDiffCheckExitCode':0})
c.write_text(json.dumps(d,indent=2)+'\n');print('Recorded all25 rulings and3 deferred Minors; complete PR body ready.')
