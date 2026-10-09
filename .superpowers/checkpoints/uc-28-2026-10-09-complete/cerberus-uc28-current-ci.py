from pathlib import Path
import json,subprocess
pr=int(Path('/tmp/cerberus-uc28-pr-url.txt').read_text().strip().rsplit('/',1)[1]);cmd=['gh','pr','view',str(pr),'--repo','artur-rios/cerberus-api','--json','number,url,state,baseRefName,headRefOid,mergeable,mergeStateStatus,statusCheckRollup,body'];d=json.loads(subprocess.check_output(cmd,text=True));assert d['headRefOid']==Path('/tmp/cerberus-uc28-tested-head.txt').read_text().strip();Path('/tmp/cerberus-uc28-pr-progress.json').write_text(json.dumps(d,indent=2)+'\n');latest={}
for x in d['statusCheckRollup']:
 n=x.get('name')
 if n not in latest or x.get('startedAt','')>=latest[n].get('startedAt',''):latest[n]=x
print(json.dumps({'pr':pr,'state':d['state'],'mergeable':d['mergeable'],'mergeState':d['mergeStateStatus'],'checks':{n:{'status':x['status'],'conclusion':x['conclusion'],'url':x.get('detailsUrl')} for n,x in latest.items()}},indent=2))
if any(x['status']=='COMPLETED' and x['conclusion'] not in ('SUCCESS','SKIPPED') for x in latest.values()):raise SystemExit(2)
