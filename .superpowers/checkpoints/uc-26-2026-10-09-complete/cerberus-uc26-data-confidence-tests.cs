    [FunctionalTheory][InlineData("account")][InlineData("profile")][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("grant")]
    public async Task GivenTerminalMarkerWithSameGuidButDifferentKind_WhenUpdating_ThenOnlyFolderMarkerHidesTarget(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Folder(s);
        await using(var db=fixture.CreateContext()){db.TerminalErasures.Add(new(){ResourceId=target.PublicId,ResourceKind=kind,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var before=await Snapshot(s);var input=Input();var result=await Update(s,target,input);
        if(kind=="folder"){Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));}else await Success(target,input,result);
    }
    [FunctionalTheory][InlineData("profileTrash","vault_access_denied")][InlineData("profileTerminal","vault_access_denied")][InlineData("actorTerminal","not_found")]
    public async Task GivenSelectedScopeLifecycleRemoval_WhenUpdating_ThenFailWithoutWideningOrNullingSelection(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Folder(s);var p=await Profile(s,owner,key,folders:[target.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};
        await using(var db=fixture.CreateContext()){if(kind=="profileTrash")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));else{db.TerminalErasures.Add(new(){ResourceId=kind=="actorTerminal"?s.AccountId:p.PublicId,ResourceKind=kind=="actorTerminal"?"account":"profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        var before=await Snapshot(s);var result=await Update(s,target,Input());Assert.Equal(error,result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));await using var read=fixture.CreateContext();Assert.Equal(p.Id,(await read.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier)).ProfileId);
    }
    [FunctionalTheory][InlineData("cancel")][InlineData("timeout")][InlineData("db")][InlineData("dbUpdate")][InlineData("json")][InlineData("format")][InlineData("wrapped")]
    public async Task GivenNecessaryProviderFailure_WhenUpdating_Then503WithoutLeakingException(string kind)
    {
        Exception failure=kind switch{"cancel"=>new OperationCanceledException("private"),"timeout"=>new TimeoutException("private"),"db"=>new NpgsqlException("private"),"dbUpdate"=>new DbUpdateException("private"),"json"=>new JsonException("private"),"format"=>new FormatException("private"),_=>new InvalidOperationException("private",new TimeoutException("private"))};
        var result=await new FolderUpdateStore(new ThrowingFactory(failure)).UpdateAsync(new(Guid.NewGuid(),new string('a',64),Guid.NewGuid(),Input()),default);Assert.Equal("persistence_unavailable",result.Error);Assert.Null(result.Data);
    }
    private sealed class ThrowingFactory(Exception exception):IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()=>throw exception;
        public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromException<AppDbContext>(exception);
    }
