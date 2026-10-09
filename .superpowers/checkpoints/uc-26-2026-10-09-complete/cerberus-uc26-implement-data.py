from pathlib import Path
import re
w=Path('/tmp/cerberus-uc26-worktree');p=w/'src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderUpdateStore.cs';assert not p.exists()
red=Path('/tmp/cerberus-uc26-data-red.log').read_text();assert 'error CS0246' in red and 'FolderUpdateStore' in red and 'error CS0105' not in red
s=(w/'src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordUpdateStore.cs').read_text().replace('Record','Folder').replace('recordId','folderId').replace('record_id','folder_id').replace('cerberus.record ','cerberus.folder ').replace("resource_kind='record'","resource_kind='folder'")
s=s.replace('// Do not lock foreign accounts/profiles or folders. Existing create locks folders\n            // leaf-to-root; complete current ancestry is instead recomputed INSIDE the write.', '// Do not lock foreign accounts/profiles or ancestors. Only target folder is\n            // locked; existing create walks leaf-to-root, so acquire no upward locks.\n            // Complete current ancestry is recomputed INSIDE the guarded write.')
a=s.index('                            SELECT r.id folder_id,r.account_id,f.id,f.parent_folder_id,ARRAY[f.id] path,false cycle,')
b=s.index('                            UNION ALL',a)
s=s[:a]+'''                            SELECT r.id folder_id,r.account_id,r.id,r.parent_folder_id,ARRAY[r.id] path,false cycle,
                                (r.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='folder' AND e.resource_id=r.public_id))) hidden
                            FROM candidates r
'''+s[b:]
a=s.index('                            WHERE EXISTS(SELECT FROM cerberus.collection_record ');b=s.index('EXISTS(SELECT FROM cerberus.collection_folder ',a)
s=s[:a]+'                            WHERE '+s[b:]
a=s.index('                                OR EXISTS(SELECT FROM cerberus.profile_record ');b=s.index('                                OR EXISTS(SELECT FROM cerberus.profile_folder ',a)
s=s[:a]+s[b:]
s=s.replace('AND (r.folder_id IS NULL OR EXISTS(SELECT FROM ancestry t WHERE t.folder_id=r.id AND t.parent_folder_id IS NULL))','AND EXISTS(SELECT FROM ancestry t WHERE t.folder_id=r.id AND t.parent_folder_id IS NULL)')
assert 'cerberus.record' not in s and 'collection_record' not in s and 'profile_record' not in s and 'r.folder_id' not in s
assert 'public sealed record Item' not in s # Nested record has private access.
assert 'private sealed record Item(Guid FolderId' in s and 'db.Folders' in s
p.write_text(s)
l=w/'.superpowers/sdd/2026-10-09-uc-26-update-folder/progress.md';l.write_text(l.read_text()+'Task2 genuine RED: actual named focused tests exit1 only CS0246 missing FolderUpdateStore, data-red.log BEFORE product store. Adapted record-update transaction with precise folder-only authority changes: target+self ancestry/required null-parent terminus, no CR/PR routes, only target folder lock and no upward/foreign account/profile locks; full guarded current UPDATE. Fixtures include true containing-record access before folder denial, actual FolderCreateStore target-lock interleaving, complete native rotation inventory still containing record+folder.\n')
print('Implemented FolderUpdateStore after genuine missing-store RED; no record authority routes.')
