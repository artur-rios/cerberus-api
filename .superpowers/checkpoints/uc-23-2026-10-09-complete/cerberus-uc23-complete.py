import json,subprocess,hashlib,shutil,re
from pathlib import Path
w=Path('/tmp/cerberus-uc23-worktree');primary=Path('/root/repositories/cerberus-api');base='d72d92072eaba28509597c545f4833a8ef58faa6'
def git(*args):return subprocess.check_output(['git',*args],cwd=w,text=True).strip()
head=Path('/tmp/cerberus-uc23-tested-head.txt').read_text().strip();pr=json.loads(Path('/tmp/cerberus-uc23-pr-merged.json').read_text());issue=json.loads(Path('/tmp/cerberus-uc23-issue-final.json').read_text());project=json.loads(Path('/tmp/cerberus-uc23-project-done-result.json').read_text());validation=json.loads(Path('/tmp/cerberus-uc23-validation.json').read_text());total=validation['tests']
assert pr['state']=='MERGED' and pr['headRefOid']==head and pr['baseRefName']=='develop';merge=pr['mergeCommit']['oid'];number=pr['number'];assert git('rev-parse','origin/develop')==merge;assert git('rev-parse',head+'^{tree}')==git('rev-parse','origin/develop^{tree}');assert git('merge-base',head,base)==base;assert git('rev-parse','HEAD')==head and not git('status','--porcelain')
assert issue['number']==24 and issue['state']=='CLOSED';assert len(re.findall(r'^- \[x\]',issue['body'],re.M))==9 and '- [ ]' not in issue['body']
assert not Path('/tmp/cerberus-uc23-remote-after-merge.txt').read_text().strip();assert 'errors' not in project;v=project['data']['updateProjectV2ItemFieldValue']['projectV2Item'];assert v['content']['number']==24 and v['fieldValueByName']['name']=='Done'
readme=git('show','origin/develop:README.md');assert any('UC-23 — Create folder' in x and x.endswith('Done |') for x in readme.splitlines());assert '15 / 26 closed' in readme
checkpoint=primary/'.superpowers/checkpoints/open-issues-2026-10-08.json';d=json.loads(checkpoint.read_text())
for f,s in d['preservedUserFilesSha256'].items():assert hashlib.sha256((primary/f).read_bytes()).hexdigest()==s,f
full=Path(validation['log']).read_text();a=re.findall(r'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)',full);assert len(a)==6 and sum(int(x[1]) for x in a)==total and all(f=='0' and s=='0' and p==t for f,p,s,t in a)
summary=json.loads((w/'docs/coverage-report/Summary.json').read_text());assert summary['summary']['assemblies']==6 and all(x['coverage']>=90 for x in summary['coverage']['assemblies']);line=summary['summary']['linecoverage'];branch=summary['summary']['branchcoverage']
ledger=w/'.superpowers/sdd/2026-10-09-uc-23-create-folder/progress.md';l=ledger.read_text();assert all(f'Task {n}: complete' in l for n in (1,2,3,4));rulings=[x for x in l.splitlines() if 'Ruling:' in x];minors=[x for x in l.splitlines() if 'minor (deferred):' in x];assert len(rulings)==24 and len(minors)==4
items=['Own isolated feature branch from verified develop','Main and all AF01..05 implemented and verified','Unit/Data/actualnativeHTTP at correctlayers',f'Freshunfiltered{total}PASS zero fail-skip/{line}line/{branch}branch sixassemblies>=90line/allotherchecks','Ownership/native/sharing/opaquecontent preserved; independentformal/clientreleasegates pending','ONEfreshordinaryreview +exactlatesthead3CISUCCESS +normalmatchheadsquash under explicitauthorization','Actualremotebranchabsent','Actualissue24Closed/nineDoDchecked/projectDone','MergedREADMEUC23Done/M03fifteen']
record={'useCase':'UC-23','issue':24,'pr':number,'testedHead':head,'mergeCommit':merge,'definitionOfDone':[{'item':x,'passed':True} for x in items],'developTreeEqualsTestedHead':True,'primaryFourSha256Unchanged':True,'rulings':len(rulings),'deferredMinors':len(minors),'tests':total,'lineCoverage':line,'branchCoverage':branch}
Path('/tmp/cerberus-uc23-dod.json').write_text(json.dumps(record,indent=2)+'\n');ledger.write_text(l+f'\nUC23 integratedCOMPLETE PR{number} normalmatchheadsquash {merge};latestexactheadbranch-policy/test/dockerSUCCESS;issue24Closed9DoD/projectDone/remotebranchabsent;freshdevelopSHA+tree==testedHEAD;4primarySHAunchanged. Ownledger/review/package/logs/coverageSummary archived beforeonlyownscratchcleanup. UC24 next; typed terminal identities must be preserved and selected ProfileId must never be nulled.\n')
archive=primary/'.superpowers/checkpoints/uc-23-2026-10-09-complete';archive.mkdir(parents=True,exist_ok=True)
for f in Path('/tmp').glob('cerberus-uc23-*'):
 if f.is_file():shutil.copy2(f,archive/f.name)
shutil.copytree(ledger.parent,archive/'sdd-evidence',dirs_exist_ok=True);shutil.copy2(w/'docs/coverage-report/Summary.json',archive/'coverage-summary.json')
for kind in ('specs','plans'):shutil.copy2(w/f'docs/superpowers/{kind}/2026-10-09-uc-23-create-folder.md',archive/f'{kind}.md')
def same(source,target):assert hashlib.sha256(source.read_bytes()).digest()==hashlib.sha256(target.read_bytes()).digest(),str(source)
for f in Path('/tmp').glob('cerberus-uc23-*'):
 if f.is_file():same(f,archive/f.name)
for f in ledger.parent.rglob('*'):
 if f.is_file():same(f,archive/'sdd-evidence'/f.relative_to(ledger.parent))
same(w/'docs/coverage-report/Summary.json',archive/'coverage-summary.json')
for kind in ('specs','plans'):same(w/f'docs/superpowers/{kind}/2026-10-09-uc-23-create-folder.md',archive/f'{kind}.md')
body=pr['body'];assert all(x in body for x in rulings+minors)
ci=json.loads(Path('/tmp/cerberus-uc23-pr-final.json').read_text());assert ci['headRefOid']==head
latest={}
for x in ci['statusCheckRollup']:
 name=x['name']
 if name not in latest or x.get('startedAt','')>=latest[name].get('startedAt',''):latest[name]=x
assert all(latest[n]['status']=='COMPLETED' and latest[n]['conclusion']=='SUCCESS' for n in ('branch-policy','test','docker'))
for x in d['useCases']:
 if x.get('id')=='UC-23':x.update({'status':'done','projectStatus':'Done','totalTests':total,'reviewAgent':'/root/uc23_final_review','reviewVerdict':'ready_for_development_CI','deferredMinorCount':len(minors),'prUrl':pr['url'],'pr':number,'mergeCommit':merge,'definitionOfDone':'verified_all_nine','remoteBranchDeleted':True,'archive':str(archive),'currentTask':'complete'})
for k in ('issuesMergedThisRun','issuesClosedThisRun'):
 if 24 not in d[k]:d[k].append(24)
d['deferredMinorFindings'].append({'useCase':'UC-23','findings':minors,'runtime':'strict required4/optionalparent input/exact four metadata fields/empty413 actualHTTP111','followUp':'shared generator and two narrowed test fixtures before release; full UC23 range diffcheck passed','evidence':f'PR{number} allRulings+Minors/archivedreview'})
v=f'UC23COMPLETE PR{number}merged{merge};issue24Closed9DoD/projectDone/remoteabsent/developtestedtree/4primarySHAunchanged;{total}PASS/{line}line/{branch}branch;all{len(rulings)}Rulings+{len(minors)}Minors archived.24UCsDone. UC24 issue25 next fresh spec/design/isolation/ledger/TDD; typed terminal identity and existing lock/inventory obligations apply.'
for k in ('currentTask','resumeInstruction','requiredToResume'):d[k]=v
d['nextUseCase']='UC-24';d['checkedAt']='2026-10-09'
for s in d['steps']:
 if s['step']=='Report completed use cases':s['status']='24 use cases merged, closed, Done; UC24 next'
checkpoint.write_text(json.dumps(d,indent=2)+'\n');assert (archive/'sdd-evidence/progress.md').read_text()==ledger.read_text();shutil.rmtree(ledger.parent)
print(f'PASSallnineDoD UC23PR{number}merged{merge};24UCsDone; ownarchive{archive};UC24next')
