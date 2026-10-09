from pathlib import Path
import re,json,hashlib
w=Path('/tmp/cerberus-uc28-worktree');text=Path('/tmp/cerberus-uc28-coverage.log').read_text()
rows=re.findall(r'Passed!\s+- Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+),.*?ArturRios\.Cerberus\.([A-Za-z]+)\.Tests\.dll',text)
want={'Domain':361,'Shared':125,'Query':411,'Command':1076,'Data':2456,'WebApi':2129}
assert len(rows)==6,rows
for fail,passed,skip,total,family in rows:assert (int(fail),int(skip),int(passed),int(total))==(0,0,want[family],want[family]),(family,fail,passed,skip,total)
assert not re.search(r'\[FAIL\]|\berror [A-Z]+\d+|Failed!',text)
summary=json.loads((w/'docs/coverage-report/Summary.json').read_text());root=summary['summary'];assemblies=summary['coverage']['assemblies']
assert len(assemblies)==6,assemblies
for asm in assemblies:assert asm['coverage']>=90,asm
assert root['linecoverage']>=90,root
record={'tests':sum(want.values()),'counts':want,'lineCoverage':root['linecoverage'],'branchCoverage':root['branchcoverage'],'assemblies':[{k:x.get(k) for k in ['name','coverage','branchcoverage']} for x in assemblies]}
Path('/tmp/cerberus-uc28-coverage-evidence.json').write_text(json.dumps(record,indent=2)+'\n')
print('Fresh unfiltered6 families',record['tests'],'PASS zero fail/skip; all6assemblies>=90line; actual line',root['linecoverage'],'branch',root['branchcoverage'])
