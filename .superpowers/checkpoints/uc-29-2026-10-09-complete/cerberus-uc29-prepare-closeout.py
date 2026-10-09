from pathlib import Path
import json, subprocess, hashlib

root = Path('/root/repositories/cerberus-api')
w = Path('/tmp/cerberus-uc29-worktree')
prefix = Path('/tmp')
def read(name): return json.loads((prefix / ('cerberus-uc29-' + name)).read_text())
def git(*args): return subprocess.check_output(['git', *args], cwd=w, text=True).strip()
head = (prefix / 'cerberus-uc29-tested-head.txt').read_text().strip()
pr = read('pr-merged.json')
assert pr['number'] == 90 and pr['state'] == 'MERGED' and pr['headRefOid'] == head
merge = pr['mergeCommit']['oid']
assert git('rev-parse', 'origin/develop') == merge
assert git('rev-parse', 'origin/develop^{tree}') == git('rev-parse', head + '^{tree}')
assert not git('status', '--porcelain')
assert not (prefix / 'cerberus-uc29-remote-after-merge.txt').read_text().strip()
issue = read('issue-after-merge.json')
assert issue['number'] == 30 and issue['state'] == 'CLOSED'
body = issue['body']
assert body.count('- [ ]') == 9 and '## Definition of Done' in body
assert body.split('## Definition of Done')[0].count('- [ ]') == 0
(prefix / 'cerberus-uc29-issue-done-body.md').write_text(body.replace('- [ ]', '- [x]'))
project = read('project-after-merge.json')['data']['repository']['issue']
assert project['number'] == 30 and project['state'] == 'CLOSED'
items = [x for x in project['projectItems']['nodes'] if x['project']['id'] == 'PVT_kwHOAgOUtM4BmFmu']
assert len(items) == 1 and items[0]['id'] == 'PVTI_lAHOAgOUtM4BmFmuzg_LyfU'
payload = read('testing-input.json')
assert payload['variables']['item'] == items[0]['id']
payload['variables']['option'] = '98236657'
(prefix / 'cerberus-uc29-done-input.json').write_text(json.dumps(payload) + '\n')
readme = subprocess.check_output(['git', 'show', 'origin/develop:README.md'], cwd=w, text=True)
assert '| 21 / 26 closed |' in readme
assert any('UC-29' in line and line.endswith('| Done |') for line in readme.splitlines())
for f, sha in read('gate1.json')['primarySha'].items():
    assert hashlib.sha256((root / f).read_bytes()).hexdigest() == sha, f
print('Actual merge, tested tree, issue closure, remote branch deletion, README and user files verified; nine DoD body and actual project Done transition prepared.')
