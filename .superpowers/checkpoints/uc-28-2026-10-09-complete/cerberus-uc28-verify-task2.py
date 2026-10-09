from pathlib import Path
import hashlib,json,re
for path,want in json.loads(Path('/tmp/cerberus-uc28-data-tested-sha.json').read_text()).items():
 assert hashlib.sha256(Path(path).read_bytes()).hexdigest()==want,path
for name,count in [('data-green',211),('data-whole',2456)]:
 s=Path('/tmp/cerberus-uc28-'+name+'.log').read_text()
 assert re.search(r'Passed!\s+- Failed:\s+0, Passed:\s+'+str(count)+r', Skipped:\s+0, Total:\s+'+str(count),s),(name,s[-2000:])
 assert not re.search(r'\berror [A-Z]+\d+|\[FAIL\]',s),name
print('focused211/wholeData2456 PASS zero fail/skip; exact tested store/test SHA preserved')
