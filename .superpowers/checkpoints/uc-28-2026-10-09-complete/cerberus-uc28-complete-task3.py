from pathlib import Path
import json,subprocess,hashlib
w=Path('/tmp/cerberus-uc28-worktree');primary=Path('/root/repositories/cerberus-api')
v=json.loads(Path('/tmp/cerberus-uc28-coverage-evidence.json').read_text());assert v['tests']==6558 and len(v['assemblies'])==6 and all(x['coverage']>=90 for x in v['assemblies'])
checkpoint=primary/'.superpowers/checkpoints/open-issues-2026-10-08.json';d=json.loads(checkpoint.read_text())
for f,s in d['preservedUserFilesSha256'].items():assert hashlib.sha256((primary/f).read_bytes()).hexdigest()==s,f
assert subprocess.check_output(['git','rev-parse','origin/develop'],cwd=w,text=True).strip()=='5c5615935786da2ecc8d82b447fbb6d99d85c481'
rules=json.loads(Path('/tmp/cerberus-uc28-branch-rules.json').read_text());p=next(x['parameters'] for x in rules if x['type']=='pull_request');assert p['required_approving_review_count']==0
plan=w/'docs/superpowers/plans/2026-10-09-uc-28-move-folder.md';s=plan.read_text();a,b=s.split('### Task 3:',1);b,c=b.split('## Delivery',1);assert b.count('- [ ]')==6;plan.write_text(a+'### Task 3:'+b.replace('- [ ]','- [x]')+'## Delivery'+c)
ledger=w/'.superpowers/sdd/2026-10-09-uc-28-move-folder/progress.md';s=ledger.read_text();assert 'Task 3: complete' not in s
ledger.write_text(s+f'\nTask3 fresh unfiltered actual6558PASS0fail0skip: Domain361/Shared125/Query411/Command1076/Data2456/WebApi2129. ActualSummary{v["lineCoverage"]}%line/{v["branchCoverage"]}%branch/all6asms>=90line. Freshlockedrestore/audits/native322x4/helpers36+23/specs/OpenAPIshape+drift/allnewfileswhitespace/freshbase/rules0approvals/4primarySHA verified. HTTP148firstGREEN; no product fixes. All18Rulings preserved. Task3 commit/task-done actualfullcoverageaudit then ONEfreshwholebranchreview.\n')
print('Task3 six required checkbox steps evidence complete; commit/task-done next.')
