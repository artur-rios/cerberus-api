from pathlib import Path
import subprocess,json
subprocess.run(['python3','/tmp/cerberus-uc29-verify-coverage.py'],check=True)
for name in ['gate1','gate2','precoverage-evidence']:
 d=json.loads(Path('/tmp/cerberus-uc29-'+name+'.json').read_text());assert d['passed'],name
subprocess.run(['python3','/tmp/cerberus-uc29-verify-openapi.py'],check=True)
print('Task3 actual fresh full-suite7005/7005 plus all required contract/audit/spec gates verified; sourceSHA unchanged.')
