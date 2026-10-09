import json,subprocess,hashlib
from pathlib import Path
w=Path('/tmp/cerberus-uc20-worktree');head='67042c4632936dcb73ee170e00ea10e061a1a2a5';base='1713397fea3deb4ae9d30119e493997eb7943514'
def git(*args):return subprocess.check_output(['git',*args],cwd=w,text=True).strip()
p=json.loads(Path('/tmp/cerberus-uc20-pr-final.json').read_text());assert p['headRefOid']==head;assert p['baseRefName']=='develop';assert p['mergeable']=='MERGEABLE' and p['mergeStateStatus']=='CLEAN',p
latest={}
for r in p['statusCheckRollup']:
 name=r.get('name')
 if name and (name not in latest or r.get('startedAt','')>latest[name].get('startedAt','')):latest[name]=r
for name in ('branch-policy','test','docker'):
 r=latest[name];assert r['status']=='COMPLETED' and r['conclusion']=='SUCCESS',(name,r)
assert git('rev-parse','HEAD')==head;assert git('rev-parse','origin/develop')==base;assert not git('status','--porcelain'),git('status','--porcelain')
primary=Path('/root/repositories/cerberus-api');d=json.loads((primary/'.superpowers/checkpoints/open-issues-2026-10-08.json').read_text())
for f,s in d['preservedUserFilesSha256'].items():assert hashlib.sha256((primary/f).read_bytes()).hexdigest()==s,f
rules=json.loads(Path('/tmp/cerberus-uc20-branch-rules.json').read_text());assert all(x.get('parameters',{}).get('required_approving_review_count',0)==0 for x in rules if x['type']=='pull_request')
print('PASS UC20 exacthead latest threeCI SUCCESS +MERGEABLE/CLEAN +freshunchangedbase +cleanbranch +fourprimarySHA +no required approval')
