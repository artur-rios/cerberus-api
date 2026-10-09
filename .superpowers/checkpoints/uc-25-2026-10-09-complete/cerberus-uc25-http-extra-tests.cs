    [FunctionalTheory]
    [InlineData(CollectionGrantAccess.ReadOnly,false)][InlineData(CollectionGrantAccess.ReadOnly,true)]
    [InlineData(CollectionGrantAccess.ReadWrite,false)][InlineData(CollectionGrantAccess.ReadWrite,true)]
    public async Task GivenActualNativeRecordOnlyGrant_WhenGettingFolderOverHttp_ThenKeepContainerAndSiblingHidden(CollectionGrantAccess access,bool selected)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var key=new ProtectionFixture();var foreign=await Setup(owner);var s=await Setup(recipient);var folder=await Folder(foreign);var sibling=await Folder(foreign);
        var input=new ArturRios.Cerberus.Domain.Records.RecordCreateInput(Guid.NewGuid(),Envelope(),DateTimeOffset.UnixEpoch,[],folder.PublicId);
        using(var create=Request(foreign,Bytes(input),"/api/records")){using var response=await Gateway.Client.SendAsync(create);await Success<ArturRios.Cerberus.Command.Records.CreateRecordOutput>(response,201);}
        var(collection,grant)=await Share(foreign,owner,s,recipient,access,[],[]);
        await using(var db=fixture.Context()){var row=await db.Records.SingleAsync(x=>x.PublicId==input.RecordId);db.CollectionRecords.Add(new(){AccountId=foreign.InternalId,CollectionId=collection.Id,RecordId=row.Id});await db.SaveChangesAsync();}
        var p=await Profile(s,recipient,key,collections:[collection.PublicId]);var request=selected?s with{Access=await Open(s,p,key)}:s;
        using(var allowed=await Read(request,"/api/records/"+input.RecordId)){var record=await Success<ArturRios.Cerberus.Query.Records.RecordDetailsOutput>(allowed,200);Assert.Equal(input.RecordId,record.RecordId);Assert.Null(record.FolderId);}
        await using(var db=fixture.Context())await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));
        var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);using var hidden=await Read(request,"/api/folders/"+folder.PublicId);await Failure(hidden,404,"not_found");using var other=await Read(request,"/api/folders/"+sibling.PublicId);await Failure(other,404,"not_found");Assert.Equal(before,await Snapshot(s));Assert.Equal(ownerBefore,await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenNativeProfileRecordOnly_WhenGettingFolderOverHttp_ThenNeverWidenToContainer(string mode)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await Setup(owner);var folder=await Folder(s);var p=await Profile(s,owner,key,mode:mode);
        var input=new ArturRios.Cerberus.Domain.Records.RecordCreateInput(Guid.NewGuid(),Envelope(),DateTimeOffset.UnixEpoch,[p.PublicId],folder.PublicId);
        using(var create=Request(s,Bytes(input),"/api/records")){using var response=await Gateway.Client.SendAsync(create);await Success<ArturRios.Cerberus.Command.Records.CreateRecordOutput>(response,201);}
        var request=s with{Access=await Open(s,p,key)};
        using(var allowed=await Read(request,"/api/records/"+input.RecordId)){var record=await Success<ArturRios.Cerberus.Query.Records.RecordDetailsOutput>(allowed,200);Assert.Equal(input.RecordId,record.RecordId);Assert.Null(record.FolderId);}
        var before=await Snapshot(s);using var hidden=await Read(request,"/api/folders/"+folder.PublicId);await Failure(hidden,404,"not_found");Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenCorruptOverlappingForeignFolderGrant_WhenGettingOverHttp_ThenFailOnlyWhenRouteIsSelected(bool selected)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var key=new ProtectionFixture();var foreign=await Setup(owner);var s=await Setup(recipient);var root=await Folder(foreign);var target=await Create(foreign,folder:root.PublicId);
        var(a,_)=await Share(foreign,owner,s,recipient,CollectionGrantAccess.ReadOnly,[],[root.Id]);var(b,bad)=await Share(foreign,owner,s,recipient,CollectionGrantAccess.ReadWrite,[target.FolderId],[]);
        var p=await Profile(s,recipient,key,collections:[a.PublicId]);var request=selected?s with{Access=await Open(s,p,key)}:s;
        await using(var db=fixture.Context())await db.CollectionGrants.Where(x=>x.Id==bad.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));
        var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);using var response=await Read(request,"/api/folders/"+target.FolderId);
        if(selected){var item=await Success<FolderDetailsOutput>(response,200);Assert.Equal(root.PublicId,item.ParentFolderId);Assert.Equal(new[]{a.PublicId},item.CollectionIds);Assert.Empty(item.ProfileIds);}else await Failure(response,503,"persistence_unavailable");
        Assert.Equal(before,await Snapshot(s));Assert.Equal(ownerBefore,await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData("account")][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("grant")][InlineData("profile")]
    public async Task GivenTypedTerminalAlias_WhenGettingFolderOverHttp_ThenOnlyFolderKindHidesTarget(string kind)
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);
        await using(var db=fixture.Context()){db.TerminalErasures.Add(new(){ResourceId=target.FolderId,ResourceKind=kind,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var before=await Snapshot(s);using var response=await Read(s,"/api/folders/"+target.FolderId);
        if(kind=="folder")await Failure(response,404,"not_found");else{var item=await Success<FolderDetailsOutput>(response,200);Assert.Equal(target.FolderId,item.FolderId);Assert.Equal(target.Envelope,item.Envelope);}Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("Master",false)][InlineData("Master",true)][InlineData("PerProfile",false)][InlineData("PerProfile",true)]
    public async Task GivenNativeSelectionOfOwnedCollection_WhenGettingFolderOverHttp_ThenReturnOnlyEffectiveReferencesAndPermittedParent(string mode,bool ancestorMember)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await Setup(owner);var root=await Folder(s);var target=await Create(s,folder:root.PublicId);var outside=await Create(s);VaultCollection collection;
        await using(var db=fixture.Context())
        {
            collection=new(){PublicId=Guid.NewGuid(),AccountId=s.InternalId,Envelope=Bytes(Envelope()),EditedAt=DateTimeOffset.UnixEpoch};db.Collections.Add(collection);await db.SaveChangesAsync();
            var targetRow=await db.Folders.SingleAsync(x=>x.PublicId==target.FolderId);db.CollectionFolders.Add(new(){AccountId=s.InternalId,CollectionId=collection.Id,FolderId=ancestorMember?root.Id:targetRow.Id});await db.SaveChangesAsync();
        }
        var p=await Profile(s,owner,key,mode:mode,collections:[collection.PublicId]);var request=s with{Access=await Open(s,p,key)};var before=await Snapshot(s);
        using var response=await Read(request,"/api/folders/"+target.FolderId);var item=await Success<FolderDetailsOutput>(response,200);Assert.Empty(item.ProfileIds);Assert.Equal(new[]{collection.PublicId},item.CollectionIds);Assert.Equal(ancestorMember?root.PublicId:null,item.ParentFolderId);
        using var hidden=await Read(request,"/api/folders/"+outside.FolderId);await Failure(hidden,404,"not_found");Assert.Equal(before,await Snapshot(s));
    }
