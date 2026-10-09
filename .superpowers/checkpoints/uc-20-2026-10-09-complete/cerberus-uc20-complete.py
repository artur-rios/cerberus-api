import json,subprocess,hashlib,shutil,re
from pathlib import Path
w=Path('/tmp/cerberus-uc20-worktree');primary=Path('/root/repositories/cerberus-api');head='67042c4632936dcb73ee170e00ea10e061a1a2a5';base='1713397fea3deb4ae9d30119e493997eb7943514'
def git(*args):return subprocess.check_output(['git',*args],cwd=w,text=True).strip()
pr=json.loads(Path('/tmp/cerberus-uc20-pr-merged.json').read_text());issue=json.loads(Path('/tmp/cerberus-uc20-issue-final.json').read_text());project=json.loads(Path('/tmp/cerberus-uc20-project-done-result.json').read_text())
assert pr['number']==81 and pr['state']=='MERGED' and pr['headRefOid']==head and pr['baseRefName']=='develop';merge=pr['mergeCommit']['oid'];assert git('rev-parse','origin/develop')==merge;assert git('rev-parse',head+'^{tree}')==git('rev-parse','origin/develop^{tree}');assert git('merge-base',head,base)==base;assert git('rev-parse','HEAD')==head and not git('status','--porcelain')
assert issue['number']==21 and issue['state']=='CLOSED';assert len(re.findall(r'^- \[x\]',issue['body'],re.M))==9 and '- [ ]' not in issue['body']
assert not Path('/tmp/cerberus-uc20-remote-after-merge.txt').read_text().strip();assert 'errors' not in project;v=project['data']['updateProjectV2ItemFieldValue']['projectV2Item'];assert v['content']['number']==21 and v['fieldValueByName']['name']=='Done'
readme=git('show','origin/develop:README.md');assert any('UC-20 — Delete record' in x and x.endswith('Done |') for x in readme.splitlines());assert '12 / 26 closed' in readme
checkpoint=primary/'.superpowers/checkpoints/open-issues-2026-10-08.json';d=json.loads(checkpoint.read_text())
for f,s in d['preservedUserFilesSha256'].items():assert hashlib.sha256((primary/f).read_bytes()).hexdigest()==s,f
full=Path('/tmp/cerberus-uc20-coverage.log').read_text();a=re.findall(r'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)',full);assert len(a)==6 and sum(int(x[1]) for x in a)==3878 and all(f=='0' and s=='0' and p==t for f,p,s,t in a)
summary=json.loads((w/'docs/coverage-report/Summary.json').read_text());assert summary['summary']['assemblies']==6 and all(x['coverage']>=90 for x in summary['coverage']['assemblies'])
ledger=w/'.superpowers/sdd/2026-10-09-uc-20-delete-record/progress.md';l=ledger.read_text();assert all(f'Task {n}: complete' in l for n in (1,2,3));assert len([x for x in l.splitlines() if 'Ruling:' in x])==20;assert len([x for x in l.splitlines() if 'minor (deferred):' in x])==2
items=['Own isolated feature branch from freshly verified develop','Main and all AF01..05 implemented/verified','Unit/Data/actual native HTTP tests at correct layers','Fullunfiltered3878PASS zero fail-skip98.3line91.7branch, sixassemblies>=90line, allotherchecks','Ownership/native/sharing/opaquecontent retained; independentformalreleasegatepending','ONEfreshordinaryreview0blocking +latestexacthead3CI SUCCESS +normalmatchheadsquash under explicitauthorization','Actualremotebranchabsent','Actualissue21Closed/nineDoDchecked/projectDone','MergedREADME UC20Done/M03 twelve']
record={'useCase':'UC-20','issue':21,'pr':81,'testedHead':head,'mergeCommit':merge,'definitionOfDone':[{'item':x,'passed':True} for x in items],'developTreeEqualsTestedHead':True,'primaryFourSha256Unchanged':True,'rulings':20,'deferredMinors':2,'tests':3878,'lineCoverage':98.3,'branchCoverage':91.7}
Path('/tmp/cerberus-uc20-dod.json').write_text(json.dumps(record,indent=2)+'\n');ledger.write_text(l+'\nUC20 integrated COMPLETE: PR81 normalmatchheadsquash '+merge+';latestexactheadbranch-policy/test/dockerSUCCESS;issue21Closed/9DoDchecked/projectDone/remotebranchabsent;freshdevelopSHA+tree==testedHEAD;4primarySHAunchanged. Ownledger/review/package/logs/coverageSummary archived beforeonlyownscratchcleanup. UC21next.\n')
archive=primary/'.superpowers/checkpoints/uc-20-2026-10-09-complete';archive.mkdir(parents=True,exist_ok=True)
for f in Path('/tmp').glob('cerberus-uc20-*'):
 if f.is_file():shutil.copy2(f,archive/f.name)
shutil.copytree(ledger.parent,archive/'sdd-evidence',dirs_exist_ok=True);shutil.copy2(w/'docs/coverage-report/Summary.json',archive/'coverage-summary.json')
for kind in ('specs','plans'):shutil.copy2(w/f'docs/superpowers/{kind}/2026-10-09-uc-20-delete-record.md',archive/f'{kind}.md')
for x in d['useCases']:
 if x.get('id')=='UC-20':x.update({'status':'done','projectStatus':'Done','totalTests':3878,'webTests':1236,'queryTests':259,'reviewAgent':'/root/uc20_final_review','importantReviewFindingsFixed':0,'reviewVerdict':'ready_for_development_CI','deferredMinorCount':2,'prUrl':pr['url'],'mergeCommit':merge,'definitionOfDone':'verified_all_nine','remoteBranchDeleted':True,'archive':str(archive),'currentTask':'complete'})
for k in ('issuesMergedThisRun','issuesClosedThisRun'):
 if 21 not in d[k]:d[k].append(21)
d['deferredMinorFindings'].append({'useCase':'UC-20','finding':'SharedOpenAPIrequiredheader/body/sixoutputfields and actualempty413 modeledProblemDetails','runtime':'strictinputs/exact6metadata/empty413 testedHTTP99','followUp':'sharedgeneratorcorrectbeforerelease','evidence':'PR81 exhaustive20rulings+2minors and archivedfinalreview'})
v=f'UC20 COMPLETE PR81merged{merge};issue21Closed9DoD/projectDone/remoteabsent/developtestedtree/4primarySHAunchanged;3878PASS98.3line91.7branch;20rulings+2minorsarchived.21UCsDone. UC21issue22next: freshspec/design/isolatedworktree/ledger/InProgress/TDD.'
for k in ('currentTask','resumeInstruction','requiredToResume'):d[k]=v
d['nextUseCase']='UC-21';d['checkedAt']='2026-10-09';
for s in d['steps']:
 if s['step']=='Report completed use cases':s['status']='21 use cases merged/closed/Done; UC21 next'
checkpoint.write_text(json.dumps(d,indent=2)+'\n');assert (archive/'sdd-evidence/progress.md').read_text()==ledger.read_text();shutil.rmtree(ledger.parent)
print('PASS allnineDoD;UC20PR81 merged '+merge+';21UCsDone; ownarchive '+str(archive)+'; UC21next')
