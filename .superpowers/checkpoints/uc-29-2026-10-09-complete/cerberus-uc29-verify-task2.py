from pathlib import Path
import hashlib,json,re,subprocess
root=Path('/tmp/cerberus-uc29-worktree')
evidence=json.loads(Path('/tmp/cerberus-uc29-task2-focused-evidence.json').read_text())
for name,want in evidence['files'].items():
 assert hashlib.sha256((root/name).read_bytes()).hexdigest()==want,name
for path,count in [('/tmp/cerberus-uc29-data-green.log',206),('/tmp/cerberus-uc29-data-whole.log',2662)]:
 text=Path(path).read_text()
 assert re.search(r'Passed!\s+- Failed:\s+0, Passed:\s+'+str(count)+r', Skipped:\s+0, Total:\s+'+str(count)+r',',text),path
 assert '[FAIL]' not in text and 'error ' not in text,path
print('Fresh unfiltered Data family PASS2662; collection focus PASS206; exact tested store/test SHA unchanged.')
