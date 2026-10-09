from pathlib import Path
import json, subprocess, hashlib, shutil
w=Path('/tmp/cerberus-uc26-worktree');primary=Path('/root/repositories/cerberus-api');p=primary/'.superpowers/checkpoints';d=json.loads((p/'open-issues-2026-10-08.json').read_text())
assert d['nextUseCase']=='UC-26';previous=json.loads((p/'uc-25-2026-10-09-complete/cerberus-uc25-dod.json').read_text());assert previous['tests']==5474 and all(x['passed'] for x in previous['definitionOfDone'])
base=previous['mergeCommit'];git=lambda *a:subprocess.check_output(['git',*a],cwd=w,text=True).strip()
assert git('rev-parse','HEAD')==base and git('rev-parse','origin/develop')==base and git('branch','--show-current')=='feature/uc-26-update-folder' and not git('status','--porcelain')
assert git('rev-parse','HEAD^{tree}')==git('rev-parse',previous['testedHead']+'^{tree}')
for f,s in d['preservedUserFilesSha256'].items():assert hashlib.sha256((primary/f).read_bytes()).hexdigest()==s
issue=json.loads(Path('/tmp/cerberus-uc26-issue.json').read_text());assert issue['number']==27 and issue['state']=='OPEN' and 'FR-FD-04' in issue['body']
spec=w/'docs/superpowers/specs/2026-10-09-uc-26-update-folder.md';plan=w/'docs/superpowers/plans/2026-10-09-uc-26-update-folder.md'
assert not spec.exists() and not plan.exists();shutil.copy2('/tmp/cerberus-uc26-spec.md',spec);shutil.copy2('/tmp/cerberus-uc26-plan.md',plan)
script='/root/.codex/plugins/cache/openai-curated-remote/superpowers/6.4.2/skills/subagent-driven-development/scripts/sdd-workspace'
workspace=Path(subprocess.check_output([script,str(plan)],cwd=w,text=True).strip());ledger=workspace/'progress.md';assert not ledger.exists()
rulings=Path('/tmp/cerberus-uc26-rulings.txt').read_text()
ledger.write_text(f'# SDD ledger — plan: {plan}\n\nBase: {base}\nBranch: feature/uc-26-update-folder\nIssue:27 UC26UpdateFolder\nAuthorized all-open-issues batch, automated design/plan gates and inline execution; no repeat approval.\n\n'+rulings+'\nPre-flight Task1→Task2: FolderUpdateRequest/Input/IFolderUpdateStore exact signatures and FolderCreateDetails agree.\nPre-flight Task1+Task2→Task3: UpdateFolderCommand/Handler/Output/Messages/Validator and explicit Startup DI agree; strict three-field body/four-field result/current folder-only authority.\nGate1: fresh issue27/specUC26/main+AF01..05/FR-FD-04/BR07+22+27/authorization/testing/technology/operations loaded. All cited FR exists, each AF and five focus lines mapped; three test-first tasks, self-review no placeholders or undecided interface. Baseline tree verified; no product/test implementation yet.\n')
a=p/'batch-review-decisions-2026-10-08.md';a.write_text(a.read_text()+'\n## UC26 initial decisions\n\n'+rulings)
entry=next((x for x in d['useCases'] if x['id']=='UC-26'),None)
if entry is None:entry={'id':'UC-26','issue':27};d['useCases'].append(entry)
entry.update({'status':'in_progress','branch':'feature/uc-26-update-folder','worktree':str(w),'base':base,'ledger':str(ledger),'currentTask':'Task1 command RED next'})
for k in ('currentTask','resumeInstruction','requiredToResume'):d[k]='UC26 issue27 fresh design/plan/self-check/owned worktree at verified UC25 merge. All10 initialRulings ledgered; three inline TDD tasks/ONE ordinary review/exact-head CI. Task1 command RED next; no product/test files installed yet. Preserve primary user work and independent development-only deferrals.'
d['currentUseCase']='UC-26';d['currentWorktree']=str(w);d['currentBranch']='feature/uc-26-update-folder';d['currentLedger']=str(ledger)
for k in ('activeExecSession','activeTestLog'):d.pop(k,None)
(p/'open-issues-2026-10-08.json').write_text(json.dumps(d,indent=2)+'\n');Path('/tmp/cerberus-uc26-base.txt').write_text(base+'\n');print('UC26 specs/plan/all10Rulings/ledger and checkpoint initialized; no product code.')
