from pathlib import Path
import re
p=Path('/tmp/cerberus-uc27-command-whole.log');s=p.read_text();assert re.search(r'Failed:\s+0, Passed:\s+987, Skipped:\s+0, Total:\s+987',s);print('cerberus-uc27-command-whole.log 987 PASS zero fail/skip')
