from pathlib import Path
w=Path('/tmp/cerberus-uc26-worktree');original=(w/'tests/Presentation/ArturRios.Cerberus.WebApi.Tests/RecordUpdateHttpTests.cs').read_text();body=original.split('    private static RecordUpdateInput Replacement',1)[0]
body=body.replace('Record','Folder').replace('recordId','folderId').replace('record_updated','folder_updated').replace('/api/records','/api/folders').replace('recordTrash','folderTrash').replace('recordTerminal','folderTerminal').replace('ResourceKind="record"','ResourceKind="folder"')
body=body.replace('"record"=>"folderId"','"record"=>"recordId"')
body=body.replace('ALTER TABLE cerberus.record RENAME TO fixture_unavailable_record','ALTER TABLE cerberus.folder RENAME TO fixture_unavailable_folder').replace('ALTER TABLE cerberus.fixture_unavailable_record RENAME TO record','ALTER TABLE cerberus.fixture_unavailable_folder RENAME TO folder')
folder=(w/'tests/Presentation/ArturRios.Cerberus.WebApi.Tests/FolderReadHttpTests.cs').read_text();helpers=folder.split('    private async Task<FolderCreateInput> Create',1)[1];helpers='    private async Task<FolderCreateInput> Create'+helpers
helpers=helpers.replace('Guid[] records,long[] folders','Guid[] folderIds,long[] folders').replace('foreach(var id in records)','foreach(var id in folderIds)').replace('var record=await db.Folders','var folder=await db.Folders').replace('FolderId=record.Id','FolderId=folder.Id')
update='''    private static FolderUpdateInput Replacement(long revision=1)=>new(revision,Envelope(),DateTimeOffset.Parse("2026-10-09T00:00:00Z").AddTicks(19));
    private static HttpRequestMessage Put(State s,string path,byte[] body){var r=Request(s,body,path);r.Method=HttpMethod.Put;return r;}
    private async Task<HttpResponseMessage> Update(State s,Guid id,FolderUpdateInput input){using var req=Put(s,"/api/folders/"+id,Bytes(input));return await Gateway.Client.SendAsync(req);}
    private async Task Changed(State owner,FolderCreateInput target,FolderUpdateInput input,UpdateFolderOutput output)
    {
        Assert.Equal(target.FolderId,output.FolderId);Assert.Equal(input.ExpectedRevision+1,output.Revision);Assert.Equal(Normalize(input.EditedAt),output.EditedAt);
        await using var db=fixture.Context();var row=await db.Folders.SingleAsync(x=>x.PublicId==target.FolderId);Assert.Equal(owner.InternalId,row.AccountId);Assert.Equal(output.Revision,row.Revision);Assert.Equal(output.ServerSequence,row.ServerSequence);Assert.True(output.ServerSequence>0);Assert.Equal(output.EditedAt,row.EditedAt);Assert.Equal(input.Envelope,JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,ProtectionFixture.Json));
        if(target.ParentFolderId is null)Assert.Null(row.ParentFolderId);else Assert.Equal(target.ParentFolderId,(await db.Folders.SingleAsync(x=>x.Id==row.ParentFolderId)).PublicId);
    }
    private async Task<string> Unrelated(State s,Guid target){var node=JsonNode.Parse(await Snapshot(s))!.AsObject();var rows=node["Folders"]!.AsArray();node["Folders"]=new JsonArray(rows.Where(r=>r!["PublicId"]!.GetValue<Guid>()!=target).Select(r=>r!.DeepClone()).ToArray());return node.ToJsonString();}
'''
s=body+update+helpers
assert 'public sealed folder' not in s and '/api/records' not in s and 'db.Records' in s # complete snapshot retains records
assert 'fixture_unavailable_record' not in s and 'recordId' in s # forged independent record selector
p=Path('/tmp/cerberus-uc26-http-tests.cs');assert not p.exists();p.write_text(s);print('Prepared native folder PUT test-only draft, retaining complete records+folders snapshots and actual folder membership helpers; not installed yet.')
