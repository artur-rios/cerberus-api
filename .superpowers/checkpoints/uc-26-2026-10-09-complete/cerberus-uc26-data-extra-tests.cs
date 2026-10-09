    [FunctionalTheory]
    [InlineData(CollectionGrantAccess.ReadOnly,false)]
    [InlineData(CollectionGrantAccess.ReadWrite,false)]
    [InlineData(CollectionGrantAccess.ReadOnly,true)]
    [InlineData(CollectionGrantAccess.ReadWrite,true)]
    public async Task GivenActualNativeRecordOnlyAuthority_WhenUpdatingContainingFolder_Then404DespiteSuccessfulRecordRead(CollectionGrantAccess access,bool selected)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();
        var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);
        await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));
        await Members(o,a.Collection,[a.Record.Id],[]);
        await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,access);
        if(selected){var p=await Profile(s,client,key,collections:[a.Collection.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};}
        var proof=await new ArturRios.Cerberus.Data.Records.RecordReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,a.Record.PublicId),default);
        Assert.Null(proof.Error);Assert.Equal(a.Record.PublicId,proof.Data!.Record.RecordId);
        await using(var db=fixture.CreateContext())await db.Folders.Where(x=>x.Id==a.Folder.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.Envelope,"bad"u8.ToArray()));
        var before=await Snapshot(o);var recipient=await Snapshot(s);var result=await Update(s,a.Folder,Input(77));
        Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(o));Assert.Equal(recipient,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenSelectedProfileRecordOnlyAuthority_WhenUpdatingContainingFolder_Then404DespiteSuccessfulRecordRead()
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);
        await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,a.Folder.Id));
        var p=await Profile(s,owner,key,records:[a.Record.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};
        var proof=await new ArturRios.Cerberus.Data.Records.RecordReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,a.Record.PublicId),default);
        Assert.Null(proof.Error);Assert.Equal(a.Record.PublicId,proof.Data!.Record.RecordId);
        var before=await Snapshot(s);var result=await Update(s,a.Folder,Input(77));Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("parent")][InlineData("sibling")]
    public async Task GivenNativeMemberLeafWriteAuthority_WhenUpdatingPrivateContainer_Then404AndNoChange(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);
        var leaf=await Folder(o,a.Folder.Id);var sibling=await Folder(o,a.Folder.Id);await Members(o,a.Collection,[],[leaf.Id]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);
        var proof=await new ArturRios.Cerberus.Data.Folders.FolderReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,leaf.PublicId),default);Assert.Null(proof.Error);Assert.Null(proof.Data!.ParentFolderId);
        var before=await Snapshot(o);var result=await Update(s,kind=="parent"?a.Folder:sibling,Input());Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(o));
    }
