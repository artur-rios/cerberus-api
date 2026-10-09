import json
s=json.load(open('/tmp/cerberus-uc13-ci-final.json'))
assert s['state']=='OPEN',s['state']
assert s['headRefOid']=='0647dc566c4a8cdaea54f15b0d07aa0c5659107d',s['headRefOid']
assert s['mergeable']=='MERGEABLE',s['mergeable']
assert s['mergeStateStatus']=='CLEAN',s['mergeStateStatus']
for name in ['branch-policy','test','docker']:
    checks=[c for c in s['statusCheckRollup'] if c.get('name')==name]
    assert checks,name
    c=max(checks,key=lambda c:c['startedAt'])
    assert c['status']=='COMPLETED' and c['conclusion']=='SUCCESS',(name,c)
print('PASS fresh exact UC13 head, MERGEABLE, CLEAN, latest three required checks SUCCESS')
