from pathlib import Path
import json,re
r=Path('/tmp/cerberus-uc20-worktree');log=Path('/tmp/cerberus-uc20-coverage.log').read_text();a=re.findall(r'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)',log)
assert len(a)==6 and sum(int(x[1]) for x in a)==3878 and all(int(f)==0 and int(s)==0 and int(p)==int(t) for f,p,s,t in a)
x=json.loads((r/'docs/coverage-report/Summary.json').read_text());assert x['summary']['linecoverage']>=90
assemblies=x['coverage']['assemblies'];assert len(assemblies)==6
for v in assemblies: assert v['coverage']>=90,v
p=r/'docs/superpowers/plans/2026-10-09-uc-20-delete-record.md';s=p.read_text();i=s.index('### Task 3:');s=s[:i]+s[i:].replace('- [ ]','- [x]');p.write_text(s)
p=r/'.superpowers/sdd/2026-10-09-uc-20-delete-record/progress.md';p.write_text(p.read_text()+f'\nTask3 fresh unfiltered sixfamily3878PASS zero fail-skip; all6productionassemblies>=90line, merged{x["summary"]["linecoverage"]}%line/{x["summary"]["branchcoverage"]}%branch. Freshlockedrestore/NuGetclean/nativeOSV153/corpus322/36nativehelpers/23Python/spec107FR55UC29BR/OpenAPIgenerated+drift+shape/diff PASS. Task3 allExpected compared, no correction afterrouteRED; commit/taskdone then mandatory globalreview/PR/CI/integrationDoD still pending.\n')
print(x['summary']);print([(v['name'],v['coverage']) for v in assemblies])
