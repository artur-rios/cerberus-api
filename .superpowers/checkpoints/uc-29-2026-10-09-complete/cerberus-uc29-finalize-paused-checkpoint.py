from pathlib import Path
import json, subprocess, hashlib, shutil

root = Path('/root/repositories/cerberus-api')
w = Path('/tmp/cerberus-uc29-worktree')
tmp = Path('/tmp')
checkpoint = root / '.superpowers/checkpoints/open-issues-2026-10-08.json'
archive = root / '.superpowers/checkpoints/uc-29-2026-10-09-complete'
owned = w / '.superpowers/sdd/2026-10-09-uc-29-create-collection'
plan = 'docs/superpowers/plans/2026-10-09-uc-29-create-collection.md'
def read(name): return json.loads((tmp / ('cerberus-uc29-' + name)).read_text())
def sha(p): return hashlib.sha256(p.read_bytes()).hexdigest()
def git(*args): return subprocess.check_output(['git', *args], cwd=w, text=True).strip()

pr=read('pr-merged.json'); coverage=read('coverage-evidence.json'); ci=read('ci-evidence.json')
head=(tmp/'cerberus-uc29-tested-head.txt').read_text().strip(); merge=pr['mergeCommit']['oid']
manifest=json.loads((archive/'sha256-manifest.json').read_text())
for relative, expected in manifest.items(): assert sha(archive/relative)==expected, relative
assert len(coverage['freshXML'])==6
for relative, expected in coverage['freshXML'].items(): assert sha(archive/'coverage-inputs'/relative)==expected, relative
assert not owned.exists() and not git('status','--porcelain')
assert pr['state']=='MERGED' and git('rev-parse','origin/develop')==merge
assert git('rev-parse','origin/develop^{tree}')==git('rev-parse',head+'^{tree}')
for f,s in read('gate1.json')['primarySha'].items(): assert sha(root/f)==s,f
d = json.loads(checkpoint.read_text())
uc = next(x for x in d['useCases'] if x['id'] == 'UC-29')
uc.update(status='done', worktree=str(w), branch='feature/uc-29-create-collection', head=head,
          pr=90, mergeCommit=merge, tests=7005, lineCoverage=98.4, branchCoverage=91.9,
          review='Sole ordinary review: no Critical/Important/newMinor; 27 rulings and two inherited Minors retained',
          definitionOfDone='pass; nine checked; CLOSED/Done; exact CI green; remote absent; tested trees equal',
          archive=str(archive), ledger=str(archive / 'sdd-evidence/progress.md'),
          ci={**ci['checks'], 'run':37940892359})
for k in ['issuesMergedThisRun', 'issuesClosedThisRun']:
    if 30 not in d[k]: d[k].append(30)
assert sum(x['status'] in ('done', 'complete') for x in d['useCases']) == 30
d['status'] = 'paused'
d['currentTask'] = 'UC29 issue30 complete; user-requested pause before UC30 issue31'
d['currentLedger'] = d['ledger'] = str(archive / 'sdd-evidence/progress.md')
d['lastCompletedUseCase'] = 'UC-29'; d['nextUseCase'] = 'UC-30'; d['nextIssue'] = 31
d['pauseReason'] = 'User: Pause after finishing the current issue'
d['requiredToResume'] = d['resumeInstruction'] = ('Paused by user after UC29 issue30 PR90 merged ' + merge +
    '. Thirty use cases done,25 remain. Do not start UC30 issue31 until user resumes. Then freshly load issue/specifications and verify develop before new design/branch/TDD. Existing authorization and development-only deferral persist; all release and ordinary verification gates remain.')
d['nextUseCasePreparation'] = {'id':'UC-30','issue':31,'status':'not_started; preliminary read-only exploration only','needsFreshCheckoutSpecReload':True}
d['task3Evidence']['completedFullFamilies'] = coverage['counts']
d['task3Evidence']['ciChecks'] = ci['checks']; d['task3Evidence']['ciWatchSession'] = None
d['archiveVerification'] = {'path':str(archive),'files':len(manifest),'sha256Verified':True,'ownedSddRemoved':str(owned)}
for s in d['steps']:
    if s['step'] == 'Implement each use case': s['status'] = 'paused_by_user_after_issue30'
    if s['step'] == 'Report completed use cases': s['status'] = '30 use cases merged, closed, Done; pause before UC30 issue31'
minors = [l for l in (archive/'sdd-evidence/progress.md').read_text().splitlines() if l.startswith('Final: minor')]
assert len(minors) == 2
d['deferredMinorFindings'].append({'useCase':'UC-29','findings':minors,'followUp':'Shared generated OpenAPI requiredness/wrapper/empty413 metadata before external-client release','evidence':'PR90 full27Rulings/2Minors; archived sole review'})
checkpoint.write_text(json.dumps(d,indent=2)+'\n')
report = root / '.superpowers/checkpoints/batch-paused-2026-10-09.md'
lines = ['# Batch paused after UC-29', '',
         'Paused at the user’s request after completing issue #30. PR #90 merged into develop as `' + merge + '`. Issue #30 is closed, all nine Definition of Done checks are completed, and its project status is Done. The remote feature branch is absent and the merged tree equals the exact tested tree.', '',
         'UC29 verification: 7,005 tests passed with no failures or skips; 98.4% line coverage and 91.9% branch coverage; all six production assemblies meet the 90% line gate. Required branch-policy, test and docker checks passed on the exact delivery commit. One whole-branch review found no blocking issues.', '',
         'Thirty use cases are delivered; 25 remain. UC30 issue #31 has not started. Independent security/protocol/client qualification remains deferred for development as instructed; main/tag/release gates remain. Shared OpenAPI requiredness/wrapper and empty-413 metadata still need correction before external-client release. All 27 UC29 rulings, two inherited Minor dispositions, full review and verification artifacts are preserved in [the delivery ledger](' + str(archive/'sdd-evidence/progress.md') + ') and [PR #90](https://github.com/artur-rios/cerberus-api/pull/90). Earlier obligations remain in [the aggregate dispositions](' + str(root/'.superpowers/checkpoints/batch-review-decisions-2026-10-08.md') + ').', '',
         'Evidence archive: '+str(len(manifest))+' files copied and SHA256 verified before only the UC29 SDD directory was removed. Worktrees and primary user files are retained.', '',
         '## Delivered', '', '| Use case | Issue | PR | Definition of Done |', '| --- | --- | --- | --- |']
for x in d['useCases']:
    if x['status'] in ('done', 'complete'):
        number=x.get('pr',x.get('pullRequest')); issue_number=x['issue']['number']
        lines.append(f"| {x['id']} | [#{issue_number}](https://github.com/artur-rios/cerberus-api/issues/{issue_number}) | [#{number}](https://github.com/artur-rios/cerberus-api/pull/{number}) | Pass |")
lines += ['', '## Remaining', '', '| Use case | Issue | Status |', '| --- | --- | --- |']
for x in d['useCases']:
    if x['status'] not in ('done', 'complete'):
        n=x['issue']['number']; lines.append(f"| {x['id']} | [#{n}](https://github.com/artur-rios/cerberus-api/issues/{n}) | Not started |")
report.write_text('\n'.join(lines)+'\n')
print(json.dumps({'status':'paused','currentIssueComplete':30,'mergeCommit':merge,'done':30,'remaining':25,'nextIssueNotStarted':31,'archiveFilesVerified':len(manifest),'report':str(report)},indent=2))
