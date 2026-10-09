from pathlib import Path
import re
source=Path('/tmp/cerberus-uc24-worktree/tests/Infrastructure/ArturRios.Cerberus.Data.Tests/RecordReadStoreTests.cs').read_text()
s=source
start=s.rfind('    [FunctionalFact]',0,s.index('GivenSamePublicGuidAcrossResourceKinds'))
end=s.index('    [FunctionalTheory]',start)
s=s[:start]+s[end:]
s=s.replace('r.Data.FolderId','r.Data.ParentFolderId').replace('r.Data!.FolderId','r.Data!.ParentFolderId')
s=s.replace('RecordRead','FolderRead').replace('Domain.Records','Domain.Folders').replace('Data.Records','Data.Folders')
s=s.replace('VaultRecord','VaultFolder').replace('Record(s','TargetFolder(s').replace('Record(foreign','TargetFolder(foreign').replace('Record(targetOwner','TargetFolder(targetOwner').replace('Record(account','TargetFolder(account')
s=s.replace('Task<VaultFolder> Record(','Task<VaultFolder> TargetFolder(')
s=s.replace('RecordIds=records??[],FolderIds=folders??[]','RecordIds=[],FolderIds=(records??[]).Concat(folders??[]).Distinct().ToArray()')
old='db.CollectionRecords.AddRange(records.Select(x=>new CollectionRecord{AccountId=s.InternalId,CollectionId=collection.Id,RecordId=x}));db.CollectionFolders.AddRange(folders.Select(x=>new CollectionFolder{AccountId=s.InternalId,CollectionId=collection.Id,FolderId=x}));'
new='db.CollectionFolders.AddRange(records.Concat(folders).Distinct().Select(x=>new CollectionFolder{AccountId=s.InternalId,CollectionId=collection.Id,FolderId=x}));'
assert old in s;s=s.replace(old,new)
s=s.replace('.Data.Record','.Data.Folder').replace('.Data!.Record','.Data!.Folder').replace('.Record.PublicId','.Folder.PublicId').replace('.Record.Id','.Folder.Id')
s=s.replace('.RecordId','.FolderId').replace('db.Records','db.Folders')
s=s.replace('AccountId=s.InternalId,FolderId=folder','AccountId=s.InternalId,ParentFolderId=folder')
s=s.replace('db.CollectionRecords.Where','db.CollectionFolders.Where')
s=s.replace('collection_record','collection_folder')
s=s.replace('recordTrash','folderTrash').replace('recordTerminal','folderTerminal').replace('ResourceKind="record"','ResourceKind="folder"').replace('?"record":','?"folder":')
s=s.replace('2d,ancestry','3d,ancestry')
s=s.replace('Read(s,a.Folder.PublicId)).Error);','Read(s,b.Folder.PublicId)).Error);')
s=s.replace('"collectionRecord"','"collectionTarget"')
s=s.replace('GivenOwnedRecord','GivenOwnedFolder').replace('GivenSelectedRecordRoute','GivenSelectedFolderRoute').replace('GivenNativeSharedRecord','GivenNativeSharedFolder').replace('GivenAnotherCorruptRecord','GivenAnotherCorruptFolder')
# Keep the persisted snapshot broad: records are separate resources and must remain unchanged.
start=s.index('    private async Task<string> Snapshot(');end=s.index('    private sealed class CapturePlan',start)
list_source=Path('/tmp/cerberus-uc24-worktree/tests/Infrastructure/ArturRios.Cerberus.Data.Tests/FolderListStoreTests.cs').read_text()
snap=list_source[list_source.index('    private async Task<string> Snapshot('):list_source.index('    private sealed class RevokeAfterRead')]
s=s[:start]+snap+s[end:]
assert 'FolderReadStore' in s and 'ParentFolderId' in s and 'CollectionFolder{AccountId=' in s
assert 'CollectionRecord{' not in s
s=s.replace('    private FolderReadStore Store()',Path('/tmp/cerberus-uc25-data-extra-tests.cs').read_text()+'\n    private FolderReadStore Store()')
Path('/tmp/cerberus-uc25-data-tests.cs').write_text(s)
print('Draft folder-read PostgreSQL matrix only; no repository installation.')
