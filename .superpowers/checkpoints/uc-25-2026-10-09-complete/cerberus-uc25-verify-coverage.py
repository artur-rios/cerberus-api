from pathlib import Path
import json,re
w=Path('/tmp/cerberus-uc25-worktree');log=Path('/tmp/cerberus-uc25-coverage.log');t=log.read_text()
a=re.findall(r'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+).*? - (ArturRios.Cerberus.\w+.Tests.dll)',t)
expected={'Domain':361,'Query':411,'Shared':125,'Command':841,'Data':1971,'WebApi':1765}
assert len(a)==6,(len(a),a)
actual={name.removeprefix('ArturRios.Cerberus.').removesuffix('.Tests.dll'):int(p) for f,p,s,n,name in a};assert actual==expected,(actual,expected)
assert all(int(f)==0 and int(s)==0 and p==n for f,p,s,n,name in a)
summary=json.loads((w/'docs/coverage-report/Summary.json').read_text());assert summary['summary']['assemblies']==6 and len(summary['coverage']['assemblies'])==6
assert all(x['coverage']>=90 for x in summary['coverage']['assemblies'])
result={'tests':sum(actual.values()),'families':actual,'line':summary['summary']['linecoverage'],'branch':summary['summary']['branchcoverage'],'assemblies':[{'name':x['name'],'line':x['coverage'],'branch':x['branchcoverage']} for x in summary['coverage']['assemblies']],'log':str(log)}
assert result['tests']==5474
Path('/tmp/cerberus-uc25-validation.json').write_text(json.dumps(result,indent=2)+'\n');print(json.dumps(result,indent=2))
