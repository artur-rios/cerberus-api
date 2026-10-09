    [FunctionalTheory]
    [InlineData("Master",false)][InlineData("Master",true)][InlineData("PerProfile",false)][InlineData("PerProfile",true)]
    public async Task GivenNativeSelectedAccessThroughOwnCollection_WhenCreatingChild_ThenAttachOnlyNewFolder(string mode,bool descendant)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner);
        var root=await Folder(s);var parent=descendant?await Folder(s,root.Id):root;var p=await Profile(s,owner,scoped,null,mode);
        await using(var db=fixture.Context())
        {
            var c=new VaultCollection{PublicId=Guid.NewGuid(),AccountId=s.InternalId,Envelope=Bytes(Envelope()),KeyEpoch=1,EditedAt=DateTimeOffset.UnixEpoch};
            db.Collections.Add(c);await db.SaveChangesAsync();db.CollectionFolders.Add(new(){AccountId=s.InternalId,CollectionId=c.Id,FolderId=root.Id});db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=c.Id});await db.SaveChangesAsync();
        }
        var selected=s with{Access=await Open(s,p,scoped)};var input=Input([p.PublicId],parent.PublicId);
        using var response=await Send(selected,input);var result=await Success<CreateFolderOutput>(response,201);Assert.Equal(input.FolderId,result.FolderId);
        using var read=await Read(selected,"/api/profiles/"+p.PublicId);var details=await Success<ProfileDetailsOutput>(read,200);Assert.Equal(input.FolderId,Assert.Single(details.FolderIds));
        await using var check=fixture.Context();Assert.Equal(parent.Id,(await check.Folders.SingleAsync(x=>x.PublicId==input.FolderId)).ParentFolderId);
    }

    [FunctionalTheory]
    [InlineData("account")][InlineData("profile")][InlineData("record")][InlineData("collection")][InlineData("grant")]
    public async Task GivenOtherKindTerminalUuid_WhenCreatingThroughHttp_ThenReserveOnlyFolderNamespace(string kind)
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var input=Input();
        await using(var db=fixture.Context()){db.TerminalErasures.Add(new(){ResourceId=input.FolderId,ResourceKind=kind,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        using var response=await Send(s,input);await Success<CreateFolderOutput>(response,201);
        await using var check=fixture.Context();Assert.True(await check.Folders.AnyAsync(x=>x.PublicId==input.FolderId));Assert.True(await check.TerminalErasures.AnyAsync(x=>x.ResourceId==input.FolderId && x.ResourceKind==kind));
    }

    [FunctionalFact]
    public async Task GivenNewFolderFromActualHttp_WhenCreatingSelectedRecord_ThenUseCurrentOwnedAncestry()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner);var p=await Profile(s,owner,scoped);
        var selected=s with{Access=await Open(s,p,scoped)};var folder=Input([p.PublicId]);using(var created=await Send(selected,folder))await Success<CreateFolderOutput>(created,201);
        var record=new ArturRios.Cerberus.Domain.Records.RecordCreateInput(Guid.NewGuid(),Envelope(),DateTimeOffset.UnixEpoch,[p.PublicId],folder.FolderId);
        using var request=Request(selected,Bytes(record),"/api/records");using var response=await Gateway.Client.SendAsync(request);
        var result=await Success<ArturRios.Cerberus.Command.Records.CreateRecordOutput>(response,201);Assert.Equal(record.RecordId,result.RecordId);
        using var get=await Read(selected,"/api/records/"+record.RecordId);Assert.Equal(HttpStatusCode.OK,get.StatusCode);Assert.True(get.Headers.CacheControl?.NoStore);
        await using var check=fixture.Context();var parent=await check.Folders.SingleAsync(x=>x.PublicId==folder.FolderId);Assert.Equal(2,parent.Revision);Assert.Equal(parent.Id,(await check.Records.SingleAsync(x=>x.PublicId==record.RecordId)).FolderId);
    }

    [FunctionalFact]
    public async Task GivenNewNestedFolders_WhenRotatingCompleteInventory_ThenPreserveParentAndClientTimes()
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var rootInput=Input();using(var created=await Send(s,rootInput))await Success<CreateFolderOutput>(created,201);
        var childInput=Input([],rootInput.FolderId);using(var created=await Send(s,childInput))await Success<CreateFolderOutput>(created,201);
        var content=new[]{new ContentReplacement("account",s.AccountId,1,Envelope(2)),new ContentReplacement("folder",rootInput.FolderId,2,Envelope(2)),new ContentReplacement("folder",childInput.FolderId,1,Envelope(2))};
        var change=new ProtectionChange(s.AccountId,1,1,"rotate-content",owner.Rewrap(),content);var raw=Bytes(change);
        using var challengeRequest=new HttpRequestMessage(HttpMethod.Post,"/api/vault/challenges"){Content=JsonContent.Create(new{operation="change-protection",requestHash=ProtocolBinary.Encode(SHA256.HashData(raw))})};challengeRequest.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));
        using var issued=await Gateway.Client.SendAsync(challengeRequest);Assert.Equal(HttpStatusCode.Created,issued.StatusCode);
        using var document=JsonDocument.Parse(await issued.Content.ReadAsStringAsync());var challenge=document.RootElement.GetProperty("data").GetProperty("challenge").Deserialize<VaultProofChallenge>(ProtectionFixture.Json)!;
        using var request=Request(s,raw,"/api/vault/protection");request.Method=HttpMethod.Put;request.Headers.Add("X-Cerberus-Challenge-Id",challenge.ChallengeId.ToString());request.Headers.Add("X-Cerberus-Proof",owner.Sign(challenge));using var response=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.OK,response.StatusCode);
        await using var check=fixture.Context();var root=await check.Folders.SingleAsync(x=>x.PublicId==rootInput.FolderId);var child=await check.Folders.SingleAsync(x=>x.PublicId==childInput.FolderId);
        Assert.Equal(3,root.Revision);Assert.Equal(2,child.Revision);Assert.Null(root.ParentFolderId);Assert.Equal(root.Id,child.ParentFolderId);
        Assert.Equal(Normalize(rootInput.EditedAt),root.EditedAt);Assert.Equal(Normalize(childInput.EditedAt),child.EditedAt);Assert.Equal(Bytes(content[1].Envelope),root.Envelope);Assert.Equal(Bytes(content[2].Envelope),child.Envelope);
    }
