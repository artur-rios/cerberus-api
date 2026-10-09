from pathlib import Path
import hashlib,re,json
w=Path('/tmp/cerberus-uc29-worktree');text=Path('/tmp/cerberus-uc29-coverage.log').read_text()
rows=re.findall(r'Passed!\s+- Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+),.*?ArturRios\.Cerberus\.([A-Za-z]+)\.Tests\.dll',text)
want={'Domain':361,'Shared':125,'Query':411,'Command':1174,'Data':2662,'WebApi':2272}
assert len(rows)==6,rows
assert {r[-1] for r in rows}==set(want)
for fail,passed,skip,total,family in rows:assert (int(fail),int(skip),int(passed),int(total))==(0,0,want[family],want[family]),(family,fail,passed,skip,total)
assert not re.search(r'\[FAIL\]|\berror [A-Z]+\d+|Failed!',text)
for f,wantsha in json.loads(Path('/tmp/cerberus-uc29-precoverage-source-sha.json').read_text()).items():assert hashlib.sha256((w/f).read_bytes()).hexdigest()==wantsha,f
xmls=list((w/'tests').glob('**/TestResults/*/coverage.cobertura.xml'));assert len(xmls)==6,xmls
summary=json.loads((w/'docs/coverage-report/Summary.json').read_text());root=summary['summary'];assemblies=summary['coverage']['assemblies'];assert len(assemblies)==6,assemblies
for asm in assemblies:assert asm['coverage']>=90,asm
assert root['linecoverage']>=90,root
record={'tests':sum(want.values()),'counts':want,'lineCoverage':root['linecoverage'],'branchCoverage':root['branchcoverage'],'assemblies':[{k:x.get(k) for k in ['name','coverage','branchcoverage']} for x in assemblies],'freshXML':{str(p.relative_to(w)):hashlib.sha256(p.read_bytes()).hexdigest() for p in xmls},'sourceFilesUnchanged':len(json.loads(Path('/tmp/cerberus-uc29-precoverage-source-sha.json').read_text()))}
Path('/tmp/cerberus-uc29-coverage-evidence.json').write_text(json.dumps(record,indent=2)+'\n')
print('Fresh unfiltered6 families',record['tests'],'PASS zero fail/skip; all6assemblies>=90line; actual line',root['linecoverage'],'branch',root['branchcoverage'],'; alltested sourceSHA unchanged.')
