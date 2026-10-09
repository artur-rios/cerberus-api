from pathlib import Path
import re
for log,n in [('data-green',147),('data-whole',2245)]:
 s=Path('/tmp/cerberus-uc27-'+log+'.log').read_text();assert re.search(rf'Failed:\s+0, Passed:\s+{n}, Skipped:\s+0, Total:\s+{n}',s);print(log,n,'PASS zero fail/skip')
