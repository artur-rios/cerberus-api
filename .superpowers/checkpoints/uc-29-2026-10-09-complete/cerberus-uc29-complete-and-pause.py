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

pr = read('pr-merged.json'); issue = read('issue-final.json'); ci = read('ci-evidence.json')
coverage = read('coverage-evidence.json'); project = read('project-done-confirmed.json')['data']['repository']['issue']
head = (tmp / 'cerberus-uc29-tested-head.txt').read_text().strip(); merge = pr['mergeCommit']['oid']
assert pr['number'] == 90 and pr['state'] == 'MERGED' and pr['headRefOid'] == head
assert ci['testedHead'] == head and all(v == 'SUCCESS' for v in ci['checks'].values())
assert issue['number'] == 30 and issue['state'] == 'CLOSED' and issue['body'].count('- [x]') == 9 and '- [ ]' not in issue['body']
assert project['number'] == 30 and project['state'] == 'CLOSED'
item = next(x for x in project['projectItems']['nodes'] if x['id'] == 'PVTI_lAHOAgOUtM4BmFmuzg_LyfU')
assert item['fieldValueByName']['name'] == 'Done' and item['project']['id'] == 'PVT_kwHOAgOUtM4BmFmu'
assert not (tmp / 'cerberus-uc29-remote-after-merge.txt').read_text().strip()
assert git('rev-parse', 'origin/develop') == merge
assert git('rev-parse', 'origin/develop^{tree}') == git('rev-parse', head + '^{tree}')
assert git('rev-parse', 'HEAD') == head and not git('status', '--porcelain')
assert coverage['tests'] == 7005 and coverage['lineCoverage'] == 98.4 and coverage['branchCoverage'] == 91.9
for f, s in read('precoverage-source-sha.json').items(): assert sha(w / f) == s, f
for f, s in read('gate1.json')['primarySha'].items(): assert sha(root / f) == s, f
readme = subprocess.check_output(['git', 'show', 'origin/develop:README.md'], cwd=w, text=True)
assert '| 21 / 26 closed |' in readme and any('UC-29' in l and l.endswith('| Done |') for l in readme.splitlines())
assert owned.joinpath('progress.md').read_text().splitlines()[0] == '# SDD ledger — plan: ' + plan
assert not archive.exists(), 'Do not overwrite existing archive'
proof = {
    'useCase': 'UC-29', 'issue': 30, 'pr': 90, 'testedHead': head, 'mergeCommit': merge,
    'checks': ci['checks'], 'tests': 7005, 'failed': 0, 'skipped': 0,
    'lineCoverage': 98.4, 'branchCoverage': 91.9, 'projectStatus': 'Done',
    'issueStatus': 'CLOSED', 'definitionOfDoneChecked': 9, 'remoteBranchAbsent': True,
    'mergedTreeEqualsTestedTree': True, 'primaryFourSHAUnchanged': True,
    'ordinaryReviews': 1, 'criticalImportantFindings': 0, 'rulings': 27, 'inheritedMinors': 2,
    'pauseBefore': {'useCase': 'UC-30', 'issue': 31},
    'developmentOnlyDeferrals': 'Independent security/protocol/client qualification; release gates retained',
    'definitionOfDone': [
        'Own feature branch from freshly verified develop',
        'Main flow and AF01..05 mapped and implemented: Gate2',
        'Command98, Data206, HTTP142 focused plus six full families',
        'Fresh7005/7005; six assemblies >=90 line; exact-head required CI success',
        'Ordinary review/native suites/authority/ownership/atomicity/rotation verified; formal release qualification deferred by owner',
        'Sole whole-branch review; normal exact-head squash under explicit unattended authorization',
        'Remote feature branch absent; worktree retained',
        'Issue30 CLOSED; actual project item Done',
        'Merged README UC29 Done; M03 21/26 closed'
    ]
}
(tmp / 'cerberus-uc29-dod.json').write_text(json.dumps(proof, indent=2) + '\n')
with owned.joinpath('progress.md').open('a') as f:
    f.write('\nDelivery COMPLETE: PR90 normal match-head squash into develop ' + merge +
            '; exact-head branch-policy/test/docker SUCCESS; actual issue30 CLOSED/nine DoD checked/project Done; remote feature absent; merged tree equals tested tree; README UC29 Done/M03 21/26; primary four SHA unchanged. Full7005 PASS98.4line/91.9branch; all27Rulings and two inheritedMinors retained.\n\nUser-requested PAUSE after this issue: UC30 issue31 not started. Archive complete evidence and verify SHA before deleting only this owned SDD directory; retain worktrees and user files.\n')
archive.mkdir()
for p in sorted(tmp.glob('cerberus-uc29-*')):
    if p.is_file(): shutil.copy2(p, archive / p.name)
shutil.copytree(owned, archive / 'sdd-evidence')
shutil.copy2(w / plan, archive / 'plans.md')
shutil.copy2(w / 'docs/superpowers/specs/2026-10-09-uc-29-create-collection.md', archive / 'specs.md')
shutil.copy2(w / 'docs/coverage-report/Summary.json', archive / 'coverage-summary.json')
for relative, expected in coverage['freshXML'].items():
    source = w / relative; assert sha(source) == expected, relative
    target = archive / 'coverage-inputs' / relative
    target.parent.mkdir(parents=True, exist_ok=True); shutil.copy2(source, target)
manifest = {str(p.relative_to(archive)): sha(p) for p in sorted(archive.rglob('*')) if p.is_file()}
(archive / 'sha256-manifest.json').write_text(json.dumps(manifest, indent=2) + '\n')
for relative, expected in manifest.items(): assert sha(archive / relative) == expected, relative
assert sha(archive / 'sdd-evidence/progress.md') == sha(owned / 'progress.md')
shutil.rmtree(owned)
assert not owned.exists() and not git('status', '--porcelain')
for f, s in read('gate1.json')['primarySha'].items(): assert sha(root / f) == s, f

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
assert sum(x['status'] == 'done' for x in d['useCases']) == 30
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
    if x['status']=='done':
        number=x.get('pr',x.get('pullRequest')); issue_number=x['issue']['number']
        lines.append(f"| {x['id']} | [#{issue_number}](https://github.com/artur-rios/cerberus-api/issues/{issue_number}) | [#{number}](https://github.com/artur-rios/cerberus-api/pull/{number}) | Pass |")
lines += ['', '## Remaining', '', '| Use case | Issue | Status |', '| --- | --- | --- |']
for x in d['useCases']:
    if x['status']!='done':
        n=x['issue']['number']; lines.append(f"| {x['id']} | [#{n}](https://github.com/artur-rios/cerberus-api/issues/{n}) | Not started |")
report.write_text('\n'.join(lines)+'\n')
print(json.dumps({'status':'paused','currentIssueComplete':30,'mergeCommit':merge,'done':30,'remaining':25,'nextIssueNotStarted':31,'archiveFilesVerified':len(manifest),'report':str(report)},indent=2))
