from pathlib import Path
import re
w=Path('/tmp/cerberus-uc25-worktree');log=Path('/tmp/cerberus-uc25-data-red.log').read_text();assert 'error CS0246' in log and 'FolderReadStore' in log
s=(w/'src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordReadStore.cs').read_text()
s=s.replace('Record','Folder').replace('record_id','folder_id').replace('cerberus.record r','cerberus.folder r').replace("'record'","'folder'")
s=re.sub(r'\br\.folder_id\b','r.parent_folder_id',s)
old='''                    SELECT r.id folder_id,r.account_id,f.id,f.parent_folder_id,ARRAY[f.id] path,false cycle,
                        (f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='folder' AND e.resource_id=f.public_id))) hidden
                    FROM candidates r JOIN cerberus.folder f ON f.id=r.parent_folder_id AND f.account_id=r.account_id'''
new='''                    SELECT r.id folder_id,r.account_id,r.id,r.parent_folder_id,ARRAY[r.id] path,false cycle,
                        (r.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='folder' AND e.resource_id=r.public_id))) hidden
                    FROM candidates r'''
assert old in s;s=s.replace(old,new)
old='''                    WHERE EXISTS(SELECT FROM cerberus.collection_record cr WHERE cr.collection_id=c.id AND cr.folder_id=r.id AND cr.account_id=r.account_id)
                       OR EXISTS(SELECT FROM cerberus.collection_folder cf JOIN ancestry t ON t.id=cf.folder_id'''
new='''                    WHERE EXISTS(SELECT FROM cerberus.collection_folder cf JOIN ancestry t ON t.id=cf.folder_id'''
assert old in s;s=s.replace(old,new)
old='''                        OR EXISTS(SELECT FROM cerberus.profile_record pr WHERE pr.profile_id=s.profile_id AND pr.account_id=s.account_id AND pr.folder_id=r.id)
                        OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN ancestry t ON t.id=pf.folder_id'''
new='''                        OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN ancestry t ON t.id=pf.folder_id'''
assert old in s;s=s.replace(old,new)
s=s.replace('(r.parent_folder_id IS NULL OR EXISTS(SELECT FROM ancestry t WHERE t.folder_id=r.id AND t.parent_folder_id IS NULL))','EXISTS(SELECT FROM ancestry t WHERE t.folder_id=r.id AND t.parent_folder_id IS NULL)')
s=s.replace("'recordId'","'folderId'").replace('cerberus.profile_record pr','cerberus.profile_folder pr')
s=s.replace("'folderId',CASE WHEN r.parent_folder_id","'parentFolderId',CASE WHEN r.parent_folder_id")
s=s.replace('WHERE pf.account_id=s.account_id AND pf.profile_id=s.profile_id AND t.folder_id=r.id)))','WHERE pf.account_id=s.account_id AND pf.profile_id=s.profile_id AND t.folder_id=r.id AND t.id<>r.id)))')
s=s.replace('JOIN ancestry t ON t.id=cf.folder_id WHERE i.folder_id=r.id AND t.folder_id=r.id))','JOIN ancestry t ON t.id=cf.folder_id WHERE i.folder_id=r.id AND t.folder_id=r.id AND t.id<>r.id))')
s=s.replace('item.ProfileIds, item.FolderId, item.CollectionIds','item.ProfileIds, item.ParentFolderId, item.CollectionIds')
s=s.replace('Guid[] ProfileIds, Guid? FolderId, Guid[] CollectionIds','Guid[] ProfileIds, Guid? ParentFolderId, Guid[] CollectionIds')
assert 'profile_record' not in s and 'collection_record' not in s and "'recordId'" not in s
assert 't.id<>r.id' in s and 'SELECT r.id folder_id,r.account_id,r.id' in s
assert "'parentFolderId',CASE" in s and 'item.ParentFolderId' in s
p=w/'src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderReadStore.cs';assert not p.exists();p.write_text(s)
l=w/'.superpowers/sdd/2026-10-09-uc-25-get-folder/progress.md';l.write_text(l.read_text()+'\nTask2 actual installed PostgreSQL matrix produced missing FolderReadStore CS0246 RED data-red.log BEFORE store. Implemented bounded public-ID candidate plus complete self/upward ancestry, current authority and visible refs/native evidence in one SQL statement; parent routes explicitly exclude target itself. No writes/locks/direct-record folder scope/new dependencies. Focused GREEN next.\n');print('Target-first folder read store implemented after genuine RED')
