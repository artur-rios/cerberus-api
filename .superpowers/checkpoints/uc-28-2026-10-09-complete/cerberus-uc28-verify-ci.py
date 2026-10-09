from pathlib import Path
import json,subprocess,hashlib
w=Path('/tmp/cerberus-uc28-worktree');head=Path('/tmp/cerberus-uc28-tested-head.txt').read_text().strip();base='5c5615935786da2ecc8d82b447fbb6d99d85c481';d=json.loads(Path('/tmp/cerberus-uc28-pr-final.json').read_text());rules=json.loads(Path('/tmp/cerberus-uc28-branch-rules.json').read_text())
def git(*args):return subprocess.check_output(['git',*args],cwd=w,text=True).strip()
assert d['state']=='OPEN' and d['baseRefName']=='develop' and d['headRefOid']==head;assert d['mergeable']=='MERGEABLE' and d['mergeStateStatus']=='CLEAN';assert git('rev-parse','HEAD')==head and git('rev-parse','origin/develop')==base and not git('status','--porcelain');required=set();pr_rule=False
for r in rules:
 if r['type']=='required_status_checks':required.update(x['context'] for x in r['parameters']['required_status_checks'])
 if r['type']=='pull_request':
  p=r['parameters'];assert p['required_approving_review_count']==0 and not p['require_code_owner_review'] and not p['require_last_push_approval'] and not p.get('required_reviewers');assert 'squash' in p.get('allowed_merge_methods',['squash']);pr_rule=True
assert pr_rule and required=={'branch-policy','test','docker'}
latest={}
for c in d['statusCheckRollup']:
 name=c.get('name');started=c.get('startedAt','');
 if name not in latest or started>=latest[name].get('startedAt',''):latest[name]=c
assert all(n in latest and latest[n]['status']=='COMPLETED' and latest[n]['conclusion']=='SUCCESS' for n in required),latest
primary=Path('/root/repositories/cerberus-api');checkpoint=json.loads((primary/'.superpowers/checkpoints/open-issues-2026-10-08.json').read_text())
for f,s in checkpoint['preservedUserFilesSha256'].items():assert hashlib.sha256((primary/f).read_bytes()).hexdigest()==s,f
print(json.dumps({'pr':d['number'],'testedHead':head,'freshBase':base,'checks':{n:latest[n]['conclusion'] for n in sorted(required)},'mergeable':d['mergeable'],'mergeStateStatus':d['mergeStateStatus'],'humanApprovalsRequired':0,'primaryFourSHAUnchanged':True},indent=2))
