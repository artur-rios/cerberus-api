from pathlib import Path
w=Path('/tmp/cerberus-uc25-worktree');s=(w/'tests/Presentation/ArturRios.Cerberus.WebApi.Tests/RecordReadHttpTests.cs').read_text()
s=s.replace('result.FolderId','result.ParentFolderId').replace('item.FolderId','item.ParentFolderId')
s=s.replace('Fields(result,"recordId","revision","serverSequence","editedAt","envelope","profileIds","folderId","collectionIds")','Fields(result,"folderId","revision","serverSequence","editedAt","envelope","profileIds","parentFolderId","collectionIds")')
s=s.replace('Record','Folder').replace('/api/records','/api/folders').replace('db.Records','db.Folders').replace('db.CollectionRecords','db.CollectionFolders')
s=s.replace('r.FolderId,newParent.Id','r.ParentFolderId,newParent.Id')
s=s.replace('f.FolderId,newParent.Id','f.ParentFolderId,newParent.Id')
s=s.replace('record_found','folder_found').replace('collection_record','collection_folder')
# Move-folder detail freshness uses the actual parent column.
s=s.replace('r.FolderId,outside.Id','r.ParentFolderId,outside.Id').replace('r.FolderId,','r.ParentFolderId,')
# Keep the real cross-resource persisted snapshot broad and skip only the renamed required folder-membership table.
start=s.index('    private async Task<string> Snapshot(');end=s.index('    private static async Task<T> Success',start)
original=(w/'tests/Presentation/ArturRios.Cerberus.WebApi.Tests/RecordReadHttpTests.cs').read_text();snap=original[original.index('    private async Task<string> Snapshot('):original.index('    private static async Task<T> Success')]
snap=snap.replace('Members=skipMembers?[]:await db.CollectionRecords','Members=await db.CollectionRecords').replace('CollectionFolders=await db.CollectionFolders','CollectionFolders=skipMembers?[]:await db.CollectionFolders')
snap=snap.replace('ProfileRecords=await db.ProfileRecords','ProfileFolders=await db.ProfileFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileRecords=await db.ProfileRecords')
s=s[:start]+snap+s[end:]
assert 'private sealed record State' in s and 'FolderCreateInput' in s and 'parentFolderId' in s
assert 'CollectionFolder{AccountId=' in s and '"recordId"' not in s
s=s.replace('    private async Task<FolderCreateInput> Create(',Path('/tmp/cerberus-uc25-http-extra-tests.cs').read_text()+'\n    private async Task<FolderCreateInput> Create(')
Path('/tmp/cerberus-uc25-http-tests.cs').write_text(s)
print('Native HTTP folder detail test draft only; not installed yet')
