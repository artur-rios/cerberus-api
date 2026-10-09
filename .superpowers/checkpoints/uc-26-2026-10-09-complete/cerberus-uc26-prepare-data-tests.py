from pathlib import Path
import re
w=Path('/tmp/cerberus-uc26-worktree');original=(w/'tests/Infrastructure/ArturRios.Cerberus.Data.Tests/RecordUpdateStoreTests.cs').read_text()
head,helper=original.split('    private RecordUpdateStore Store()',1)
# Preserve the actual record entity in the complete protection inventory.
head=head.replace('new("record",a.Record.PublicId,1,ProfileSetup.Envelope(2))','KEEP_RECORD_INVENTORY')
head=head.replace('RecordUpdate','FolderUpdate').replace('RecordCreateDetails','FolderCreateDetails').replace('Records_Then','Folders_Then')
head=head.replace('using ArturRios.Cerberus.Data.Records;','using ArturRios.Cerberus.Data.Records;\nusing ArturRios.Cerberus.Data.Folders;').replace('using ArturRios.Cerberus.Domain.Records;','using ArturRios.Cerberus.Domain.Records;\nusing ArturRios.Cerberus.Domain.Folders;')
head=head.replace('await Record(', 'await Folder(').replace('x.Record','x.Folder').replace('y.Record','y.Folder').replace('db.Records','db.Folders').replace('held.Records','held.Folders')
head=re.sub(r'\ba\.Record\b','a.Folder',head)
head=head.replace('recordTrash','folderTrash').replace('recordTerminal','folderTerminal').replace('"record"','"folder"').replace('cerberus.record','cerberus.folder').replace('collectionRecord','collectionTarget')
head=head.replace('KEEP_RECORD_INVENTORY','new("record",a.Record.PublicId,1,ProfileSetup.Envelope(2))')
head=head.replace('await Members(s,a.Collection,route=="collectionTarget"?[target.Id]:[],route=="collectionFolder"?[a.Folder.Id]:[]);','await Members(s,a.Collection,[],route=="collectionTarget"?[target.Id]:route=="collectionFolder"?[a.Folder.Id]:[]);')
head=head.replace('var p=await Profile(s,owner,key,route=="direct"?[target.PublicId]:[],route=="folder"?[a.Folder.PublicId]:[],route.StartsWith("collection")?[a.Collection.PublicId]:[]);','var p=await Profile(s,owner,key,folders:route=="direct"?[target.PublicId]:route=="folder"?[a.Folder.PublicId]:[],collections:route.StartsWith("collection")?[a.Collection.PublicId]:[]);')
head=head.replace('await Members(o,a.Collection,route=="direct"?[target.Id]:[],route=="folder"?[a.Folder.Id]:[]);','await Members(o,a.Collection,[],route=="direct"?[target.Id]:[a.Folder.Id]);')
head=re.sub(r'Members\((\w+),(\w+\.Collection),(\[[^\]]*\]),\[\]\)',r'Members(\1,\2,[],\3)',head)
head=head.replace('Members(o,b.Collection,revoked?[a.Folder.Id]:[],[])','Members(o,b.Collection,[],revoked?[a.Folder.Id]:[])')
# Folder lock wait uses the real target folder, not an ancestor or record.
head=head.replace('Assert.DoesNotContain(capture.Locks,x=>x.Contains("cerberus.folder"));','var targetLock=Assert.Single(capture.Locks,x=>x.Contains("FROM cerberus.folder "));Assert.Contains("FOR NO KEY UPDATE",targetLock);')
head=head.replace('Assert.Equal(2d,Assert.Single(nodes','Assert.Equal(3d,Assert.Single(nodes').replace('i<40','i<50')
a=head.index('    [FunctionalFact]\n    public async Task GivenCreationHoldsChildBeforeAncestorLock')
b=head.index('    [FunctionalTheory][InlineData(false)][InlineData(true)]\n    public async Task GivenOwnerNativeRotation',a)
head=head[:a]+'''    [FunctionalFact]
    public async Task GivenCreationHoldsTargetBeforeAncestorLock_WhenRecipientEdits_ThenWaitWithoutCycleAndPreserveWinningParentRevision()
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var target=await Folder(o,a.Folder.Id);
        await Members(o,a.Collection,[],[a.Folder.Id]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var editing=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var creator=new FolderCreateStore(new ProfileSetup.Factory(fixture,new PauseSecondFolder(reached,release))).CreateAsync(new(o.Actor,o.Verifier,new(Guid.NewGuid(),ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch,[],target.PublicId)),ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));var pending=new FolderUpdateStore(new ProfileSetup.Factory(fixture,new Signal(editing,"folder"))).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,Input()),ct.Token);
        try{await editing.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);}finally{release.TrySetResult();}
        Assert.Null((await creator).Error);var winning=await Row(target.Id);var result=await pending;Assert.Equal("revision_conflict",result.Error);Assert.Null(result.Data);Assert.Equal(target.Revision+1,winning.Revision);Assert.Equal(JsonSerializer.Serialize(winning),JsonSerializer.Serialize(await Row(target.Id)));Assert.Equal(target.Envelope,winning.Envelope);
    }
'''+head[b:]
# Retain complete ordinary fixture helpers and actual records for negative proof.
helper='    private RecordUpdateStore Store()'+helper
helper=helper.replace('RecordUpdate','FolderUpdate').replace('RecordCreateDetails','FolderCreateDetails')
helper=helper.replace('Update(ProfileSetup.State s,VaultRecord r,','Update(ProfileSetup.State s,VaultFolder r,').replace('Success(VaultRecord original,','Success(VaultFolder original,').replace('result.Data!.RecordId','result.Data!.FolderId').replace('original.FolderId,r.FolderId','original.ParentFolderId,r.ParentFolderId')
helper=helper.replace('private async Task<VaultRecord> Row(long id){await using var db=fixture.CreateContext();return await db.Records.AsNoTracking().SingleAsync','private async Task<VaultFolder> Row(long id){await using var db=fixture.CreateContext();return await db.Folders.AsNoTracking().SingleAsync')
a=helper.index('    private async Task<string> Unrelated(');b=helper.index('    private static byte[] Malformed',a)
helper=helper[:a]+'''    private async Task<string> Unrelated(ProfileSetup.State s,long folder)
    {
        var node=System.Text.Json.Nodes.JsonNode.Parse(await Snapshot(s))!.AsObject();var rows=node["Folders"]!.AsArray();node["Folders"]=new System.Text.Json.Nodes.JsonArray(rows.Where(r=>r!["Id"]!.GetValue<long>()!=folder).Select(r=>r!.DeepClone()).ToArray());return node.ToJsonString();
    }
'''+helper[b:]
helper=helper.replace('UPDATE cerberus.record AS','UPDATE cerberus.folder AS')
extra=Path('/tmp/cerberus-uc26-data-extra-tests.cs').read_text()
s=head+extra+helper
assert 'public sealed folder' not in s and 'collectionTarget' in s and 'CTE ancestry' in s
assert s.count('GivenCreationHoldsTargetBeforeAncestorLock')==1 and 'new("record",a.Record.PublicId' in s
p=w/'tests/Infrastructure/ArturRios.Cerberus.Data.Tests/FolderUpdateStoreTests.cs';assert not p.exists();p.write_text(s)
print('Installed folder-specific PostgreSQL tests only; store absent.')
