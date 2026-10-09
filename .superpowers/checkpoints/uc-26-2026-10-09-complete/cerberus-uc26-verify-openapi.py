from pathlib import Path
import json
w=Path('/tmp/cerberus-uc26-worktree');d=json.loads((w/'docs/contracts/openapi.json').read_text());op=d['paths']['/api/folders/{id}']['put'];schemas=d['components']['schemas']
assert set(op['responses'])=={'200','400','401','403','404','409','413','503'}
assert {(x['name'],x['in']) for x in op['parameters']}=={('id','path'),('X-Cerberus-Vault-Access','header')}
assert op['requestBody']['content']['application/json']['schema']['$ref']=='#/components/schemas/UpdateFolderCommand'
assert set(schemas['UpdateFolderCommand']['properties'])=={'expectedRevision','envelope','editedAt'}
assert set(schemas['UpdateFolderOutput']['properties'])=={'folderId','revision','serverSequence','editedAt'}
assert schemas['UpdateFolderCommand']['properties']['envelope']['$ref']=='#/components/schemas/EncryptedEnvelope'
assert d['paths']['/api/folders']['post']['responses']['201'] and d['paths']['/api/folders']['get']['responses']['200'] and d['paths']['/api/folders/{id}']['get']['responses']['200']
observed={'accessHeaderRequired':next(x for x in op['parameters'] if x['in']=='header').get('required',False),'requestBodyRequired':op['requestBody'].get('required',False),'inputRequiredFields':schemas['UpdateFolderCommand'].get('required',[]),'outputRequiredFields':schemas['UpdateFolderOutput'].get('required',[]),'response413':op['responses']['413']}
Path('/tmp/cerberus-uc26-openapi-shape.json').write_text(json.dumps(observed,indent=2)+'\n');print('Folder PUT exactthreeinput/fouroutput/statuses/parameters/nativeenvelope/POST+list+detail retained PASS; shared release metadata:',json.dumps(observed))
