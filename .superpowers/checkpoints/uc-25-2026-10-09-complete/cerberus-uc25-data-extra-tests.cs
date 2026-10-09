    [FunctionalTheory]
    [InlineData(CollectionGrantAccess.ReadOnly,false)][InlineData(CollectionGrantAccess.ReadOnly,true)]
    [InlineData(CollectionGrantAccess.ReadWrite,false)][InlineData(CollectionGrantAccess.ReadWrite,true)]
    public async Task GivenActualNativeRecordOnlyGrant_WhenGettingFolder_ThenKeepContainingFolderHidden(CollectionGrantAccess access,bool selected)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var scoped=new ProtectionFixture();
        var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var a=await AssociationSetup.Items(fixture,foreign);
        await using(var db=fixture.CreateContext())
        {
            await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));
            db.CollectionRecords.Add(new(){AccountId=foreign.InternalId,CollectionId=a.Collection.Id,RecordId=a.Record.Id});await db.SaveChangesAsync();
        }
        var grant=await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,a.Collection,access);
        var p=await Profile(s,recipient,scoped,collections:[a.Collection.PublicId]);var request=selected?s with{Verifier=await Selected(s,p.Id)}:s;
        var proof=await new ArturRios.Cerberus.Data.Records.RecordReadStore(fixture).ReadAsync(new(s.Actor,request.Verifier,a.Record.PublicId),default);
        Assert.Null(proof.Error);Assert.Equal(a.Record.PublicId,proof.Data!.Record.RecordId);Assert.Null(proof.Data.FolderId);
        await using(var db=fixture.CreateContext())await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));
        var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);var result=await Read(request,a.Folder.PublicId);
        Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));Assert.Equal(ownerBefore,await Snapshot(foreign));
    }

    [FunctionalFact]
    public async Task GivenSelectedDirectProfileRecordOnly_WhenGettingFolder_ThenNeverExpandToContainingFolder()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);
        await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));
        var input=ProfileSetup.Input(s,owner,scoped) with{RecordIds=[a.Record.PublicId],FolderIds=[],CollectionIds=[]};
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);
        await using var context=fixture.CreateContext();var p=await context.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==input.ProfileId);var request=s with{Verifier=await Selected(s,p.Id)};
        var proof=await new ArturRios.Cerberus.Data.Records.RecordReadStore(fixture).ReadAsync(new(s.Actor,request.Verifier,a.Record.PublicId),default);
        Assert.Null(proof.Error);Assert.Equal(a.Record.PublicId,proof.Data!.Record.RecordId);Assert.Null(proof.Data.FolderId);
        var before=await Snapshot(s);var result=await Read(request,a.Folder.PublicId);Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenSeveralNativeFolderContributors_WhenGetting_ThenVerifyEveryGrantWithReusedPartyPins(bool selected)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var scoped=new ProtectionFixture();
        var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var root=await Folder(foreign);var target=await Folder(foreign,root.Id);
        var collections=new List<VaultCollection>();var grants=new List<CollectionGrant>();
        for(var n=0;n<3;n++){var a=await AssociationSetup.Items(fixture,foreign);await Members(foreign,a.Collection,[n==0?root.Id:target.Id],[]);collections.Add(a.Collection);grants.Add(await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,a.Collection));}
        var p=await Profile(s,recipient,scoped,collections:collections.Take(2).Select(x=>x.PublicId).ToArray());var request=selected?s with{Verifier=await Selected(s,p.Id)}:s;
        var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);var result=await Read(request,target.PublicId);Assert.Null(result.Error);Assert.Equal(root.PublicId,result.Data!.ParentFolderId);
        Assert.Equal(collections.Take(selected?2:3).Select(x=>x.PublicId).Order(),result.Data.CollectionIds);Assert.Equal(before,await Snapshot(s));Assert.Equal(ownerBefore,await Snapshot(foreign));
        await using(var db=fixture.CreateContext())await db.CollectionGrants.Where(x=>x.Id==grants[1].Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));
        var failed=await Read(request,target.PublicId);Assert.Equal("persistence_unavailable",failed.Error);Assert.Null(failed.Data);
    }

    [FunctionalTheory][InlineData("account")][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("grant")][InlineData("profile")]
    public async Task GivenTerminalAliasWithAnotherResourceKind_WhenGettingFolder_ThenRespectTypedIdentity(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var folder=await Folder(s);
        await using(var db=fixture.CreateContext()){db.TerminalErasures.Add(new(){ResourceKind=kind,ResourceId=folder.PublicId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var before=await Snapshot(s);var r=await Read(s,folder.PublicId);Assert.Equal(kind=="folder"?"not_found":null,r.Error);if(kind=="folder")Assert.Null(r.Data);else Assert.Equal(folder.PublicId,r.Data!.Folder.FolderId);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenSamePublicGuidAcrossResourceKinds_WhenGettingFolder_ThenKeepTypedVisibleOrganization()
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var id=Guid.NewGuid();var a=await AssociationSetup.Items(fixture,s,id);
        await Members(s,a.Collection,[a.Folder.Id],[]);var input=ProfileSetup.Input(s,owner,key);
        input=input with{ProfileId=id,KeyWrappers=input.KeyWrappers with{MasterKeyWrapper=owner.Wrap(s.AccountId,"profile",id,s.Actor)},RecordIds=[],FolderIds=[id],CollectionIds=[id]};
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);
        var r=await Read(s,id);Assert.Null(r.Error);Assert.Equal(id,r.Data!.Folder.FolderId);Assert.Equal(new[]{id},r.Data.ProfileIds);Assert.Null(r.Data.ParentFolderId);Assert.Equal(new[]{id},r.Data.CollectionIds);
    }

    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly)][InlineData(CollectionGrantAccess.ReadWrite)]
    public async Task GivenCorruptUnselectedOverlappingFolderGrant_WhenGettingSelectedTarget_ThenIgnoreUnusedRouteButWideScopeFails(CollectionGrantAccess access)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var key=new ProtectionFixture();
        var foreign=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,recipient);var root=await Folder(foreign);var target=await Folder(foreign,root.Id);
        var a=await AssociationSetup.Items(fixture,foreign);var b=await AssociationSetup.Items(fixture,foreign);
        await Members(foreign,a.Collection,[root.Id],[]);await Members(foreign,b.Collection,[target.Id],[]);
        await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,a.Collection,access);var bad=await AssociationSetup.Grant(fixture,foreign,owner,s,recipient,b.Collection,access);
        var p=await Profile(s,recipient,key,collections:[a.Collection.PublicId]);var request=s with{Verifier=await Selected(s,p.Id)};
        await using(var db=fixture.CreateContext())await db.CollectionGrants.Where(x=>x.Id==bad.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));
        var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);var r=await Read(request,target.PublicId);Assert.Null(r.Error);Assert.Equal(target.PublicId,r.Data!.Folder.FolderId);
        Assert.Equal(root.PublicId,r.Data.ParentFolderId);Assert.Equal(new[]{a.Collection.PublicId},r.Data.CollectionIds);
        var wide=await Read(s,target.PublicId);Assert.Equal("persistence_unavailable",wide.Error);Assert.Null(wide.Data);Assert.Equal(before,await Snapshot(s));Assert.Equal(ownerBefore,await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenInternalTimeoutOrCancellation_WhenGettingFolder_ThenReturn503WithoutMutation(bool cancellation)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Folder(s);var before=await Snapshot(s);
        var r=await new FolderReadStore(new ProfileSetup.Factory(fixture,new FailRead(cancellation))).ReadAsync(new(s.Actor,s.Verifier,target.PublicId),default);
        Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));
    }
    private sealed class FailRead(bool cancellation):DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {if(cancellation)throw new OperationCanceledException();throw new TimeoutException("private provider timeout");}
    }
