from pathlib import Path
import json
w=Path('/tmp/cerberus-uc28-worktree');d=json.loads((w/'docs/contracts/openapi.json').read_text());op=d['paths']['/api/folders/{id}/parent']['put'];s=d['components']['schemas']
assert set(op['responses'])=={'200','400','401','403','404','409','413','503'}
assert {(x['name'],x['in']) for x in op['parameters']}=={('id','path'),('X-Cerberus-Vault-Access','header')}
assert op['requestBody']['content']['application/json']['schema']['$ref']=='#/components/schemas/MoveFolderCommand'
assert set(s['MoveFolderCommand']['properties'])=={'expectedRevision','parentFolderId'}
assert set(s['MoveFolderCommand'].get('required',[]))=={'expectedRevision','parentFolderId'}
assert set(s['MoveFolderOutput']['properties'])=={'folderId','parentFolderId','revision','serverSequence'}
for name in ['MoveFolderCommand','MoveFolderOutput']:
 parent=s[name]['properties']['parentFolderId']
 assert 'null' in parent.get('type',[]) or parent.get('nullable') is True or any(x.get('type')=='null' for x in parent.get('anyOf',[])),(name,parent)
assert {'post','get'}<=set(d['paths']['/api/folders'])
assert {'get','put','delete'}<=set(d['paths']['/api/folders/{id}'])
assert '/api/records/{id}/folder' in d['paths']
observed={'accessHeaderRequired':next(x for x in op['parameters'] if x['in']=='header').get('required',False),'requestBodyRequired':op['requestBody'].get('required',False),'inputRequiredFields':s['MoveFolderCommand'].get('required',[]),'outputRequiredFields':s['MoveFolderOutput'].get('required',[]),'parentInput':s['MoveFolderCommand']['properties']['parentFolderId'],'parentOutput':s['MoveFolderOutput']['properties']['parentFolderId'],'response413':op['responses']['413']}
Path('/tmp/cerberus-uc28-openapi-shape.json').write_text(json.dumps(observed,indent=2)+'\n')
print('PUTparent exact required2input/4output/nullableparent/statuses/retainedfolder+recordroutes PASS; inherited shared release metadata:',json.dumps(observed))
