    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly,"wide")][InlineData(CollectionGrantAccess.ReadWrite,"wide")][InlineData(CollectionGrantAccess.ReadOnly,"Master")][InlineData(CollectionGrantAccess.ReadWrite,"Master")][InlineData(CollectionGrantAccess.ReadOnly,"PerProfile")][InlineData(CollectionGrantAccess.ReadWrite,"PerProfile")]
    public async Task GivenActualNativeRecordOnlyGrant_WhenReplacingContainingFolder_Then404AfterSuccessfulRecordGet(CollectionGrantAccess access,string mode)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();var o=await Setup(owner);var s=await Setup(client);var target=await Create(o);var record=await CreateRecord(o,target.FolderId);var (collection,grant)=await Share(o,owner,s,client,access,[],[]);
        await using(var db=fixture.Context()){var row=await db.Records.SingleAsync(x=>x.PublicId==record.RecordId);db.CollectionRecords.Add(new(){AccountId=o.InternalId,CollectionId=collection.Id,RecordId=row.Id});await db.SaveChangesAsync();}
        var actor=s;if(mode!="wide"){var p=await Profile(s,client,key,mode:mode,collections:[collection.PublicId]);actor=s with{Access=await Open(s,p,key)};}
        using(var get=await Read(actor,"/api/records/"+record.RecordId)){var proof=await Success<ArturRios.Cerberus.Query.Records.RecordDetailsOutput>(get,200);Assert.Equal(record.RecordId,proof.RecordId);Assert.Null(proof.FolderId);}
        await using(var db=fixture.Context()){await db.Folders.Where(x=>x.PublicId==target.FolderId).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.Envelope,"bad"u8.ToArray()));await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));}
        var before=await Snapshot(o);var recipient=await Snapshot(s);using var response=await Update(actor,target.FolderId,Replacement(77));await Failure(response,404,"not_found");Assert.Equal(before,await Snapshot(o));Assert.Equal(recipient,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenSelectedDirectRecordOnly_WhenReplacingContainingFolder_Then404AfterSuccessfulRecordGet(string mode)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);var record=await CreateRecord(s,target.FolderId);var id=Guid.NewGuid();var input=new ProfileCreateInput(id,Envelope(),new(mode,key.Material.UnlockVerifier,owner.Wrap(s.AccountId,"profile",id,s.Actor),mode=="PerProfile"?key.Material.PasswordWrapper:null),DateTimeOffset.UnixEpoch,[record.RecordId],[],[]);
        using(var req=Request(s,Bytes(input),"/api/profiles"))using(var response=await Gateway.Client.SendAsync(req))await Success<CreateProfileOutput>(response,201);
        await using var db=fixture.Context();var p=await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==id);var selected=s with{Access=await Open(s,p,key)};
        using(var get=await Read(selected,"/api/records/"+record.RecordId)){var proof=await Success<ArturRios.Cerberus.Query.Records.RecordDetailsOutput>(get,200);Assert.Equal(record.RecordId,proof.RecordId);Assert.Null(proof.FolderId);}
        var before=await Snapshot(s);using var response2=await Update(selected,target.FolderId,Replacement(77));await Failure(response2,404,"not_found");Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("parent",false)][InlineData("sibling",false)][InlineData("parent",true)][InlineData("sibling",true)]
    public async Task GivenNativeMemberLeafWriteGrant_WhenReplacingUnsharedParentOrSibling_Then404AndNoChanges(string kind,bool selected)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();var o=await Setup(owner);var s=await Setup(client);var root=await Folder(o);var leaf=await Create(o,folder:root.PublicId);var sibling=await Create(o,folder:root.PublicId);var (collection,_)=await Share(o,owner,s,client,CollectionGrantAccess.ReadWrite,[leaf.FolderId],[]);var actor=s;
        if(selected){var p=await Profile(s,client,key,collections:[collection.PublicId]);actor=s with{Access=await Open(s,p,key)};}
        using(var get=await Read(actor,"/api/folders/"+leaf.FolderId)){var proof=await Success<FolderDetailsOutput>(get,200);Assert.Null(proof.ParentFolderId);Assert.Empty(proof.ProfileIds);}
        var before=await Snapshot(o);using var response=await Update(actor,kind=="parent"?root.PublicId:sibling.FolderId,Replacement(77));await Failure(response,404,"not_found");Assert.Equal(before,await Snapshot(o));
    }
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenCorruptOverlappingContributor_WhenReplacing_ThenSelectedValidRouteSucceedsAndWideFailsWholeResponse(string mode)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();var o=await Setup(owner);var s=await Setup(client);var target=await Create(o);var (good,_)=await Share(o,owner,s,client,CollectionGrantAccess.ReadWrite,[target.FolderId],[]);var (_,bad)=await Share(o,owner,s,client,CollectionGrantAccess.ReadOnly,[target.FolderId],[]);var p=await Profile(s,client,key,mode:mode,collections:[good.PublicId]);var selected=s with{Access=await Open(s,p,key)};
        await using(var db=fixture.Context())await db.CollectionGrants.Where(x=>x.Id==bad.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));var input=Replacement();using(var response=await Update(selected,target.FolderId,input))await Changed(o,target,input,await Success<UpdateFolderOutput>(response,200));
        var before=await Snapshot(o);using var denied=await Update(s,target.FolderId,Replacement(2));await Failure(denied,503,"persistence_unavailable");Assert.Equal(before,await Snapshot(o));
    }
    [FunctionalTheory][InlineData("profileTrash")][InlineData("profileTerminal")]
    public async Task GivenSelectedProfileBecomesHidden_WhenReplacing_Then403WithoutNullingOrWideningSelection(string kind)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await Setup(owner);var p=await Profile(s,owner,key);var target=await Create(s,[p.PublicId]);var selected=s with{Access=await Open(s,p,key)};
        await using(var db=fixture.Context()){if(kind=="profileTrash")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));else{db.TerminalErasures.Add(new(){ResourceId=p.PublicId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        var before=await Snapshot(s);using var response=await Update(selected,target.FolderId,Replacement());await Failure(response,403,"vault_access_denied");Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenFolderWithActualChildAndRecord_WhenReplacingContent_ThenOnlyTargetMetadataChanges()
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);await Create(s,folder:target.FolderId);await CreateRecord(s,target.FolderId);await using var db=fixture.Context();var row=await db.Folders.AsNoTracking().SingleAsync(x=>x.PublicId==target.FolderId);Assert.Equal(3,row.Revision);var before=await Unrelated(s,target.FolderId);var input=Replacement(3);
        using var response=await Update(s,target.FolderId,input);await Changed(s,target,input,await Success<UpdateFolderOutput>(response,200));Assert.Equal(before,await Unrelated(s,target.FolderId));
    }
    private async Task<ArturRios.Cerberus.Domain.Records.RecordCreateInput> CreateRecord(State s,Guid folder)
    {
        var input=new ArturRios.Cerberus.Domain.Records.RecordCreateInput(Guid.NewGuid(),Envelope(),DateTimeOffset.UnixEpoch,[],folder);using var req=Request(s,Bytes(input),"/api/records");using var response=await Gateway.Client.SendAsync(req);await Success<ArturRios.Cerberus.Command.Records.CreateRecordOutput>(response,201);return input;
    }
