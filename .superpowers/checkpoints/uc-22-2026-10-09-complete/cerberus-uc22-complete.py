import json,subprocess,hashlib,shutil,re
from pathlib import Path
w=Path('/tmp/cerberus-uc22-worktree');primary=Path('/root/repositories/cerberus-api');base='5c994f6f5626c83cb332cb5170d93907cd8e10ef'
def git(*args):return subprocess.check_output(['git',*args],cwd=w,text=True).strip()
head=Path('/tmp/cerberus-uc22-tested-head.txt').read_text().strip();pr=json.loads(Path('/tmp/cerberus-uc22-pr-merged.json').read_text());issue=json.loads(Path('/tmp/cerberus-uc22-issue-final.json').read_text());project=json.loads(Path('/tmp/cerberus-uc22-project-done-result.json').read_text());validation=json.loads(Path('/tmp/cerberus-uc22-validation.json').read_text());total=validation['tests']
assert pr['state']=='MERGED' and pr['headRefOid']==head and pr['baseRefName']=='develop';merge=pr['mergeCommit']['oid'];number=pr['number'];assert git('rev-parse','origin/develop')==merge;assert git('rev-parse',head+'^{tree}')==git('rev-parse','origin/develop^{tree}');assert git('merge-base',head,base)==base;assert git('rev-parse','HEAD')==head and not git('status','--porcelain')
assert issue['number']==23 and issue['state']=='CLOSED';assert len(re.findall(r'^- \[x\]',issue['body'],re.M))==9 and '- [ ]' not in issue['body']
assert not Path('/tmp/cerberus-uc22-remote-after-merge.txt').read_text().strip();assert 'errors' not in project;v=project['data']['updateProjectV2ItemFieldValue']['projectV2Item'];assert v['content']['number']==23 and v['fieldValueByName']['name']=='Done'
readme=git('show','origin/develop:README.md');assert any('UC-22 — Permanently delete record' in x and x.endswith('Done |') for x in readme.splitlines());assert '14 / 26 closed' in readme
checkpoint=primary/'.superpowers/checkpoints/open-issues-2026-10-08.json';d=json.loads(checkpoint.read_text())
for f,s in d['preservedUserFilesSha256'].items():assert hashlib.sha256((primary/f).read_bytes()).hexdigest()==s,f
full=Path(validation['log']).read_text();a=re.findall(r'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)',full);assert len(a)==6 and sum(int(x[1]) for x in a)==total and all(f=='0' and s=='0' and p==t for f,p,s,t in a)
summary=json.loads((w/'docs/coverage-report/Summary.json').read_text());assert summary['summary']['assemblies']==6 and all(x['coverage']>=90 for x in summary['coverage']['assemblies']);line=summary['summary']['linecoverage'];branch=summary['summary']['branchcoverage']
ledger=w/'.superpowers/sdd/2026-10-09-uc-22-permanently-delete-record/progress.md';l=ledger.read_text();assert all(f'Task {n}: complete' in l for n in (1,2,3,4));rulings=[x for x in l.splitlines() if 'Ruling:' in x];minors=[x for x in l.splitlines() if 'minor (deferred):' in x];assert len(rulings)==26 and len(minors)==3
items=['Own isolated feature branch from verified develop','Main and all AF01..05 implemented and verified','Unit/Data/actualnativeHTTP at correctlayers',f'Freshunfiltered{total}PASS zero fail-skip/{line}line/{branch}branch sixassemblies>=90line/allotherchecks','Ownership/native/sharing/opaquecontent preserved; independentformal/clientreleasegates pending','ONEfreshordinaryreview +exactlatesthead3CISUCCESS +normalmatchheadsquash under explicitauthorization','Actualremotebranchabsent','Actualissue23Closed/nineDoDchecked/projectDone','MergedREADMEUC22Done/M03fourteen']
record={'useCase':'UC-22','issue':23,'pr':number,'testedHead':head,'mergeCommit':merge,'definitionOfDone':[{'item':x,'passed':True} for x in items],'developTreeEqualsTestedHead':True,'primaryFourSha256Unchanged':True,'rulings':len(rulings),'deferredMinors':len(minors),'tests':total,'lineCoverage':line,'branchCoverage':branch}
Path('/tmp/cerberus-uc22-dod.json').write_text(json.dumps(record,indent=2)+'\n');ledger.write_text(l+f'\nUC22 integratedCOMPLETE PR{number} normalmatchheadsquash {merge};latestexactheadbranch-policy/test/dockerSUCCESS;issue23Closed9DoD/projectDone/remotebranchabsent;freshdevelopSHA+tree==testedHEAD;4primarySHAunchanged. Ownledger/review/package/logs/coverageSummary archived beforeonlyownscratchcleanup. UC23 next; typed terminal identities must be preserved and selected ProfileId must never be nulled.\n')
archive=primary/'.superpowers/checkpoints/uc-22-2026-10-09-complete';archive.mkdir(parents=True,exist_ok=True)
for f in Path('/tmp').glob('cerberus-uc22-*'):
 if f.is_file():shutil.copy2(f,archive/f.name)
shutil.copytree(ledger.parent,archive/'sdd-evidence',dirs_exist_ok=True);shutil.copy2(w/'docs/coverage-report/Summary.json',archive/'coverage-summary.json')
for kind in ('specs','plans'):shutil.copy2(w/f'docs/superpowers/{kind}/2026-10-09-uc-22-permanently-delete-record.md',archive/f'{kind}.md')
for x in d['useCases']:
 if x.get('id')=='UC-22':x.update({'status':'done','projectStatus':'Done','totalTests':total,'reviewAgent':'/root/uc22_final_review','reviewVerdict':'ready_for_development_CI','deferredMinorCount':len(minors),'prUrl':pr['url'],'pr':number,'mergeCommit':merge,'definitionOfDone':'verified_all_nine','remoteBranchDeleted':True,'archive':str(archive),'currentTask':'complete'})
for k in ('issuesMergedThisRun','issuesClosedThisRun'):
 if 23 not in d[k]:d[k].append(23)
d['deferredMinorFindings'].append({'useCase':'UC-22','findings':minors,'runtime':'strict expectedRevision input/exact two metadata fields/empty413 actualHTTP107','followUp':'shared generator and ledger permission-error mapping before release; full UC22 range diffcheck passed','evidence':f'PR{number} allRulings+Minors/archivedreview'})
v=f'UC22COMPLETE PR{number}merged{merge};issue23Closed9DoD/projectDone/remoteabsent/developtestedtree/4primarySHAunchanged;{total}PASS/{line}line/{branch}branch;all{len(rulings)}Rulings+{len(minors)}Minors archived.23UCsDone. UC23 issue24 next fresh spec/design/isolation/ledger/TDD; typed terminal identity and existing lock/inventory obligations apply.'
for k in ('currentTask','resumeInstruction','requiredToResume'):d[k]=v
d['nextUseCase']='UC-23';d['checkedAt']='2026-10-09'
for s in d['steps']:
 if s['step']=='Report completed use cases':s['status']='23 use cases merged, closed, Done; UC23 next'
checkpoint.write_text(json.dumps(d,indent=2)+'\n');assert (archive/'sdd-evidence/progress.md').read_text()==ledger.read_text();shutil.rmtree(ledger.parent)
print(f'PASSallnineDoD UC22PR{number}merged{merge};23UCsDone; ownarchive{archive};UC23next')
