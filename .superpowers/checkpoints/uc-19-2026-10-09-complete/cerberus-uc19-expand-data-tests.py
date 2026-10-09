from pathlib import Path
p=Path('/tmp/cerberus-uc19-worktree/tests/Infrastructure/ArturRios.Cerberus.Data.Tests/RecordUpdateStoreTests.cs');s=p.read_text().replace('using Microsoft.EntityFrameworkCore;','using ArturRios.Cerberus.Data.Protection;\nusing Npgsql;\nusing Microsoft.EntityFrameworkCore;')
extra=r'''
    [FunctionalTheory][InlineData(false,false)][InlineData(true,false)][InlineData(false,true)]
    public async Task GivenCyclicAncestry_WhenUpdating_Then503OnlyForRelevantActiveTarget(bool outside,bool hidden)
    {using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var root=await Folder(s);var child=await Folder(s,root.Id);var target=await Record(s,child.Id);if(outside){var p=await Profile(s,owner,key);s=s with{Verifier=await Selected(s,p.Id)};}await using(var db=fixture.CreateContext())await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,child.Id).SetProperty(f=>f.DeletedAt,hidden?(DateTimeOffset?)DateTimeOffset.UtcNow:null));var before=await Snapshot(s);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(10));var r=await Store().UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,Input()),ct.Token);Assert.Equal(outside||hidden?"not_found":"persistence_unavailable",r.Error);Assert.Equal(before,await Snapshot(s));}
    [FunctionalFact]
    public async Task GivenNewNativeAuthorityImmediatelyBeforeWrite_WhenUpdating_ThenConflictRatherThanUseUnheldGrant()
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);await Members(o,a.Collection,[a.Record.Id],[]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);var before=await Row(a.Record.Id);
        var store=new RecordUpdateStore(new ProfileSetup.Factory(fixture,new BeforeWrite(async()=>{var b=await AssociationSetup.Items(fixture,o);await Members(o,b.Collection,[a.Record.Id],[]);await AssociationSetup.Grant(fixture,o,owner,s,client,b.Collection,CollectionGrantAccess.ReadWrite);})));
        var r=await store.UpdateAsync(new(s.Actor,s.Verifier,a.Record.PublicId,Input()),default);Assert.Equal("revision_conflict",r.Error);Assert.Null(r.Data);Assert.Equal(JsonSerializer.Serialize(before),JsonSerializer.Serialize(await Row(a.Record.Id)));
    }
    [FunctionalFact]
    public async Task GivenReciprocalNativeWriteGrants_WhenBothActorsEditForeignRecords_ThenNoForeignAccountLockCycle()
    {
        using var one=new ProtectionFixture();using var two=new ProtectionFixture();var a=await ProfileSetup.Create(fixture,one);var b=await ProfileSetup.Create(fixture,two);var x=await AssociationSetup.Items(fixture,a);var y=await AssociationSetup.Items(fixture,b);await Members(a,x.Collection,[x.Record.Id],[]);await Members(b,y.Collection,[y.Record.Id],[]);await AssociationSetup.Grant(fixture,a,one,b,two,x.Collection,CollectionGrantAccess.ReadWrite);await AssociationSetup.Grant(fixture,b,two,a,one,y.Collection,CollectionGrantAccess.ReadWrite);
        var i=Input();var j=Input();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));var r=await Task.WhenAll(Store().UpdateAsync(new(a.Actor,a.Verifier,y.Record.PublicId,i),ct.Token),Store().UpdateAsync(new(b.Actor,b.Verifier,x.Record.PublicId,j),ct.Token));await Success(y.Record,i,r[0]);await Success(x.Record,j,r[1]);
    }
    [FunctionalFact]
    public async Task GivenCreationHoldsChildBeforeAncestorLock_WhenRecipientEdits_ThenNoFolderLockOrForeignOwnerWait()
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var child=await Folder(o,a.Folder.Id);var target=await Record(o,child.Id);await Members(o,a.Collection,[],[a.Folder.Id]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var creator=new RecordCreateStore(new ProfileSetup.Factory(fixture,new PauseSecondFolder(reached,release))).CreateAsync(new(o.Actor,o.Verifier,new(Guid.NewGuid(),ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch,[],child.PublicId)),ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));try{var i=Input();var edit=await Store().UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,i),ct.Token).WaitAsync(TimeSpan.FromSeconds(5));await Success(target,i,edit);}finally{release.TrySetResult();}
        Assert.Null((await creator).Error);
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenOwnerNativeRotationAndRecipientEdit_WhenCompeting_ThenNeverOverwriteWinningCipher(bool rotationFirst)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);await Members(o,a.Collection,[a.Record.Id],[]);var g=await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);
        var change=new ProtectionChange(o.AccountId,1,1,"rotate-content",owner.Rewrap(),[new("account",o.AccountId,1,ProfileSetup.Envelope(2)),new("record",a.Record.PublicId,1,ProfileSetup.Envelope(2)),new("folder",a.Folder.PublicId,1,ProfileSetup.Envelope(2)),new("collection",a.Collection.PublicId,1,ProfileSetup.Envelope(2))]){GrantReplacements=[new(g.PublicId,1,owner.WrapGrant(o.AccountId,a.Collection.PublicId,g.PublicId,s.Actor,client.Material.RecipientKey,2,2))]};
        var raw=Bytes(change);var challenge=(await new VaultProtectionStore(fixture).ChallengeAsync(o.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;var request=new ProtectionChangeRequest(o.Actor,o.Verifier,challenge.ChallengeId,owner.Sign(challenge),raw,change);
        if(rotationFirst){Assert.Null((await new VaultProtectionChangeStore(fixture).ChangeAsync(request,default)).Error);var before=await Snapshot(o);Assert.Equal("revision_conflict",(await Update(s,a.Record,Input())).Error);Assert.Equal(before,await Snapshot(o));Assert.Equal(2,(await Row(a.Record.Id)).Revision);}
        else{
            var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));var rotation=new VaultProtectionChangeStore(new ProfileSetup.Factory(fixture,new PauseRotation(reached,release))).ChangeAsync(request,ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
            try{var i=Input();var r=await Store().UpdateAsync(new(s.Actor,s.Verifier,a.Record.PublicId,i),ct.Token).WaitAsync(TimeSpan.FromSeconds(5));await Success(a.Record,i,r);}finally{release.TrySetResult();}
            Assert.Equal("revision_conflict",(await rotation).Error);await using var db=fixture.CreateContext();Assert.False((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==challenge.ChallengeId)).Consumed);Assert.Equal(1,(await db.Collections.SingleAsync(x=>x.Id==a.Collection.Id)).KeyEpoch);Assert.Equal(2,(await Row(a.Record.Id)).Revision);
        }
    }
    [FunctionalFact]
    public async Task GivenDeepUnrelatedInventories_WhenUpdatingOneSharedTarget_ThenAnalyzeOneCandidateAndOnlyItsAncestors()
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,o);var child=await Folder(o,a.Folder.Id);var target=await Record(o,child.Id);await Members(o,a.Collection,[target.Id],[]);await AssociationSetup.Grant(fixture,o,owner,s,client,a.Collection,CollectionGrantAccess.ReadWrite);
        foreach(var account in new[]{o,s}){long? parent=null;for(var i=0;i<40;i++){var f=await Folder(account,parent);await Record(account,f.Id);parent=f.Id;}}
        var capture=new Capture();var input=Input();var result=await new RecordUpdateStore(new ProfileSetup.Factory(fixture,capture)).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,input),default);await Success(target,input,result);
        Assert.DoesNotContain(capture.Locks,x=>x.Contains("cerberus.folder"));Assert.Single(capture.Locks,x=>x.Contains("FROM cerberus.account "));Assert.Contains("WITH RECURSIVE",capture.Sql);Assert.Contains("WHERE r.public_id=@target",capture.Sql);
        await using var db=fixture.CreateContext();await db.Database.OpenConnectionAsync();await using var command=db.Database.GetDbConnection().CreateCommand();command.CommandText="EXPLAIN (ANALYZE, FORMAT JSON) "+capture.Sql;foreach(var p in capture.Parameters)command.Parameters.Add(p.Clone());using var plan=JsonDocument.Parse((string)(await command.ExecuteScalarAsync())!);
        static IEnumerable<JsonElement> Nodes(JsonElement node){yield return node;if(node.TryGetProperty("Plans",out var children))foreach(var child in children.EnumerateArray())foreach(var n in Nodes(child))yield return n;}
        var nodes=Nodes(plan.RootElement[0].GetProperty("Plan")).ToArray();Assert.Equal(1d,Assert.Single(nodes,x=>x.TryGetProperty("Subplan Name",out var n)&&n.GetString()=="CTE candidates").GetProperty("Actual Rows").GetDouble());Assert.Equal(2d,Assert.Single(nodes,x=>x.TryGetProperty("Subplan Name",out var n)&&n.GetString()=="CTE ancestry").GetProperty("Actual Rows").GetDouble());
    }
    private sealed class PauseSecondFolder(TaskCompletionSource reached,TaskCompletionSource release):DbCommandInterceptor
    {private int count;public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<int> r,CancellationToken ct=default){if(c.CommandText.Contains("FROM cerberus.folder ")&&c.CommandText.Contains("FOR UPDATE")&&++count==2){reached.TrySetResult();await release.Task.WaitAsync(ct);}return r;}}
    private sealed class PauseRotation(TaskCompletionSource reached,TaskCompletionSource release):DbCommandInterceptor
    {private bool used;public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> r,CancellationToken ct=default){if(!used&&c.CommandText.Contains("FROM cerberus.collection ")&&c.CommandText.Contains("FOR UPDATE")){used=true;reached.TrySetResult();await release.Task.WaitAsync(ct);}return r;}}
    private sealed class Capture:DbCommandInterceptor
    {
        public string Sql{get;private set;}="";public NpgsqlParameter[] Parameters{get;private set;}=[];public List<string> Locks{get;}=[];
        private void Note(DbCommand c){if(c.CommandText.Contains("FOR UPDATE")||c.CommandText.Contains("FOR SHARE"))Locks.Add(c.CommandText);}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<int> r,CancellationToken ct=default){Note(c);return ValueTask.FromResult(r);}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> r,CancellationToken ct=default){Note(c);if(Sql.Length==0&&c.CommandText.StartsWith("WITH RECURSIVE")){Sql=c.CommandText;Parameters=c.Parameters.Cast<NpgsqlParameter>().Select(p=>p.Clone()).ToArray();}return ValueTask.FromResult(r);}
    }
'''
s=s.replace('    private RecordUpdateStore Store()',extra+'\n    private RecordUpdateStore Store()');p.write_text(s)
print('Expanded cycle, unheldauthority, reciprocal/creation/actualrotation concurrency and analyzed target-plan tests.')
