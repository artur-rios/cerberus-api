from pathlib import Path
import json,re,subprocess,hashlib
w=Path('/tmp/cerberus-uc28-worktree');pr=json.loads(Path('/tmp/cerberus-uc28-pr-merged.json').read_text());head=Path('/tmp/cerberus-uc28-tested-head.txt').read_text().strip();assert pr['state']=='MERGED' and pr['number']==89 and pr['headRefOid']==head
assert subprocess.check_output(['git','rev-parse','origin/develop'],cwd=w,text=True).strip()==pr['mergeCommit']['oid'];assert subprocess.check_output(['git','rev-parse','origin/develop^{tree}'],cwd=w,text=True)==subprocess.check_output(['git','rev-parse',head+'^{tree}'],cwd=w,text=True)
assert not Path('/tmp/cerberus-uc28-remote-after-merge.txt').read_text().strip()
x=json.loads(Path('/tmp/cerberus-uc28-issue-after-merge.json').read_text());assert x['number']==29 and x['state'] in ('OPEN','CLOSED');body=x['body'];assert len(re.findall(r'^- \[[ x]\]',body,re.M))==9
Path('/tmp/cerberus-uc28-issue-done-body.md').write_text(body.replace('- [ ]','- [x]'))
assert '- [ ]' not in Path('/tmp/cerberus-uc28-issue-done-body.md').read_text()
print('Actualmerged89/testedtree/remotebranchabsent; prepared allnineUC28DoD checkboxes; currentissue',x['state'])
