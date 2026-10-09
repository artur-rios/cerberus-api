import json,subprocess,hashlib,shutil,re
from pathlib import Path
w=Path('/tmp/cerberus-uc28-worktree');primary=Path('/root/repositories/cerberus-api');base='5c5615935786da2ecc8d82b447fbb6d99d85c481'
def git(*args):return subprocess.check_output(['git',*args],cwd=w,text=True).strip()
head=Path('/tmp/cerberus-uc28-tested-head.txt').read_text().strip();pr=json.loads(Path('/tmp/cerberus-uc28-pr-merged.json').read_text());issue=json.loads(Path('/tmp/cerberus-uc28-issue-final.json').read_text());project=json.loads(Path('/tmp/cerberus-uc28-project-done-result.json').read_text());validation=json.loads(Path('/tmp/cerberus-uc28-validation.json').read_text());total=validation['tests']
assert pr['state']=='MERGED' and pr['headRefOid']==head and pr['baseRefName']=='develop';merge=pr['mergeCommit']['oid'];number=pr['number'];assert git('rev-parse','origin/develop')==merge;assert git('rev-parse',head+'^{tree}')==git('rev-parse','origin/develop^{tree}');assert git('merge-base',head,base)==base;assert git('rev-parse','HEAD')==head and not git('status','--porcelain')
assert issue['number']==29 and issue['state']=='CLOSED';assert len(re.findall(r'^- \[x\]',issue['body'],re.M))==9 and '- [ ]' not in issue['body']
assert not Path('/tmp/cerberus-uc28-remote-after-merge.txt').read_text().strip();assert 'errors' not in project;v=project['data']['updateProjectV2ItemFieldValue']['projectV2Item'];assert v['content']['number']==29 and v['fieldValueByName']['name']=='Done'
readme=git('show','origin/develop:README.md');assert any('UC-28 — Move folder' in x and x.endswith('Done |') for x in readme.splitlines());assert '20 / 26 closed' in readme
checkpoint=primary/'.superpowers/checkpoints/open-issues-2026-10-08.json';d=json.loads(checkpoint.read_text())
for f,s in d['preservedUserFilesSha256'].items():assert hashlib.sha256((primary/f).read_bytes()).hexdigest()==s,f
full=Path(validation['log']).read_text();a=re.findall(r'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)',full);assert len(a)==6 and sum(int(x[1]) for x in a)==total and all(f=='0' and s=='0' and p==t for f,p,s,t in a)
summary=json.loads((w/'docs/coverage-report/Summary.json').read_text());assert summary['summary']['assemblies']==6 and all(x['coverage']>=90 for x in summary['coverage']['assemblies']);line=summary['summary']['linecoverage'];branch=summary['summary']['branchcoverage']
ledger=w/'.superpowers/sdd/2026-10-09-uc-28-move-folder/progress.md';l=ledger.read_text();assert all(f'Task {n}: complete' in l for n in (1,2,3));rulings=[x for x in l.splitlines() if 'Ruling:' in x];minors=[x for x in l.splitlines() if 'minor (deferred):' in x];entry=next(x for x in d['useCases'] if x['id']=='UC-28');assert len(rulings)==entry['rulingCount'] and len(minors)==entry['deferredMinorCount']
items=['Own isolated feature branch from verified develop','Main and all AF01..06 implemented and verified','Unit/Data/actualnativeHTTP at correctlayers',f'Freshunfiltered{total}PASS zero fail-skip/{line}line/{branch}branch sixassemblies>=90line/allotherchecks','Ownership/native/sharing/opaquecontent preserved; independentformal/clientreleasegates pending','ONEfreshordinaryreview +exactlatesthead3CISUCCESS +normalmatchheadsquash under explicitauthorization','Actualremotebranchabsent','Actualissue29Closed/nineDoDchecked/projectDone','MergedREADMEUC28Done/M03twenty']
record={'useCase':'UC-28','issue':29,'pr':number,'testedHead':head,'mergeCommit':merge,'definitionOfDone':[{'item':x,'passed':True} for x in items],'developTreeEqualsTestedHead':True,'primaryFourSha256Unchanged':True,'rulings':len(rulings),'deferredMinors':len(minors),'tests':total,'lineCoverage':line,'branchCoverage':branch}
Path('/tmp/cerberus-uc28-dod.json').write_text(json.dumps(record,indent=2)+'\n');ledger.write_text(l+f'\nUC28 integratedCOMPLETE PR{number} normalmatchheadsquash {merge};latestexactheadbranch-policy/test/dockerSUCCESS;issue29Closed9DoD/projectDone/remotebranchabsent;freshdevelopSHA+tree==testedHEAD;4primarySHAunchanged. Ownledger/review/package/logs/coverageSummary archived beforeonlyownscratchcleanup. UC29 next; typed terminal identities must be preserved and selected ProfileId must never be nulled.\n')
archive=primary/'.superpowers/checkpoints/uc-28-2026-10-09-complete';archive.mkdir(parents=True,exist_ok=True)
for f in Path('/tmp').glob('cerberus-uc28-*'):
 if f.is_file():shutil.copy2(f,archive/f.name)
shutil.copytree(ledger.parent,archive/'sdd-evidence',dirs_exist_ok=True);shutil.copy2(w/'docs/coverage-report/Summary.json',archive/'coverage-summary.json')
for kind in ('specs','plans'):shutil.copy2(w/f'docs/superpowers/{kind}/2026-10-09-uc-28-move-folder.md',archive/f'{kind}.md')
def same(source,target):assert hashlib.sha256(source.read_bytes()).digest()==hashlib.sha256(target.read_bytes()).digest(),str(source)
for f in Path('/tmp').glob('cerberus-uc28-*'):
 if f.is_file():same(f,archive/f.name)
for f in ledger.parent.rglob('*'):
 if f.is_file():same(f,archive/'sdd-evidence'/f.relative_to(ledger.parent))
same(w/'docs/coverage-report/Summary.json',archive/'coverage-summary.json')
for kind in ('specs','plans'):same(w/f'docs/superpowers/{kind}/2026-10-09-uc-28-move-folder.md',archive/f'{kind}.md')
xmls=list((w/'tests').glob('**/TestResults/*/coverage.cobertura.xml'));assert len(xmls)==6,len(xmls)
for f in xmls:
 target=archive/'coverage-inputs'/f.relative_to(w/'tests');target.parent.mkdir(parents=True,exist_ok=True);shutil.copy2(f,target);same(f,target)
manifest={str(f.relative_to(archive)):hashlib.sha256(f.read_bytes()).hexdigest() for f in archive.rglob('*') if f.is_file() and f.name!='sha256-manifest.json'}
(archive/'sha256-manifest.json').write_text(json.dumps(manifest,indent=2)+'\n')
for f,sha in manifest.items():assert hashlib.sha256((archive/f).read_bytes()).hexdigest()==sha,f
body=pr['body'];assert all(x in body for x in rulings+minors)
ci=json.loads(Path('/tmp/cerberus-uc28-pr-final.json').read_text());assert ci['headRefOid']==head
latest={}
for x in ci['statusCheckRollup']:
 name=x['name']
 if name not in latest or x.get('startedAt','')>=latest[name].get('startedAt',''):latest[name]=x
assert all(latest[n]['status']=='COMPLETED' and latest[n]['conclusion']=='SUCCESS' for n in ('branch-policy','test','docker'))
for x in d['useCases']:
 if x.get('id')=='UC-28':x.update({'status':'done','projectStatus':'Done','totalTests':total,'reviewAgent':'/root/uc28_final_review','reviewVerdict':'oneImportantfixtureFixedTDD_and_exactCIverified','deferredMinorCount':len(minors),'prUrl':pr['url'],'pr':number,'mergeCommit':merge,'definitionOfDone':'verified_all_nine','remoteBranchDeleted':True,'archive':str(archive),'currentTask':'complete'})
for k in ('issuesMergedThisRun','issuesClosedThisRun'):
 if 29 not in d[k]:d[k].append(29)
d['deferredMinorFindings'].append({'useCase':'UC-28','findings':minors,'runtime':'strict canonicalpath/vaultheader/noquery/exacttwobody/fouroutput/ownerparentmove/empty413 actualHTTP148','followUp':'shared OpenAPI required header/body/output and empty413 metadata before client release; full UC28 range diffcheck passed','evidence':f'PR{number} allRulings+Minors/archivedreview'})
v=f'UC28COMPLETE PR{number}merged{merge};issue29Closed9DoD/projectDone/remoteabsent/developtestedtree/4primarySHAunchanged;{total}PASS/{line}line/{branch}branch;all{len(rulings)}Rulings+{len(minors)}Minors archived.29UCsDone. UC29 issue30 next fresh spec/design/isolation/ledger/TDD; typed terminal identity and existing lock/inventory obligations apply.'
for k in ('currentTask','resumeInstruction','requiredToResume'):d[k]=v
d['nextUseCase']='UC-29';d['checkedAt']='2026-10-09'
for s in d['steps']:
 if s['step']=='Report completed use cases':s['status']='29 use cases merged, closed, Done; UC29 next'
checkpoint.write_text(json.dumps(d,indent=2)+'\n');assert (archive/'sdd-evidence/progress.md').read_text()==ledger.read_text();shutil.rmtree(ledger.parent)
print(f'PASSallnineDoD UC28PR{number}merged{merge};29UCsDone; ownarchive{archive};UC29next')
