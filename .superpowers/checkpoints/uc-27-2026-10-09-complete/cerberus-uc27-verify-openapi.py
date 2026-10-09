from pathlib import Path
import json
w=Path('/tmp/cerberus-uc27-worktree');d=json.loads((w/'docs/contracts/openapi.json').read_text());op=d['paths']['/api/folders/{id}']['delete'];schemas=d['components']['schemas']
assert set(op['responses'])=={'200','400','401','403','404','409','413','503'}
assert {(x['name'],x['in']) for x in op['parameters']}=={('id','path'),('X-Cerberus-Vault-Access','header')}
assert op['requestBody']['content']['application/json']['schema']['$ref']=='#/components/schemas/DeleteFolderCommand'
assert set(schemas['DeleteFolderCommand']['properties'])=={'expectedRevision'}
assert set(schemas['DeleteFolderOutput']['properties'])=={'folderId','trashOperationId','revision','serverSequence','deletedAt','purgeAt'}
assert d['paths']['/api/folders']['post']['responses']['201'] and d['paths']['/api/folders']['get']['responses']['200'] and d['paths']['/api/folders/{id}']['get']['responses']['200']
assert d['paths']['/api/folders/{id}']['put']['responses']['200']
observed={'accessHeaderRequired':next(x for x in op['parameters'] if x['in']=='header').get('required',False),'requestBodyRequired':op['requestBody'].get('required',False),'inputRequiredFields':schemas['DeleteFolderCommand'].get('required',[]),'outputRequiredFields':schemas['DeleteFolderOutput'].get('required',[]),'response413':op['responses']['413']}
Path('/tmp/cerberus-uc27-openapi-shape.json').write_text(json.dumps(observed,indent=2)+'\n');print('Folder DELETE exactoneinput/sixoutput/statuses/parameters/POST+list+detail+PUT retained PASS; shared release metadata:',json.dumps(observed))
