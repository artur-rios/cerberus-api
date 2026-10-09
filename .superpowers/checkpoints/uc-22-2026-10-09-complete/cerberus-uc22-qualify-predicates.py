from pathlib import Path
import re
root=Path('/tmp/cerberus-uc22-worktree/src/Infrastructure/ArturRios.Cerberus.Data')
updates={}
for p in root.rglob('*.cs'):
 if 'Migrations' in p.parts or p.name in ('TerminalErasureStore.cs','AppDbContext.cs'):continue
 s=p.read_text()
 if 'TerminalErasures' not in s and 'terminal_erasure' not in s:continue
 lines=[];kind=None
 for line in s.splitlines(keepends=True):
  if p.name=='ProfileAssociationVisibility.cs':
   for name,k in [('VaultRecord','record'),('VaultFolder','folder'),('VaultCollection','collection')]:
    if f'IQueryable<{name}>' in line:kind=k
  def linq(m):
   alias,ref=m.groups();base=ref.split('.')[0]
   mapping={'account':'account','a':'account','accountId':'account','profile':'profile','p':'profile','folder':'folder','c':'collection','g':'grant','input.ProfileId':'profile','input.RecordId':'record'}
   if ref=='x.PublicId':k='account' if p.name=='AccountAuthenticationStore.cs' else kind
   else:k=mapping.get(ref,mapping.get(base))
   assert k,(str(p),line,ref)
   return f'({alias}.ResourceKind == "{k}" && {alias}.ResourceId == {ref})'
  if 'TerminalErasures' in line:
   line=re.sub(r'\b([ex])\.ResourceId\s*==\s*([A-Za-z_][A-Za-z_0-9.]*)',linq,line)
  def sql(m):
   ref=m.group(1);alias=ref.split('.')[0]
   mapping={'a':'account','o':'account','owner':'account','recipient':'account','p':'profile','r':'record','f':'folder','c':'collection','g':'grant'}
   k='record' if ref=='{input.RecordId}' else ref.split('.')[1] if ref.startswith('cerberus.') else mapping.get(alias)
   assert k,(str(p),line,ref)
   return f"(e.resource_kind='{k}' AND e.resource_id={ref})"
  line=re.sub(r'\be\.resource_id\s*=\s*([a-z._]+public_id|\{input.RecordId\})',sql,line)
  lines.append(line)
 updates[p]=''.join(lines)
for p,s in updates.items():p.write_text(s)
print(f'Qualified typed predicates in {len(updates)} files; manually inspect compound ORs.')

testroot=Path('/tmp/cerberus-uc22-worktree/tests/Infrastructure/ArturRios.Cerberus.Data.Tests')
for name in ('RecordReadStoreTests.cs','RecordListStoreTests.cs','ProfileAssociationIntegrationTests.cs'):
 p=testroot/name;s=p.read_text()
 if name.startswith('Record'):
  s=s.replace('ResourceKind="fixture"','ResourceKind=kind=="grantTerminal"?"grant":kind=="collectionTerminal"?"collection":kind=="ownerTerminal"?"account":kind=="recordTerminal"?"record":kind=="profileTerminal"?"profile":"folder"',1)
  if name=='RecordReadStoreTests.cs':s=s.replace('ResourceKind="fixture"','ResourceKind=kind.StartsWith("profile")?"profile":"collection"')
 else:s=s.replace('ResourceKind="fixture"','ResourceKind=kind=="recordErased"?"record":kind=="folderErased"?"folder":kind is "ownCollectionErased" or "sharedErased"?"collection":kind=="grantErased"?"grant":"account"')
 p.write_text(s)
