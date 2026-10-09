from pathlib import Path
import re,subprocess,hashlib,json
w=Path('/tmp/cerberus-uc28-worktree');red=Path('/tmp/cerberus-uc28-review-red.log').read_text();green=Path('/tmp/cerberus-uc28-review-green.log').read_text()
assert 'Expected: OK' in red and 'Actual:   BadRequest' in red
assert re.search(r'Failed!\s+- Failed:\s+1, Passed:\s+0, Skipped:\s+0, Total:\s+1',red)
assert re.search(r'Passed!\s+- Failed:\s+0, Passed:\s+149, Skipped:\s+0, Total:\s+149',green)
assert not re.search(r'\[FAIL\]|\berror [A-Z]+\d+|Failed!',green)
f='tests/Presentation/ArturRios.Cerberus.WebApi.Tests/FolderMoveHttpTests.cs';s=(w/f).read_text();assert 'parentParentFolderId' not in s and 'GivenUnmodifiedStrictBodyFixture_WhenMoving_ThenAcceptValidBodyAndReturnExactMetadata' in s
attrs=s.split('public async Task GivenStrictOrForgedMoveBody',1)[0].rsplit('[FunctionalTheory]',1)[1];assert len(re.findall(r'\[InlineData\(',attrs))==38
assert subprocess.check_output(['git','diff','--name-only','HEAD'],cwd=w,text=True).splitlines()==[f]
for name,sha in json.loads(Path('/tmp/cerberus-uc28-final-tested-files-sha.json').read_text()).items():assert hashlib.sha256((w/name).read_bytes()).hexdigest()==sha,name
print('Importantfix genuinecontrol1RED400vs200→focused149GREEN0fail0skip; all38negativecasesretained; onlytestfixturechanged/21SHAunchanged.')
