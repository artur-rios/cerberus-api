from pathlib import Path
import re
for stage,count in [('green',127),('whole',2098)]:
 p=Path(f'/tmp/cerberus-uc26-data-{stage}.log');s=p.read_text();rows=re.findall(r'Failed:\s+(\d+), Passed:\s+(\d+), Skipped:\s+(\d+), Total:\s+(\d+)',s);assert rows==[('0',str(count),'0',str(count))];assert 'error CS' not in s;print(p.name,count,'PASS zero fail/skip')
