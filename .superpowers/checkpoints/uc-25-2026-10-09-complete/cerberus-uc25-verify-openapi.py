from pathlib import Path
import json
w=Path('/tmp/cerberus-uc25-worktree');d=json.loads((w/'docs/contracts/openapi.json').read_text());op=d['paths']['/api/folders/{id}']['get'];schemas=d['components']['schemas']
assert set(op['responses'])=={'200','400','401','403','404','413','503'}
assert {(x['name'],x['in']) for x in op['parameters']}=={('id','path'),('X-Cerberus-Vault-Access','header')};assert 'requestBody' not in op
assert set(schemas['FolderDetailsOutput']['properties'])=={'folderId','revision','serverSequence','editedAt','envelope','profileIds','parentFolderId','collectionIds'}
assert schemas['FolderDetailsOutput']['properties']['envelope']['$ref']=='#/components/schemas/EncryptedEnvelope'
assert d['paths']['/api/folders']['post']['responses']['201'] and d['paths']['/api/folders']['get']['responses']['200']
observed={'accessHeaderRequired':next(x for x in op['parameters'] if x['in']=='header').get('required',False),'detailRequiredFields':schemas['FolderDetailsOutput'].get('required',[]),'profileIdsNullable':schemas['FolderDetailsOutput']['properties']['profileIds'].get('nullable',False),'collectionIdsNullable':schemas['FolderDetailsOutput']['properties']['collectionIds'].get('nullable',False),'response413':op['responses']['413']}
Path('/tmp/cerberus-uc25-openapi-shape.json').write_text(json.dumps(observed,indent=2)+'\n');print('Folder detail exacteightfields/statuses/parameters/no-body/envelope/POST+list retained PASS; observed shared release metadata:',json.dumps(observed))
