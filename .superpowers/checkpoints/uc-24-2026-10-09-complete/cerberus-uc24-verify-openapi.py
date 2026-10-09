from pathlib import Path
import json
w=Path('/tmp/cerberus-uc24-worktree');d=json.loads((w/'docs/contracts/openapi.json').read_text());op=d['paths']['/api/folders']['get'];schemas=d['components']['schemas']
assert set(op['responses'])=={'200','400','401','403','404','413','503'}
assert {(x['name'],x['in']) for x in op['parameters']}=={('pageSize','query'),('cursor','query'),('X-Cerberus-Vault-Access','header')}
assert 'requestBody' not in op
assert set(schemas['FolderListOutput']['properties'])=={'items','nextCursor'}
assert schemas['FolderListOutput']['properties']['items']['items']['$ref']=='#/components/schemas/FolderListItem'
assert set(schemas['FolderListItem']['properties'])=={'folderId','revision','serverSequence','editedAt','envelope'}
assert schemas['FolderListItem']['properties']['envelope']['$ref']=='#/components/schemas/EncryptedEnvelope'
assert d['paths']['/api/folders']['post']['responses']['201']
observed={'accessHeaderRequired':next(x for x in op['parameters'] if x['in']=='header').get('required',False),'itemsNullable':schemas['FolderListOutput']['properties']['items'].get('nullable',False),'itemRequiredFields':schemas['FolderListItem'].get('required',[]),'response413':op['responses']['413']}
Path('/tmp/cerberus-uc24-openapi-shape.json').write_text(json.dumps(observed,indent=2)+'\n')
print('Folder GET statuses/parameters/no-body/exactfiveitemfields/items+cursor/envelope/POSTretained PASS; observed shared requiredness/nullableitems/empty413 metadata retained for release:',json.dumps(observed))
