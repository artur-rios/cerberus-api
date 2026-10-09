from pathlib import Path
import json,subprocess
w=Path('/tmp/cerberus-uc29-worktree');d=json.loads((w/'docs/contracts/openapi.json').read_text());op=d['paths']['/api/collections']['post'];s=d['components']['schemas']
assert set(d['paths']['/api/collections'])=={'post'}
assert set(op['responses'])=={'201','400','401','403','404','409','413','503'}
assert {(x['name'],x['in']) for x in op['parameters']}=={('X-Cerberus-Vault-Access','header')}
assert op['requestBody']['content']['application/json']['schema']['$ref']=='#/components/schemas/CreateCollectionCommand'
assert set(s['CreateCollectionCommand']['properties'])=={'collectionId','envelope','editedAt','profileIds','recordIds','folderIds'}
assert set(s['CreateCollectionCommand'].get('required',[]))=={'collectionId','envelope','editedAt','profileIds','recordIds','folderIds'}
assert set(s['CreateCollectionOutput']['properties'])=={'collectionId','revision','serverSequence','editedAt'}
baseline=json.loads(subprocess.check_output(['git','show','886cf84fc8deb44388f2a804de3f79197276f81a:docs/contracts/openapi.json'],cwd=w,text=True))
assert set(d['paths'])==set(baseline['paths'])|{'/api/collections'}
for path,operations in baseline['paths'].items():
 assert d['paths'][path]==operations,path
observed={'accessHeaderRequired':op['parameters'][0].get('required',False),'requestBodyRequired':op['requestBody'].get('required',False),'inputRequiredFields':s['CreateCollectionCommand'].get('required',[]),'outputRequiredFields':s['CreateCollectionOutput'].get('required',[]),'response413':op['responses']['413']}
Path('/tmp/cerberus-uc29-openapi-shape.json').write_text(json.dumps(observed,indent=2)+'\n')
print('POSTcollection exact required6input/4output/8statuses/all existingoperations unchanged PASS; inherited shared release metadata:',json.dumps(observed))
