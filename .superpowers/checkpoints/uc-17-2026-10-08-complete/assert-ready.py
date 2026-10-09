import json,sys
s=json.load(open('/tmp/cerberus-uc17-ci-final.json'))
assert s['state']=='OPEN',s['state']
assert s['headRefOid']==sys.argv[1],s['headRefOid']
assert s['baseRefName']=='develop',s['baseRefName']
assert s['mergeable']=='MERGEABLE',s['mergeable']
assert s['mergeStateStatus']=='CLEAN',s['mergeStateStatus']
for name in ['branch-policy','test','docker']:
    checks=[c for c in s['statusCheckRollup'] if c.get('name')==name]
    assert checks,name
    c=max(checks,key=lambda c:c['startedAt'])
    assert c['status']=='COMPLETED' and c['conclusion']=='SUCCESS',(name,c)
print('PASS fresh exact UC17 head, develop base, MERGEABLE, CLEAN, latest three required checks SUCCESS')
