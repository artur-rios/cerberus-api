import json,subprocess,hashlib
from pathlib import Path
w=Path('/tmp/cerberus-uc19-worktree')
head='0fc261bedce02663454e2dae163ff9c63c7cd1b2'
base='15cf4fac7bfa7db2552df5618e0f4a236c0fd5b1'
def git(*args):return subprocess.check_output(['git',*args],cwd=w,text=True).strip()
p=json.loads(Path('/tmp/cerberus-uc19-pr-final.json').read_text())
assert p['number']==80 and p['headRefOid']==head
assert p['mergeable']=='MERGEABLE' and p['mergeStateStatus']=='CLEAN',p
latest={}
for r in p['statusCheckRollup']:
 name=r.get('name')
 if name and (name not in latest or r.get('startedAt','')>latest[name].get('startedAt','')):latest[name]=r
for name in ('branch-policy','test','docker'):
 r=latest[name];assert r['status']=='COMPLETED' and r['conclusion']=='SUCCESS',(name,r)
assert git('rev-parse','HEAD')==head
assert git('rev-parse','origin/develop')==base
assert not git('status','--porcelain'),git('status','--porcelain')
primary=Path('/root/repositories/cerberus-api')
d=json.loads((primary/'.superpowers/checkpoints/open-issues-2026-10-08.json').read_text())
for f,s in d['preservedUserFilesSha256'].items():assert hashlib.sha256((primary/f).read_bytes()).hexdigest()==s,f
print('PASS UC19 exacthead latest threeCI SUCCESS +MERGEABLE/CLEAN +freshunchangedbase +cleanbranch +fourprimarySHA')
