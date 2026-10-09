    [FunctionalTheory]
    [InlineData("direct")][InlineData("descendant")][InlineData("brokenCiphertext")][InlineData("unusedCounters")]
    public async Task GivenOwnedCollectionInSelectedProfile_WhenCreatingBelowItsMemberFolder_ThenUseCurrentOwnedScope(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();
        var s=await ProfileSetup.Create(fixture,owner);var root=await Folder(s);
        var parent=kind=="descendant"?await Folder(s,root.Id):root;
        var p=await Profile(s,owner,scoped);var selected=await Selected(s,p.Id);
        await using(var db=fixture.CreateContext())
        {
            var collection=new VaultCollection{PublicId=Guid.NewGuid(),AccountId=s.InternalId,
                Envelope=JsonSerializer.SerializeToUtf8Bytes(ProfileSetup.Envelope(),ProtectionFixture.Json),
                KeyEpoch=1,EditedAt=DateTimeOffset.UnixEpoch};
            db.Collections.Add(collection);await db.SaveChangesAsync();
            db.CollectionFolders.Add(new(){AccountId=s.InternalId,CollectionId=collection.Id,FolderId=root.Id});
            db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=collection.Id});
            if(kind=="brokenCiphertext")collection.Envelope=[255];
            if(kind=="unusedCounters"){collection.Revision=0;collection.ServerSequence=0;}
            await db.SaveChangesAsync();
        }
        var result=await Store().CreateAsync(new(s.Actor,selected,Input([p.PublicId],parent.PublicId)),default);
        Assert.Null(result.Error);await using var check=fixture.CreateContext();
        var row=await check.Folders.SingleAsync(x=>x.PublicId==result.Data!.FolderId);
        Assert.Equal(parent.Id,row.ParentFolderId);Assert.Equal(p.Id,Assert.Single(await check.ProfileFolders.Where(x=>x.FolderId==row.Id).ToArrayAsync()).ProfileId);
    }

    [FunctionalTheory]
    [InlineData("collectionTrash")][InlineData("collectionTerminal")][InlineData("missingProfileLink")][InlineData("missingMember")]
    public async Task GivenInactiveOrUnlinkedOwnCollection_WhenCreatingWithSelectedAccess_Then404WithoutMutation(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();
        var s=await ProfileSetup.Create(fixture,owner);var root=await Folder(s);var p=await Profile(s,owner,scoped);var selected=await Selected(s,p.Id);
        await using(var db=fixture.CreateContext())
        {
            var c=new VaultCollection{PublicId=Guid.NewGuid(),AccountId=s.InternalId,Envelope=JsonSerializer.SerializeToUtf8Bytes(ProfileSetup.Envelope(),ProtectionFixture.Json),KeyEpoch=1,EditedAt=DateTimeOffset.UnixEpoch,DeletedAt=kind=="collectionTrash"?DateTimeOffset.UtcNow:null};
            db.Collections.Add(c);await db.SaveChangesAsync();
            if(kind!="missingMember")db.CollectionFolders.Add(new(){AccountId=s.InternalId,CollectionId=c.Id,FolderId=root.Id});
            if(kind!="missingProfileLink")db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=c.Id});
            if(kind=="collectionTerminal")db.TerminalErasures.Add(new(){ResourceId=c.PublicId,ResourceKind="collection",DeletedAt=DateTimeOffset.UtcNow});
            await db.SaveChangesAsync();
        }
        var before=await Snapshot(s);var result=await Store().CreateAsync(new(s.Actor,selected,Input([p.PublicId],root.PublicId)),default);
        Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("account")][InlineData("profile")][InlineData("record")][InlineData("collection")][InlineData("grant")]
    public async Task GivenSameUuidTerminalMarkerOfAnotherKind_WhenCreatingFolder_ThenKeepTypedIdentityIndependent(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var input=Input();
        await using(var db=fixture.CreateContext()){db.TerminalErasures.Add(new(){ResourceId=input.FolderId,ResourceKind=kind,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var result=await Store().CreateAsync(new(s.Actor,s.Verifier,input),default);Assert.Null(result.Error);
        await using var check=fixture.CreateContext();Assert.True(await check.Folders.AnyAsync(x=>x.PublicId==input.FolderId));
        Assert.True(await check.TerminalErasures.AnyAsync(x=>x.ResourceId==input.FolderId && x.ResourceKind==kind));
    }

    [FunctionalFact]
    public async Task GivenUnselectedCyclicFolder_WhenCreating_ThenHideUnpermittedPathBeforeCorruption()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);
        var root=await Folder(s);var child=await Folder(s,root.Id);var p=await Profile(s,owner,scoped);var selected=await Selected(s,p.Id);
        await using(var db=fixture.CreateContext())await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ParentFolderId,child.Id));
        var before=await Snapshot(s);var result=await Store().CreateAsync(new(s.Actor,selected,Input([p.PublicId],root.PublicId)),default);
        Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("parentCiphertext")][InlineData("profileCiphertext")][InlineData("ancestorCounters")]
    public async Task GivenUnusedStoredContentMetadata_WhenCreatingChild_ThenValidateOnlyAffectedStructuralCounters(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);
        var root=await Folder(s);var parent=await Folder(s,root.Id);var p=await Profile(s,owner,scoped,root);
        await using(var db=fixture.CreateContext())
        {
            if(kind=="parentCiphertext")await db.Folders.Where(x=>x.Id==parent.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,new byte[]{255}));
            if(kind=="profileCiphertext")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,new byte[]{255}));
            if(kind=="ancestorCounters")await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revision,0).SetProperty(v=>v.ServerSequence,0));
        }
        await using var beforeContext=fixture.CreateContext();var beforeRoot=await beforeContext.Folders.AsNoTracking().Where(x=>x.Id==root.Id).Select(x=>new{x.Revision,x.ServerSequence,x.Envelope}).SingleAsync();
        var result=await Store().CreateAsync(new(s.Actor,s.Verifier,Input([p.PublicId],parent.PublicId)),default);Assert.Null(result.Error);
        await using var check=fixture.CreateContext();var afterRoot=await check.Folders.AsNoTracking().Where(x=>x.Id==root.Id).Select(x=>new{x.Revision,x.ServerSequence,x.Envelope}).SingleAsync();
        Assert.Equal(JsonSerializer.Serialize(beforeRoot),JsonSerializer.Serialize(afterRoot));
    }

    [FunctionalTheory]
    [InlineData("parent")][InlineData("ancestor")][InlineData("profile")]
    public async Task GivenUnusedStoredInfiniteClientTime_WhenCreatingChild_ThenPreserveItWithoutParsing(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);
        var root=await Folder(s);var parent=await Folder(s,root.Id);var p=await Profile(s,owner,scoped,root);
        await using(var db=fixture.CreateContext())
        {
            if(kind=="profile")await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.profile SET edited_at='-infinity' WHERE id={p.Id}");
            else await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.folder SET edited_at='-infinity' WHERE id={(kind=="parent"?parent.Id:root.Id)}");
        }
        var result=await Store().CreateAsync(new(s.Actor,s.Verifier,Input([p.PublicId],parent.PublicId)),default);Assert.Null(result.Error);
        await using var check=fixture.CreateContext();
        var preserved=kind=="profile"?await check.Database.SqlQuery<string>($"SELECT edited_at::text AS \"Value\" FROM cerberus.profile WHERE id={p.Id}").SingleAsync():await check.Database.SqlQuery<string>($"SELECT edited_at::text AS \"Value\" FROM cerberus.folder WHERE id={(kind=="parent"?parent.Id:root.Id)}").SingleAsync();
        Assert.Equal("-infinity",preserved);
    }
