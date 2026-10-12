using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

// Dynamic identifiers below are fixed test cases, never user input.
#pragma warning disable EF1003

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class CollectionTrashStoreTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenCallerCancellationDuringRealLockWait_WhenTrashing_ThenPropagateWithoutMutation()
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var before=await Snapshot(s);await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
        var reached=SignalSource();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));var pending=new CollectionTrashStore(new ProfileSetup.Factory(fixture,new Signal(reached,"account"))).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,1),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);await tx.RollbackAsync();Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenMultipleDirectMembersAndUnrelatedLinks_WhenTrashing_ThenSnapshotSortedDistinctIdsAndPreserveOtherMemberships()
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);var b=await AssociationSetup.Items(fixture,s);await Members(s,a);await Members(s,b);var p=await Profile(s,owner,key,[a.Collection.PublicId,b.Collection.PublicId]);
        await using(var db=fixture.CreateContext()){db.CollectionFolders.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,FolderId=b.Folder.Id});db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,RecordId=b.Record.Id});await db.SaveChangesAsync();}
        var other=await Full("collection",b.Collection.Id);var target=await Row(a.Collection.Id);await Success(target,await Trash(s,target,target.Revision));await using var read=fixture.CreateContext();var snapshot=JsonSerializer.Deserialize<CollectionAssociationSnapshot>((await read.TrashEntries.SingleAsync(x=>x.ResourceKind=="collection"&&x.ResourceId==target.PublicId)).AssociationSnapshot,ProtectionFixture.Json)!;
        Assert.Equal(new[]{a.Folder.PublicId,b.Folder.PublicId}.Order(),snapshot.FolderIds);Assert.Equal(new[]{a.Record.PublicId,b.Record.PublicId}.Order(),snapshot.RecordIds);Assert.Equal(new[]{p.PublicId},snapshot.ProfileIds);Assert.Equal(other,await Full("collection",b.Collection.Id));
        Assert.True(await read.ProfileCollections.AnyAsync(x=>x.ProfileId==p.Id&&x.CollectionId==b.Collection.Id));Assert.True(await read.CollectionFolders.AnyAsync(x=>x.CollectionId==b.Collection.Id&&x.FolderId==b.Folder.Id));Assert.True(await read.CollectionRecords.AnyAsync(x=>x.CollectionId==b.Collection.Id&&x.RecordId==b.Record.Id));
        foreach(var item in new[]{a,b}){Assert.Equal(2,(await read.Folders.SingleAsync(x=>x.Id==item.Folder.Id)).Revision);Assert.Equal(2,(await read.Records.SingleAsync(x=>x.Id==item.Record.Id)).Revision);}
    }
    [FunctionalTheory][InlineData("profile","sequence")][InlineData("folder","sequence")][InlineData("record","sequence")][InlineData("profile","unsafeSequence")][InlineData("folder","unsafeSequence")][InlineData("record","unsafeSequence")][InlineData("profile","time")][InlineData("folder","time")][InlineData("record","time")][InlineData("profile","purge")][InlineData("folder","purge")][InlineData("record","purge")]
    public async Task GivenCorruptRequiredLinkedMetadata_WhenTrashing_Then503AndNoMutation(string table,string kind)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);var p=await Profile(s,owner,key,[a.Collection.PublicId]);await using(var db=fixture.CreateContext())await db.Database.ExecuteSqlRawAsync("UPDATE cerberus."+table+" SET "+(kind switch{"sequence"=>"server_sequence=0","unsafeSequence"=>"server_sequence=9007199254740992","time"=>"edited_at='infinity'",_=>"purge_at=statement_timestamp()"})+" WHERE id={0}",table=="profile"?p.Id:table=="folder"?a.Folder.Id:a.Record.Id);
        var before=await Snapshot(s);Assert.Equal("persistence_unavailable",(await Trash(s,await Row(a.Collection.Id))).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("profile")][InlineData("folder")][InlineData("record")][InlineData("collection_grant")]
    public async Task GivenSecondaryMutationGuardMiss_WhenTrashing_Then409AndRollback(string table)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);await Profile(s,owner,key,[a.Collection.PublicId]);await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);var target=await Row(a.Collection.Id);var before=await Snapshot(s);
        var hook=new Suppress(c=>c.CommandText.StartsWith("UPDATE cerberus."+table+" SET"));Assert.Equal("revision_conflict",(await new CollectionTrashStore(new ProfileSetup.Factory(fixture,hook)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,target.Revision),default)).Error);Assert.True(hook.Triggered);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("profile_collection")][InlineData("collection_folder")][InlineData("collection_record")]
    public async Task GivenAssociationDeleteReportsNoRemoval_WhenTrashing_Then503AndRollback(string table)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);await Profile(s,owner,key,[a.Collection.PublicId]);var target=await Row(a.Collection.Id);var before=await Snapshot(s);var hook=new Suppress(c=>c.CommandText.Contains("DELETE FROM cerberus."+table+" "));
        Assert.Equal("persistence_unavailable",(await new CollectionTrashStore(new ProfileSetup.Factory(fixture,hook)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,target.Revision),default)).Error);Assert.True(hook.Triggered);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("profile")][InlineData("folder")][InlineData("record")][InlineData("collection_grant")][InlineData("trash_operation")][InlineData("trash_entry")][InlineData("retention_work_item")]
    public async Task GivenSecondaryWriteChangesRetainedFields_WhenTrashing_Then503AndRollback(string table)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);await Profile(s,owner,key,[a.Collection.PublicId]);await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);var target=await Row(a.Collection.Id);var before=await Snapshot(s);
        // Apply the tamper immediately before the next statement, after EF has consumed RETURNING.
        var hook=new Tamper(table,async c=>await Execute(c,table switch{"collection_grant"=>"UPDATE cerberus.collection_grant SET recipient_key_envelope='bad'::bytea WHERE collection_id=@target","trash_operation"=>"UPDATE cerberus.trash_operation SET purge_at=purge_at+interval '1 second' WHERE account_id=@account","trash_entry"=>"UPDATE cerberus.trash_entry SET association_snapshot='bad'::bytea WHERE resource_id=@public","retention_work_item"=>"UPDATE cerberus.retention_work_item SET due_at=due_at+interval '1 second' WHERE operation_key IN (SELECT 'trash/'||public_id FROM cerberus.trash_operation WHERE account_id=@account)",_=>"UPDATE cerberus."+table+" SET envelope='bad'::bytea WHERE account_id=@account"},new("target",target.Id),new("account",s.InternalId),new("public",target.PublicId)));
        Assert.Equal("persistence_unavailable",(await new CollectionTrashStore(new ProfileSetup.Factory(fixture,hook)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,target.Revision),default)).Error);Assert.True(hook.Triggered);Assert.Equal(before,await Snapshot(s));
    }
    private sealed class Suppress(Func<DbCommand,bool> match):DbCommandInterceptor
    {public bool Triggered{get;private set;}public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){if(!Triggered&&match(c)){Triggered=true;return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));}return ValueTask.FromResult(r);}}
    private sealed class Tamper(string table,Func<DbCommand,Task> action):DbCommandInterceptor
    {
        private bool ready;public bool Triggered{get;private set;}
        private void Note(DbCommand c){if(c.CommandText.StartsWith("UPDATE cerberus."+table+" SET")||c.CommandText.Contains("INSERT INTO cerberus."+table))ready=true;}
        private async Task Check(DbCommand c){if(ready&&!Triggered){Triggered=true;await action(c);}}
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default){Note(c);return ValueTask.FromResult(r);}
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData d,DbDataReader r,CancellationToken ct=default){Note(c);return ValueTask.FromResult(r);}
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){await Check(c);return r;}
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){await Check(c);return r;}
    }
    [FunctionalFact]
    public async Task GivenOneHundredUnrelatedCollections_WhenTrashing_ThenExplainSingleTargetAndLockOnlyCapturedOwnGraph()
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);var p=await Profile(s,owner,key,[a.Collection.PublicId]);var rp=await Profile(recipient,client,key,[]);var g=await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);
        await using(var db=fixture.CreateContext()){db.ProfileCollections.Add(new(){ProfileId=rp.Id,CollectionId=a.Collection.Id});db.Collections.AddRange(Enumerable.Range(0,100).Select(i=>new VaultCollection{PublicId=Guid.NewGuid(),AccountId=i<50?s.InternalId:recipient.InternalId,Envelope=Bytes(ProfileSetup.Envelope()),EditedAt=DateTimeOffset.UnixEpoch}));await db.SaveChangesAsync();}
        var capture=new Capture(true);var target=await Row(a.Collection.Id);await Success(target,await new CollectionTrashStore(new ProfileSetup.Factory(fixture,capture)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,target.Revision),default));
        Assert.Equal(s.Actor,Assert.Single(capture.AccountLockValues));Assert.DoesNotContain(capture.Locks,x=>x.Contains("vault_protection"));Assert.Equal(10,capture.Locks.Count);
        foreach(var pair in new[]{("profile",new[]{p.Id}),("folder",new[]{a.Folder.Id}),("record",new[]{a.Record.Id}),("collection_grant",new[]{g.Id})})
        {var values=Assert.Single(capture.LockParameters,x=>x.Sql.Contains("FROM cerberus."+pair.Item1+" ")).Values;Assert.Equal(pair.Item2,Assert.Single(values.OfType<long[]>()));}
        using var plan=JsonDocument.Parse(capture.Plan!);
        static IEnumerable<JsonElement> Nodes(JsonElement n){yield return n;if(n.TryGetProperty("Plans",out var children))foreach(var child in children.EnumerateArray())foreach(var nested in Nodes(child))yield return nested;}
        var candidate=Assert.Single(Nodes(plan.RootElement[0].GetProperty("Plan")),x=>x.TryGetProperty("Subplan Name",out var name)&&name.GetString()=="CTE target");Assert.Equal(1d,candidate.GetProperty("Actual Rows").GetDouble());
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenUnrelatedCorruptEvidence_WhenTrashing_ThenOnlyVisibleForeignEvidenceMatters(bool owned)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var target=await Collection(s);var other=await Collection(s);var good=await AssociationSetup.Grant(fixture,s,owner,recipient,client,target);var bad=await AssociationSetup.Grant(fixture,s,owner,recipient,client,other);
        await using(var db=fixture.CreateContext()){await db.CollectionGrants.Where(x=>x.Id==bad.Id||(owned&&x.Id==good.Id)).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.RecipientKeyEnvelope,"bad"u8.ToArray()));await db.Collections.Where(x=>x.Id==other.Id||(owned&&x.Id==target.Id)).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,"bad"u8.ToArray()));if(owned)await db.VaultProtections.Where(x=>x.AccountId==s.InternalId||x.AccountId==recipient.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Material,"bad"u8.ToArray()));}
        var before=await Snapshot(s);var actor=owned?s:recipient;var original=await Row(target.Id);var result=await Trash(actor,original);if(owned)await Success(original,result);else{Assert.Equal("vault_access_denied",result.Error);Assert.Equal(before,await Snapshot(s));}
    }
    [FunctionalTheory][InlineData("update",false)][InlineData("update",true)][InlineData("create",false)][InlineData("create",true)][InlineData("association",false)][InlineData("association",true)][InlineData("rotation",false)][InlineData("rotation",true)]
    public async Task GivenActualOwnerWriterInBothOrders_WhenTrashing_ThenSerializeAndPreserveWinningChanges(string writer,bool trashFirst)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);var p=await Profile(s,owner,key,[a.Collection.PublicId]);var target=await Row(a.Collection.Id);
        var wrappers=JsonSerializer.Deserialize<ProfileKeyWrappers>(p.KeyWrappers,ProtectionFixture.Json)!;
        var change=new ProtectionChange(s.AccountId,1,1,"rotate-content",owner.Rewrap(),[new("account",s.AccountId,1,ProfileSetup.Envelope(2)),new("collection",target.PublicId,target.Revision,ProfileSetup.Envelope(2)),new("folder",a.Folder.PublicId,1,ProfileSetup.Envelope(2)),new("record",a.Record.PublicId,1,ProfileSetup.Envelope(2)),new("profile",p.PublicId,p.Revision,ProfileSetup.Envelope(2),wrappers with{MasterKeyWrapper=owner.Wrap(s.AccountId,"profile",p.PublicId,s.Actor,2,p.Revision+1)})]);var raw=Bytes(change);var challenge=(await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;
        var reached=SignalSource();var release=SignalSource();var blocked=SignalSource();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(30));var newId=Guid.NewGuid();
        async Task<string?> Write(IDbContextFactory<AppDbContext> f)=>writer switch
        {
            "update"=>(await new CollectionUpdateStore(f).UpdateAsync(new(s.Actor,s.Verifier,target.PublicId,new(target.Revision,ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch)),ct.Token)).Error,
            "create"=>(await new CollectionCreateStore(f).CreateAsync(new(s.Actor,s.Verifier,new(newId,ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch,[p.PublicId],[a.Record.PublicId],[a.Folder.PublicId])),ct.Token)).Error,
            "association"=>(await new ProfileAssociationStore(f).SetAsync(new(s.Actor,s.Verifier,p.PublicId,new(p.Revision,[],[],[])),ct.Token)).Error,
            _=>(await new VaultProtectionChangeStore(f).ChangeAsync(new(s.Actor,s.Verifier,challenge.ChallengeId,owner.Sign(challenge),raw,change),ct.Token)).Error
        };
        var pause=new AfterAccount(reached,release);Task<string?> write;Task<VaultResult<CollectionTrashDetails>> trash;
        if(trashFirst){trash=new CollectionTrashStore(new ProfileSetup.Factory(fixture,pause)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,target.Revision),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));write=Write(new ProfileSetup.Factory(fixture,new Signal(blocked,"account")));}
        else{write=Write(new ProfileSetup.Factory(fixture,pause));await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));trash=new CollectionTrashStore(new ProfileSetup.Factory(fixture,new Signal(blocked,"account"))).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,target.Revision),ct.Token);}
        try{await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(trashFirst?write.IsCompleted:trash.IsCompleted);}finally{release.TrySetResult();}await Task.WhenAll(trash,write);
        Assert.Equal(trashFirst?writer=="update"?"not_found":writer is "association" or "rotation"?"revision_conflict":null:null,write.Result);
        Assert.Equal(!trashFirst?writer=="update"?"revision_conflict":writer=="rotation"?"vault_access_denied":null:null,trash.Result.Error);
        if(writer=="create"){await using var db=fixture.CreateContext();Assert.True(await db.Collections.AnyAsync(x=>x.PublicId==newId&&x.DeletedAt==null));Assert.Equal(3,(await db.Folders.SingleAsync(x=>x.Id==a.Folder.Id)).Revision);}
        if(writer=="rotation"){await using var db=fixture.CreateContext();Assert.Equal(!trashFirst,(await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==challenge.ChallengeId)).Consumed);}
    }

    [FunctionalTheory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task GivenActualNativeRecipientWriterInBothOrders_WhenTrashing_ThenAvoidCrossOwnerDeadlockAndPreserveWinner(bool record,bool trashFirst)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection,CollectionGrantAccess.ReadWrite);
        var reached=SignalSource();var release=SignalSource();var blocked=SignalSource();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(25));
        async Task<string?> Edit(IDbContextFactory<AppDbContext> f)=>record?(await new RecordUpdateStore(f).UpdateAsync(new(recipient.Actor,recipient.Verifier,a.Record.PublicId,new(1,ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch)),ct.Token)).Error:(await new FolderUpdateStore(f).UpdateAsync(new(recipient.Actor,recipient.Verifier,a.Folder.PublicId,new(1,ProfileSetup.Envelope(),DateTimeOffset.UnixEpoch)),ct.Token)).Error;
        Task<string?> edit;Task<VaultResult<CollectionTrashDetails>> trash;
        if(trashFirst){trash=new CollectionTrashStore(new ProfileSetup.Factory(fixture,new Before(RootWrite,async _=>{reached.TrySetResult();await release.Task.WaitAsync(ct.Token);}))).TrashAsync(new(s.Actor,s.Verifier,a.Collection.PublicId,1),ct.Token);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));edit=Edit(new ProfileSetup.Factory(fixture,new Signal(blocked,"collection")));}
        else{edit=Edit(new ProfileSetup.Factory(fixture,new Before(c=>c.CommandText.Contains(record?"UPDATE cerberus.record AS r":"UPDATE cerberus.folder AS r"),async _=>{reached.TrySetResult();await release.Task.WaitAsync(ct.Token);})));await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));trash=new CollectionTrashStore(new ProfileSetup.Factory(fixture,new Signal(blocked,"collection"))).TrashAsync(new(s.Actor,s.Verifier,a.Collection.PublicId,1),ct.Token);}
        try{await blocked.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(trashFirst?edit.IsCompleted:trash.IsCompleted);}finally{release.TrySetResult();}await Task.WhenAll(edit,trash);
        Assert.Equal(trashFirst?"not_found":null,edit.Result);Assert.Equal(trashFirst?null:"revision_conflict",trash.Result.Error);await using var db=fixture.CreateContext();Assert.Null((await db.Records.SingleAsync(x=>x.Id==a.Record.Id)).DeletedAt);Assert.Null((await db.Folders.SingleAsync(x=>x.Id==a.Folder.Id)).DeletedAt);
    }

    private sealed class AfterAccount(TaskCompletionSource reached,TaskCompletionSource release):DbCommandInterceptor
    {
        private bool used;
        private async Task Check(DbCommand c,CancellationToken ct){if(!used&&Lock(c,"account")){used=true;reached.TrySetResult();await release.Task.WaitAsync(ct);}}
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default){await Check(c,ct);return r;}
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData d,DbDataReader r,CancellationToken ct=default){await Check(c,ct);return r;}
    }
    [FunctionalTheory][InlineData("account")][InlineData("vault_access_session")][InlineData("profile")][InlineData("collection")][InlineData("folder")][InlineData("record")][InlineData("collection_grant")][InlineData("profile_collection")][InlineData("collection_folder")][InlineData("collection_record")]
    public async Task GivenNaturalExpiryAtEachLockWait_WhenTrashing_ThenDenyBeforeStaleRevision(string table)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);var p=await Profile(s,owner,key,[a.Collection.PublicId]);var g=await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);s=s with{Verifier=await Selected(s,p.Id)};
        var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));var before=await Snapshot(s);
        await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();var id=table switch{"account"=>s.InternalId,"profile"=>p.Id,"folder"=>a.Folder.Id,"record"=>a.Record.Id,"collection_grant"=>g.Id,_=>a.Collection.Id};
        var where=table=="vault_access_session"?"handle_verifier={0}":table is "profile_collection" or "collection_folder" or "collection_record"?"collection_id={0}":"id={0}";await held.Database.ExecuteSqlRawAsync("SELECT FROM cerberus."+table+" WHERE "+where+" FOR UPDATE",table=="vault_access_session"?(object)s.Verifier:id);
        var reached=SignalSource();using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));var pending=new CollectionTrashStore(new ProfileSetup.Factory(fixture,new Signal(reached,table))).TrashAsync(new(s.Actor,s.Verifier,a.Collection.PublicId,77),ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);await Past(deadline);await tx.CommitAsync();Assert.Equal("vault_access_denied",(await pending).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("PC")][InlineData("CF")][InlineData("CR")][InlineData("grant")]
    public async Task GivenMembershipOrGrantGrowthBeforeTargetLock_WhenTrashing_ThenConflictPreservesWinningGraph(string kind)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);var p=await Profile(s,owner,key,[]);string? winning=null;
        var hook=new Before(c=>Lock(c,"collection"),async _=>{await using var db=fixture.CreateContext();if(kind=="PC")db.ProfileCollections.Add(new(){ProfileId=p.Id,CollectionId=a.Collection.Id});if(kind=="CF")db.CollectionFolders.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,FolderId=a.Folder.Id});if(kind=="CR")db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=a.Collection.Id,RecordId=a.Record.Id});if(kind=="grant")await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);await db.SaveChangesAsync();winning=await Snapshot(s);});
        Assert.Equal("revision_conflict",(await new CollectionTrashStore(new ProfileSetup.Factory(fixture,hook)).TrashAsync(new(s.Actor,s.Verifier,a.Collection.PublicId,1),default)).Error);Assert.True(hook.Triggered);Assert.NotNull(winning);Assert.Equal(winning,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("PC")][InlineData("CF")][InlineData("CR")][InlineData("grant")]
    public async Task GivenHarmlessLinkDisappearanceBeforeTargetLock_WhenTrashing_ThenCaptureOnlyFreshRemainingInventory(string kind)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);var p=await Profile(s,owner,key,[a.Collection.PublicId]);var g=await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);var target=await Row(a.Collection.Id);
        var hook=new Before(c=>Lock(c,"collection"),async _=>{await using var db=fixture.CreateContext();if(kind=="PC")await db.ProfileCollections.Where(x=>x.CollectionId==target.Id).ExecuteDeleteAsync();if(kind=="CF")await db.CollectionFolders.Where(x=>x.CollectionId==target.Id).ExecuteDeleteAsync();if(kind=="CR")await db.CollectionRecords.Where(x=>x.CollectionId==target.Id).ExecuteDeleteAsync();if(kind=="grant")await db.CollectionGrants.Where(x=>x.Id==g.Id).ExecuteDeleteAsync();});
        await Success(target,await new CollectionTrashStore(new ProfileSetup.Factory(fixture,hook)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,target.Revision),default));Assert.True(hook.Triggered);
        await using var read=fixture.CreateContext();var snapshot=JsonSerializer.Deserialize<CollectionAssociationSnapshot>((await read.TrashEntries.SingleAsync(x=>x.ResourceId==target.PublicId&&x.ResourceKind=="collection")).AssociationSnapshot,ProtectionFixture.Json)!;
        Assert.Equal(kind=="PC"?Array.Empty<Guid>():[p.PublicId],snapshot.ProfileIds);Assert.Equal(kind=="CF"?Array.Empty<Guid>():[a.Folder.PublicId],snapshot.FolderIds);Assert.Equal(kind=="CR"?Array.Empty<Guid>():[a.Record.PublicId],snapshot.RecordIds);
        Assert.Equal(p.Revision+(kind=="PC"?0:1),(await read.Profiles.SingleAsync(x=>x.Id==p.Id)).Revision);Assert.Equal(1+(kind=="CF"?0:1),(await read.Folders.SingleAsync(x=>x.Id==a.Folder.Id)).Revision);Assert.Equal(1+(kind=="CR"?0:1),(await read.Records.SingleAsync(x=>x.Id==a.Record.Id)).Revision);
    }

    [FunctionalTheory][InlineData("expiry","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("selection","revision_conflict")][InlineData("profileTrash","vault_access_denied")][InlineData("profileTerminal","vault_access_denied")][InlineData("targetTrash","not_found")][InlineData("targetTerminal","not_found")][InlineData("actorTerminal","not_found")][InlineData("removePC","not_found")][InlineData("removeCF","revision_conflict")][InlineData("grant","revision_conflict")][InlineData("stamp","revision_conflict")][InlineData("sequence","revision_conflict")][InlineData("epoch","revision_conflict")]
    public async Task GivenChangeAtFinalGuard_WhenTrashing_ThenReclassifyFreshAuthorityAndRollback(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);var p=await Profile(s,owner,key,[a.Collection.PublicId]);var g=await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);s=s with{Verifier=await Selected(s,p.Id)};var before=await Snapshot(s);var target=await Row(a.Collection.Id);
        var hook=new Before(RootWrite,async command=>
        {
            var sql=kind switch{"expiry"=>"UPDATE cerberus.vault_access_session SET expires_at=statement_timestamp()-interval '1 second' WHERE handle_verifier=@verifier","policy"=>"UPDATE cerberus.account SET policy_revision=policy_revision+1 WHERE id=@account","generation"=>"UPDATE cerberus.account SET revocation_generation=revocation_generation+1 WHERE id=@account","revoked"=>"UPDATE cerberus.vault_access_session SET revoked=true WHERE handle_verifier=@verifier","selection"=>"UPDATE cerberus.vault_access_session SET profile_id=NULL WHERE handle_verifier=@verifier","profileTrash"=>"UPDATE cerberus.profile SET deleted_at=statement_timestamp() WHERE id=@profile","targetTrash"=>"UPDATE cerberus.collection SET deleted_at=statement_timestamp() WHERE id=@collection","removePC"=>"DELETE FROM cerberus.profile_collection WHERE collection_id=@collection","removeCF"=>"DELETE FROM cerberus.collection_folder WHERE collection_id=@collection","grant"=>"UPDATE cerberus.collection_grant SET state=1 WHERE id=@grant","stamp"=>"UPDATE cerberus.collection SET concurrency_stamp=@stamp WHERE id=@collection","sequence"=>"UPDATE cerberus.collection SET server_sequence=server_sequence+1 WHERE id=@collection","epoch"=>"UPDATE cerberus.collection SET key_epoch=key_epoch+1 WHERE id=@collection",_=>"INSERT INTO cerberus.terminal_erasure(resource_id,resource_kind,deleted_at) VALUES(@terminal,@kind,statement_timestamp())"};
            await Execute(command,sql,new("verifier",s.Verifier),new("account",s.InternalId),new("profile",p.Id),new("collection",target.Id),new("grant",g.Id),new("stamp",Guid.NewGuid()),new("terminal",kind=="actorTerminal"?s.AccountId:kind=="profileTerminal"?p.PublicId:target.PublicId),new("kind",kind=="actorTerminal"?"account":kind=="profileTerminal"?"profile":"collection"));
        });
        Assert.Equal(error,(await new CollectionTrashStore(new ProfileSetup.Factory(fixture,hook)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,target.Revision),default)).Error);Assert.True(hook.Triggered);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("collection")][InlineData("profile_collection")][InlineData("collection_folder")][InlineData("collection_record")][InlineData("profile")][InlineData("folder")][InlineData("record")][InlineData("collection_grant")][InlineData("trash_operation")][InlineData("trash_entry")][InlineData("retention_work_item")]
    public async Task GivenFaultAfterEachMutationStage_WhenTrashing_ThenRollbackCompleteStateAndRetry(string stage)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();using var client=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);await Profile(s,owner,key,[a.Collection.PublicId]);await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);var target=await Row(a.Collection.Id);var before=await Snapshot(s);var other=await Snapshot(recipient);var fail=new After(stage,_=>throw new TimeoutException("synthetic post-stage fault"));
        Assert.Equal("persistence_unavailable",(await new CollectionTrashStore(new ProfileSetup.Factory(fixture,fail)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,target.Revision),default)).Error);Assert.True(fail.Triggered);Assert.Equal(before,await Snapshot(s));Assert.Equal(other,await Snapshot(recipient));await Success(target,await Trash(s,target,target.Revision));
    }

    [FunctionalTheory][InlineData("collection",false)][InlineData("collection",true)][InlineData("profile",false)][InlineData("profile",true)][InlineData("folder",false)][InlineData("folder",true)][InlineData("record",false)][InlineData("record",true)]
    public async Task GivenSequenceExhaustionOrRegressionAtEachResource_WhenTrashing_ThenRollback(string stage,bool regression)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);await Profile(s,owner,key,[a.Collection.PublicId]);var target=await Row(a.Collection.Id);var before=await Snapshot(s);await using var db=fixture.CreateContext();var previous=await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();
        var hook=new Before(c=>stage=="collection"?RootWrite(c):c.CommandText.StartsWith("UPDATE cerberus."+stage+" SET"),async _=>{await using var other=fixture.CreateContext();await other.Database.ExecuteSqlRawAsync(regression?"SELECT setval('cerberus.server_sequence',1,false)":"SELECT setval('cerberus.server_sequence',9007199254740991,true)");});
        try{Assert.Equal(regression?"persistence_unavailable":"revision_conflict",(await new CollectionTrashStore(new ProfileSetup.Factory(fixture,hook)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,target.Revision),default)).Error);Assert.True(hook.Triggered);Assert.Equal(before,await Snapshot(s));}finally{await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{previous},true)");}
    }

    [FunctionalTheory][InlineData("revision")][InlineData("sequence")][InlineData("stamp")][InlineData("epoch")][InlineData("envelope")][InlineData("time")][InlineData("deleted")][InlineData("purge")]
    public async Task GivenTamperedPostWriteMetadata_WhenTrashing_ThenVerificationRollsBack(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var before=await Snapshot(s);
        var hook=new After("collection",c=>Execute(c,"UPDATE cerberus.collection SET "+(kind switch{"revision"=>"revision=revision+1","sequence"=>"server_sequence=0","stamp"=>"concurrency_stamp=@stamp","epoch"=>"key_epoch=2","envelope"=>"envelope='bad'::bytea","time"=>"edited_at='2000-01-01'","deleted"=>"deleted_at=NULL",_=>"purge_at=purge_at+interval '1 second'"})+" WHERE id=@id",new("id",target.Id),new("stamp",target.ConcurrencyStamp)));
        Assert.Equal("persistence_unavailable",(await new CollectionTrashStore(new ProfileSetup.Factory(fixture,hook)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,1),default)).Error);Assert.True(hook.Triggered);Assert.Equal(before,await Snapshot(s));
    }

    private static bool RootWrite(DbCommand c)=>c.CommandText.Contains("UPDATE cerberus.collection AS");
    private static bool Lock(DbCommand c,string table)=>c.CommandText.Contains("FROM cerberus."+table+" ")&&c.CommandText.Contains("FOR ");
    private static async Task Execute(DbCommand original,string sql,params NpgsqlParameter[] parameters){await using var c=original.Connection!.CreateCommand();c.Transaction=original.Transaction;c.CommandText=sql;c.Parameters.AddRange(parameters);await c.ExecuteNonQueryAsync();}
    private sealed class Before(Func<DbCommand,bool> match,Func<DbCommand,Task> action):DbCommandInterceptor
    {
        public bool Triggered{get;private set;}
        private async Task Check(DbCommand c){if(!Triggered&&match(c)){Triggered=true;await action(c);}}
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){await Check(c);return r;}
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){await Check(c);return r;}
    }
    private sealed class After(string table,Func<DbCommand,Task> action):DbCommandInterceptor
    {
        public bool Triggered{get;private set;}
        private async Task Check(DbCommand c){if(!Triggered&&(table=="collection"?RootWrite(c):c.CommandText.Contains("UPDATE cerberus."+table+" SET")||c.CommandText.Contains("DELETE FROM cerberus."+table+" ")||c.CommandText.Contains("INSERT INTO cerberus."+table+" ")||c.CommandText.Contains("INSERT INTO cerberus."+table+"("))){Triggered=true;await action(c);}}
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default){await Check(c);return r;}
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData d,DbDataReader r,CancellationToken ct=default){if(c.CommandText.Contains("INSERT INTO cerberus."+table)){await r.DisposeAsync();await Check(c);}return r;}
    }
    private sealed class Signal(TaskCompletionSource reached,string table):DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){if(Lock(c,table))reached.TrySetResult();return ValueTask.FromResult(r);}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){if(Lock(c,table))reached.TrySetResult();return ValueTask.FromResult(r);}
    }
    [FunctionalTheory][InlineData(false,"Master")][InlineData(true,"Master")][InlineData(true,"PerProfile")]
    public async Task GivenOwnedCollectionWithCompleteGraph_WhenTrashing_ThenRetainContentSnapshotAllLinksAndRevokeGrants(bool selected,string mode)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();using var former=new ProtectionFixture();
        var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var oldRecipient=await ProfileSetup.Create(fixture,former);
        var a=await AssociationSetup.Items(fixture,s);var unrelated=await AssociationSetup.Items(fixture,s);await Members(s,a);
        var p=await Profile(s,owner,key,[a.Collection.PublicId],mode);var rp=await Profile(recipient,client,key,[],mode);
        var grant=await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection,CollectionGrantAccess.ReadWrite);
        var revoked=await AssociationSetup.Grant(fixture,s,owner,oldRecipient,former,a.Collection,CollectionGrantAccess.ReadOnly);
        await using(var db=fixture.CreateContext())
        {
            db.ProfileCollections.Add(new(){ProfileId=rp.Id,CollectionId=a.Collection.Id});
            db.ProfileRecords.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,RecordId=a.Record.Id});db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,FolderId=a.Folder.Id});
            await db.Records.Where(x=>x.Id==a.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.FolderId,a.Folder.Id));
            await db.CollectionGrants.Where(x=>x.Id==revoked.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.State,CollectionGrantState.Revoked));await db.SaveChangesAsync();
        }
        if(selected)s=s with{Verifier=await Selected(s,p.Id)};recipient=recipient with{Verifier=await Selected(recipient,rp.Id)};
        Assert.Null((await new CollectionReadStore(fixture).ReadAsync(new(recipient.Actor,recipient.Verifier,a.Collection.PublicId),default)).Error);
        Assert.Null((await new RecordReadStore(fixture).ReadAsync(new(recipient.Actor,recipient.Verifier,a.Record.PublicId),default)).Error);
        Assert.Null((await new FolderReadStore(fixture).ReadAsync(new(recipient.Actor,recipient.Verifier,a.Folder.PublicId),default)).Error);
        var original=await Row(a.Collection.Id);var ps=await Stable("profile",p.Id);var fs=await Stable("folder",a.Folder.Id);var rs=await Stable("record",a.Record.Id);
        var recipientProfile=await Full("profile",rp.Id);var unrelatedState=await Full("collection",unrelated.Collection.Id);var revokedState=await Full("collection_grant",revoked.Id);
        await Success(original,await Trash(s,original));
        await using var read=fixture.CreateContext();Assert.False(await read.ProfileCollections.AnyAsync(x=>x.CollectionId==original.Id));Assert.False(await read.CollectionFolders.AnyAsync(x=>x.CollectionId==original.Id));Assert.False(await read.CollectionRecords.AnyAsync(x=>x.CollectionId==original.Id));
        var entry=await read.TrashEntries.SingleAsync(x=>x.ResourceKind=="collection"&&x.ResourceId==original.PublicId);var snapshot=JsonSerializer.Deserialize<CollectionAssociationSnapshot>(entry.AssociationSnapshot,ProtectionFixture.Json)!;
        Assert.Equal(new[]{p.PublicId,rp.PublicId}.Order(),snapshot.ProfileIds);Assert.Equal(new[]{a.Record.PublicId},snapshot.RecordIds);Assert.Equal(new[]{a.Folder.PublicId},snapshot.FolderIds);
        Assert.Equal(ps,await Stable("profile",p.Id));Assert.Equal(fs,await Stable("folder",a.Folder.Id));Assert.Equal(rs,await Stable("record",a.Record.Id));
        await Bumped("profile",p.Id,p.Revision,p.ServerSequence,p.ConcurrencyStamp);await Bumped("folder",a.Folder.Id,a.Folder.Revision,a.Folder.ServerSequence,a.Folder.ConcurrencyStamp);await Bumped("record",a.Record.Id,a.Record.Revision,a.Record.ServerSequence,a.Record.ConcurrencyStamp);
        Assert.Equal(recipientProfile,await Full("profile",rp.Id));Assert.Equal(unrelatedState,await Full("collection",unrelated.Collection.Id));Assert.Equal(revokedState,await Full("collection_grant",revoked.Id));
        var revokedNow=await read.CollectionGrants.AsNoTracking().SingleAsync(x=>x.Id==grant.Id);Assert.Equal(CollectionGrantState.Revoked,revokedNow.State);Assert.Equal(2,revokedNow.Revision);Assert.Equal(grant.PublicId,revokedNow.PublicId);Assert.Equal(grant.Access,revokedNow.Access);Assert.Equal(grant.RecipientAccountId,revokedNow.RecipientAccountId);Assert.Equal(grant.RecipientKeyEnvelope,revokedNow.RecipientKeyEnvelope);
        Assert.True(await read.ProfileRecords.AnyAsync(x=>x.ProfileId==p.Id&&x.RecordId==a.Record.Id));Assert.True(await read.ProfileFolders.AnyAsync(x=>x.ProfileId==p.Id&&x.FolderId==a.Folder.Id));Assert.False((await read.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==recipient.Verifier)).Revoked);
        foreach(var actor in new[]{s,recipient}){Assert.Equal("not_found",(await new CollectionReadStore(fixture).ReadAsync(new(actor.Actor,actor.Verifier,original.PublicId),default)).Error);var list=await new CollectionListStore(fixture).ListAsync(new(actor.Actor,actor.Verifier,100,0,null),default);Assert.Null(list.Error);Assert.DoesNotContain(list.Data!.Items,x=>x.CollectionId==original.PublicId);}
        Assert.Equal("not_found",(await new RecordReadStore(fixture).ReadAsync(new(recipient.Actor,recipient.Verifier,a.Record.PublicId),default)).Error);Assert.Equal("not_found",(await new FolderReadStore(fixture).ReadAsync(new(recipient.Actor,recipient.Verifier,a.Folder.PublicId),default)).Error);
        Assert.Empty((await new RecordListStore(fixture).ListAsync(new(recipient.Actor,recipient.Verifier,100,0,null),default)).Data!.Items);Assert.Empty((await new FolderListStore(fixture).ListAsync(new(recipient.Actor,recipient.Verifier,100,0,null),default)).Data!.Items);
        Assert.Null((await new RecordReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,a.Record.PublicId),default)).Error);Assert.Null((await new FolderReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,a.Folder.PublicId),default)).Error);
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenInactiveMembersAndDamagedOwnedCiphertext_WhenTrashing_ThenSnapshotLinksWithoutBumpingInactiveRows(bool terminal)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);var p=await Profile(s,owner,key,[a.Collection.PublicId]);
        await using(var db=fixture.CreateContext())
        {
            await db.Collections.Where(x=>x.Id==a.Collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Envelope,"damaged"u8.ToArray()));
            foreach(var item in new[]{("profile",p.Id,p.PublicId),("folder",a.Folder.Id,a.Folder.PublicId),("record",a.Record.Id,a.Record.PublicId)})
                if(terminal){db.TerminalErasures.Add(new(){ResourceKind=item.Item1,ResourceId=item.Item3,DeletedAt=DateTimeOffset.UtcNow});}else await db.Database.ExecuteSqlRawAsync("UPDATE cerberus."+item.Item1+" SET deleted_at=statement_timestamp(),purge_at=statement_timestamp()+interval '720 hours',revision=0 WHERE id={0}",item.Item2);
            await db.SaveChangesAsync();
        }
        var states=await Task.WhenAll(Full("profile",p.Id),Full("folder",a.Folder.Id),Full("record",a.Record.Id));var target=await Row(a.Collection.Id);await Success(target,await Trash(s,target));
        Assert.Equal(states,await Task.WhenAll(Full("profile",p.Id),Full("folder",a.Folder.Id),Full("record",a.Record.Id)));
        await using var read=fixture.CreateContext();var snapshot=JsonSerializer.Deserialize<CollectionAssociationSnapshot>((await read.TrashEntries.SingleAsync(x=>x.ResourceId==target.PublicId&&x.ResourceKind=="collection")).AssociationSnapshot,ProtectionFixture.Json)!;Assert.Equal(new[]{p.PublicId},snapshot.ProfileIds);Assert.Equal(new[]{a.Folder.PublicId},snapshot.FolderIds);Assert.Equal(new[]{a.Record.PublicId},snapshot.RecordIds);
        Assert.False(await read.ProfileCollections.AnyAsync(x=>x.CollectionId==target.Id));Assert.False(await read.CollectionFolders.AnyAsync(x=>x.CollectionId==target.Id));Assert.False(await read.CollectionRecords.AnyAsync(x=>x.CollectionId==target.Id));
    }

    [FunctionalTheory][InlineData("actor","authentication_required")][InlineData("handle","vault_access_required")][InlineData("id","validation_failed")][InlineData("zero","validation_failed")][InlineData("unsafe","validation_failed")]
    public async Task GivenInvalidInput_WhenTrashing_ThenRejectBeforeOpeningPersistence(string kind,string error)
    {var store=new CollectionTrashStore(new ThrowingFactory(new Exception("must not open")));var result=await store.TrashAsync(new(kind=="actor"?Guid.Empty:Guid.NewGuid(),kind=="handle"?"":new string('a',64),kind=="id"?Guid.Empty:Guid.NewGuid(),kind=="zero"?0:kind=="unsafe"?ProtocolBinary.MaxInteger+1:1),default);Assert.Equal(error,result.Error);Assert.Null(result.Data);}

    [FunctionalTheory][InlineData("stale","revision_conflict")][InlineData("max","revision_conflict")][InlineData("entry","revision_conflict")]
    [InlineData("revision","persistence_unavailable")][InlineData("unsafeRevision","persistence_unavailable")][InlineData("sequence","persistence_unavailable")][InlineData("unsafeSequence","persistence_unavailable")][InlineData("epoch","persistence_unavailable")][InlineData("unsafeEpoch","persistence_unavailable")][InlineData("time","persistence_unavailable")][InlineData("infinity","persistence_unavailable")][InlineData("purge","persistence_unavailable")]
    public async Task GivenInvalidStoredTarget_WhenTrashing_ThenPreserveCompleteState(string kind,string error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);
        await using(var db=fixture.CreateContext())
        {
            var c=await db.Collections.SingleAsync(x=>x.Id==target.Id);if(kind=="max")c.Revision=ProtocolBinary.MaxInteger;if(kind=="revision")c.Revision=0;if(kind=="unsafeRevision")c.Revision=ProtocolBinary.MaxInteger+1;if(kind=="sequence")c.ServerSequence=0;if(kind=="unsafeSequence")c.ServerSequence=ProtocolBinary.MaxInteger+1;if(kind=="epoch")c.KeyEpoch=0;if(kind=="unsafeEpoch")c.KeyEpoch=ProtocolBinary.MaxInteger+1;if(kind=="purge")c.PurgeAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();
            if(kind is "time" or "infinity")await db.Database.ExecuteSqlRawAsync("UPDATE cerberus.collection SET edited_at="+(kind=="time"?"'0001-01-01 00:00:00+00'":"'infinity'")+" WHERE id={0}",target.Id);
            if(kind=="entry"){var operation=new TrashOperation{AccountId=s.InternalId,RootResourceKind="collection",RootResourceId=target.PublicId,DeletedAt=DateTimeOffset.UnixEpoch,PurgeAt=DateTimeOffset.UnixEpoch.AddDays(30)};db.TrashOperations.Add(operation);await db.SaveChangesAsync();db.TrashEntries.Add(new(){OperationId=operation.Id,ResourceKind="collection",ResourceId=target.PublicId,AssociationSnapshot=Bytes(new CollectionAssociationSnapshot([],[],[]))});await db.SaveChangesAsync();}
        }
        var before=await Snapshot(s);Assert.Equal(error,(await Trash(s,target,kind=="stale"?77:kind=="max"?ProtocolBinary.MaxInteger:1)).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData("profile",false)][InlineData("profile",true)][InlineData("folder",false)][InlineData("folder",true)][InlineData("record",false)][InlineData("record",true)][InlineData("grant",false)][InlineData("grant",true)]
    public async Task GivenInvalidOrExhaustedLinkedRevision_WhenTrashing_ThenRollbackEntireGraph(string kind,bool exhausted)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var recipient=await ProfileSetup.Create(fixture,client);var a=await AssociationSetup.Items(fixture,s);await Members(s,a);var p=await Profile(s,owner,key,[a.Collection.PublicId]);var g=await AssociationSetup.Grant(fixture,s,owner,recipient,client,a.Collection);
        await using(var db=fixture.CreateContext())await db.Database.ExecuteSqlRawAsync("UPDATE cerberus."+(kind=="grant"?"collection_grant":kind)+" SET revision={0} WHERE id={1}",exhausted?ProtocolBinary.MaxInteger:0,kind=="profile"?p.Id:kind=="folder"?a.Folder.Id:kind=="record"?a.Record.Id:g.Id);
        var before=await Snapshot(s);Assert.Equal(exhausted?"revision_conflict":"persistence_unavailable",(await Trash(s,await Row(a.Collection.Id))).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenConcurrentDeleteAndLostResponseRetry_WhenTrashing_ThenExactlyOneOperationAndDeadline()
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var results=await Task.WhenAll(Trash(s,target),Trash(s,target));var success=Assert.Single(results,x=>x.Error is null);Assert.Equal("not_found",Assert.Single(results,x=>x.Error is not null).Error);await Success(target,success);var before=await Snapshot(s);Assert.Equal("not_found",(await Trash(s,target)).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenRetainedCollection_WhenRotatingProtection_ThenRequireRetainedInventoryAndPreserveTrash(bool omit)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var trash=await Trash(s,target);Assert.Null(trash.Error);ContentReplacement[] all=[new("account",s.AccountId,1,ProfileSetup.Envelope(2)),new("collection",target.PublicId,2,ProfileSetup.Envelope(2))];var change=new ProtectionChange(s.AccountId,1,1,"rotate-content",owner.Rewrap(),omit?all[..1]:all);var raw=Bytes(change);var challenge=(await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;var result=await new VaultProtectionChangeStore(fixture).ChangeAsync(new(s.Actor,s.Verifier,challenge.ChallengeId,owner.Sign(challenge),raw,change),default);Assert.Equal(omit?"validation_failed":null,result.Error);var row=await Row(target.Id);Assert.Equal(trash.Data!.DeletedAt,row.DeletedAt);Assert.Equal(trash.Data.PurgeAt,row.PurgeAt);Assert.Equal(omit?2:3,row.Revision);Assert.Equal(omit?1:2,row.KeyEpoch);
    }

    private async Task<string> Full(string table,long id){await using var db=fixture.CreateContext();return await db.Database.SqlQueryRaw<string>("SELECT to_jsonb(t)::text AS \"Value\" FROM cerberus."+table+" t WHERE id={0}",id).SingleAsync();}
    private async Task<string> Stable(string table,long id){await using var db=fixture.CreateContext();return await db.Database.SqlQueryRaw<string>("SELECT (to_jsonb(t)-'revision'-'server_sequence'-'concurrency_stamp')::text AS \"Value\" FROM cerberus."+table+" t WHERE id={0}",id).SingleAsync();}
    private async Task Bumped(string table,long id,long revision,long sequence,Guid stamp){await using var db=fixture.CreateContext();var row=await db.Database.SqlQueryRaw<Metadata>("SELECT revision,server_sequence,concurrency_stamp FROM cerberus."+table+" WHERE id={0}",id).SingleAsync();Assert.Equal(revision+1,row.Revision);Assert.True(row.ServerSequence>sequence);Assert.NotEqual(stamp,row.ConcurrencyStamp);}
    private sealed class Metadata{public long Revision{get;set;}public long ServerSequence{get;set;}public Guid ConcurrencyStamp{get;set;}}
    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly,false)][InlineData(CollectionGrantAccess.ReadOnly,true)]
    [InlineData(CollectionGrantAccess.ReadWrite,false)][InlineData(CollectionGrantAccess.ReadWrite,true)]
    public async Task GivenValidVisibleNativeRecipient_WhenTrashingWithStaleRevision_Then403AndNoMutation(CollectionGrantAccess access,bool selected)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();
        var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var target=await Collection(o);
        await AssociationSetup.Grant(fixture,o,owner,s,client,target,access);
        if(selected){var p=await Profile(s,client,key,[target.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};}
        var before=await Snapshot(o);var recipient=await Snapshot(s);var capture=new Capture();
        var r=await new CollectionTrashStore(new ProfileSetup.Factory(fixture,capture)).TrashAsync(new(s.Actor,s.Verifier,target.PublicId,77),default);
        Assert.Equal("vault_access_denied",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(o));Assert.Equal(recipient,await Snapshot(s));
        Assert.Single(capture.Locks,x=>x.Contains("FROM cerberus.account "));
        Assert.DoesNotContain(capture.Locks,x=>x.Contains("cerberus.collection ")||x.Contains("cerberus.collection_grant")||x.Contains("cerberus.vault_protection"));
    }


    [FunctionalTheory][InlineData("missing")][InlineData("ungranted")][InlineData("unselectedOwner")][InlineData("unselectedForeign")]
    [InlineData("deleted")][InlineData("terminal")][InlineData("ownerClosing")][InlineData("ownerTerminal")]
    [InlineData("revoked")][InlineData("grantTerminal")][InlineData("invalidAccess")][InlineData("zeroGrantRevision")][InlineData("unsafeGrantRevision")]
    public async Task GivenHiddenTargetWithBadCipherAndStaleRevision_WhenTrashing_Then404BeforeInspectingEvidence(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();using var key=new ProtectionFixture();
        var o=await ProfileSetup.Create(fixture,owner);var s=kind=="unselectedOwner"?o:await ProfileSetup.Create(fixture,client);var target=await Collection(o);
        CollectionGrant? grant=null;if(kind!="ungranted"&&s.InternalId!=o.InternalId)grant=await AssociationSetup.Grant(fixture,o,owner,s,client,target);
        if(kind.StartsWith("unselected")){var p=await Profile(s,kind=="unselectedOwner"?owner:client,key,[]);s=s with{Verifier=await Selected(s,p.Id)};}
        await using(var db=fixture.CreateContext())
        {
            await db.Collections.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,"bad"u8.ToArray()).SetProperty(c=>c.DeletedAt,kind=="deleted"?(DateTimeOffset?)DateTimeOffset.UtcNow:null));
            if(grant is not null)await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));
            if(kind=="ownerClosing")await db.Accounts.Where(x=>x.Id==o.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));
            if(kind=="revoked")await db.CollectionGrants.Where(x=>x.Id==grant!.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Revoked));
            if(kind=="invalidAccess")await db.CollectionGrants.Where(x=>x.Id==grant!.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Access,(CollectionGrantAccess)99));
            if(kind is "zeroGrantRevision" or "unsafeGrantRevision")await db.CollectionGrants.Where(x=>x.Id==grant!.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Revision,kind=="zeroGrantRevision"?0:ProtocolBinary.MaxInteger+1));
            if(kind is "terminal" or "ownerTerminal" or "grantTerminal"){db.TerminalErasures.Add(new(){ResourceKind=kind=="terminal"?"collection":kind=="ownerTerminal"?"account":"grant",ResourceId=kind=="terminal"?target.PublicId:kind=="ownerTerminal"?o.AccountId:grant!.PublicId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        }
        var before=await Snapshot(o);var r=await Store().TrashAsync(new(s.Actor,s.Verifier,kind=="missing"?Guid.NewGuid():target.PublicId,77),default);
        Assert.Equal("not_found",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(o));
    }


    [FunctionalTheory][InlineData("signature")][InlineData("collectionEnvelope")][InlineData("grantRevision")][InlineData("epoch")]
    [InlineData("ownerPins")][InlineData("recipientPins")][InlineData("ownerPinsMissing")][InlineData("recipientPinsMissing")]
    [InlineData("ownerPinRevision")][InlineData("recipientPinEpoch")][InlineData("identity")][InlineData("numericString")][InlineData("duplicate")]
    public async Task GivenCorruptRelevantForeignNativeEvidence_WhenTrashing_Then503BeforeOwnerOnlyDenial(string kind)
    {
        using var owner=new ProtectionFixture();using var client=new ProtectionFixture();var o=await ProfileSetup.Create(fixture,owner);var s=await ProfileSetup.Create(fixture,client);var target=await Collection(o);
        var grant=await AssociationSetup.Grant(fixture,o,owner,s,client,target,CollectionGrantAccess.ReadWrite);
        await using(var db=fixture.CreateContext())
        {
            if(kind=="signature")await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,"bad"u8.ToArray()));
            if(kind=="collectionEnvelope")await db.Collections.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,"bad"u8.ToArray()));
            if(kind is "numericString" or "duplicate")await db.Collections.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.Envelope,Malformed(target.Envelope,kind)));
            if(kind=="grantRevision")await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Revision,2));
            if(kind=="epoch")await db.Collections.Where(x=>x.Id==target.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.KeyEpoch,2));
            if(kind is "ownerPins" or "recipientPins")await db.VaultProtections.Where(x=>x.AccountId==(kind=="ownerPins"?o.InternalId:s.InternalId)).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Material,"bad"u8.ToArray()));
            if(kind is "ownerPinsMissing" or "recipientPinsMissing")await db.VaultProtections.Where(x=>x.AccountId==(kind=="ownerPinsMissing"?o.InternalId:s.InternalId)).ExecuteDeleteAsync();
            if(kind=="ownerPinRevision")await db.VaultProtections.Where(x=>x.AccountId==o.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Revision,0));
            if(kind=="recipientPinEpoch")await db.VaultProtections.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.KeyEpoch,2));
            if(kind=="identity")await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,Bytes(owner.WrapGrant(o.AccountId,target.PublicId,grant.PublicId,Guid.NewGuid(),client.Material.RecipientKey))));
        }
        var before=await Snapshot(o);var recipient=await Snapshot(s);var r=await Trash(s,target,77);
        Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(o));Assert.Equal(recipient,await Snapshot(s));
    }


    [FunctionalTheory][InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")]
    [InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")]
    [InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")][InlineData("nullExpiry","vault_access_denied")]
    [InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)][InlineData("selection","vault_access_denied")]
    [InlineData("closing","not_found")][InlineData("actorAbsent","not_found")][InlineData("actorTerminal","not_found")]
    public async Task GivenInvalidCurrentAccountOrSession_WhenTrashing_ThenNoMutation(string kind,string? error)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);
        await using(var db=fixture.CreateContext())
        {
            var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);
            if(kind=="closing")a.State=AccountState.ClosurePending;if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);
            if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="zeroPolicy")v.PolicyRevision=0;if(kind=="zeroGeneration")v.RevocationGeneration=0;
            if(kind is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(kind is "nullExpiry" or "noExpiry")v.ExpiresAt=null;if(kind=="selection")v.ProfileId=long.MaxValue;
            if(kind=="actorTerminal")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();
        }
        var before=await Snapshot(s);var input=1;var r=await Store().TrashAsync(new(kind=="actorAbsent"?Guid.NewGuid():s.Actor,kind=="access"?new string('a',64):s.Verifier,target.PublicId,input),default);
        Assert.Equal(error,r.Error);if(error is not null){Assert.Null(r.Data);Assert.Equal(before,await Snapshot(s));}else await Success(target,r);
    }


    [FunctionalTheory][InlineData("trash")][InlineData("terminal")][InlineData("foreign")]
    public async Task GivenInvalidSelectedProfile_WhenTrashing_Then403WithoutWideningSelection(string kind)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);var p=await Profile(s,owner,key,[target.PublicId]);s=s with{Verifier=await Selected(s,p.Id)};
        await using(var db=fixture.CreateContext())
        {
            if(kind=="trash")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));
            if(kind=="terminal"){db.TerminalErasures.Add(new(){ResourceId=p.PublicId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
            if(kind=="foreign"){var foreign=await ProfileSetup.Create(fixture,key);var foreignProfile=await Profile(foreign,key,owner,[]);await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,foreignProfile.Id));}
        }
        var before=await Snapshot(s);Assert.Equal("vault_access_denied",(await Trash(s,target,1)).Error);Assert.Equal(before,await Snapshot(s));
    }


    [FunctionalFact]
    public async Task GivenUnavailableDatabaseOrCallerCancellation_WhenTrashing_Then503OrPropagateCancellation()
    {
        var request=new CollectionTrashRequest(Guid.NewGuid(),new string('a',64),Guid.NewGuid(),1);
        var dead=new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=absent;Username=none;Timeout=1;Pooling=false").Options);
        Assert.Equal("persistence_unavailable",(await new CollectionTrashStore(dead).TrashAsync(request,default)).Error);using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().TrashAsync(request,ct.Token));
    }


    [FunctionalTheory][InlineData("cancel")][InlineData("timeout")][InlineData("db")][InlineData("dbUpdate")][InlineData("json")][InlineData("format")][InlineData("wrapped")]
    public async Task GivenInternalProviderFailure_WhenTrashing_Then503WithoutLeakingException(string kind)
    {
        Exception failure=kind switch{"cancel"=>new OperationCanceledException("private"),"timeout"=>new TimeoutException("private"),"db"=>new NpgsqlException("private"),"dbUpdate"=>new DbUpdateException("private"),"json"=>new JsonException("private"),"format"=>new FormatException("private"),_=>new InvalidOperationException("private",new TimeoutException("private"))};
        var r=await new CollectionTrashStore(new ThrowingFactory(failure)).TrashAsync(new(Guid.NewGuid(),new string('a',64),Guid.NewGuid(),1),default);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);
    }


    [FunctionalTheory][InlineData("account")][InlineData("profile")][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("grant")]
    public async Task GivenTerminalGuidCollision_WhenTrashing_ThenOnlyCollectionMarkerHidesTarget(string kind)
    {
        using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=await Collection(s);
        await using(var db=fixture.CreateContext()){db.TerminalErasures.Add(new(){ResourceId=target.PublicId,ResourceKind=kind,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var input=1;var r=await Trash(s,target,input);if(kind=="collection")Assert.Equal("not_found",r.Error);else await Success(target,r);
    }


    private async Task<VaultCollection> Row(long id){await using var db=fixture.CreateContext();return await db.Collections.AsNoTracking().SingleAsync(x=>x.Id==id);}
    private async Task<VaultCollection> Collection(ProfileSetup.State s){var row=new VaultCollection{PublicId=Guid.NewGuid(),AccountId=s.InternalId,Envelope=Bytes(ProfileSetup.Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.CreateContext();db.Collections.Add(row);await db.SaveChangesAsync();return row;}
    private async Task<Profile> Profile(ProfileSetup.State s,ProtectionFixture owner,ProtectionFixture key,Guid[] collections,string mode="Master"){var input=ProfileSetup.Input(s,owner,key,mode) with{CollectionIds=collections};Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);await using var db=fixture.CreateContext();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==input.ProfileId);}
    private async Task Members(ProfileSetup.State s,AssociationSetup.ResourceSet items){await using var db=fixture.CreateContext();db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=items.Collection.Id,RecordId=items.Record.Id});db.CollectionFolders.Add(new(){AccountId=s.InternalId,CollectionId=items.Collection.Id,FolderId=items.Folder.Id});await db.SaveChangesAsync();}
    private async Task<string> Selected(ProfileSetup.State s,long profile){var verifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));await using var db=fixture.CreateContext();db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,ProfileId=profile,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return verifier;}
    private async Task<string> Snapshot(ProfileSetup.State s,long? except=null)
    {
        await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId&&x.Id!=except).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),ProfileRecords=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync(),ProfileFolders=await db.ProfileFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileCollections=await db.ProfileCollections.AsNoTracking().Where(x=>db.Profiles.Any(p=>p.Id==x.ProfileId&&p.AccountId==s.InternalId)).OrderBy(x=>x.ProfileId).ThenBy(x=>x.CollectionId).ToArrayAsync(),CollectionRecords=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),CollectionFolders=await db.CollectionFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.FolderId).ToArrayAsync(),Operations=await db.TrashOperations.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Entries=await db.TrashEntries.AsNoTracking().Where(x=>db.TrashOperations.Any(o=>o.Id==x.OperationId&&o.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync(),Queue=await db.RetentionWorkItems.AsNoTracking().Where(x=>db.TrashOperations.Any(o=>o.AccountId==s.InternalId&&x.OperationKey=="trash/"+o.PublicId)).OrderBy(x=>x.Id).ToArrayAsync(),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>x.RecipientAccountId==s.InternalId||db.Collections.Any(c=>c.Id==x.CollectionId&&c.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync()});
    }
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private static byte[] Malformed(byte[] raw,string kind){var value=Encoding.UTF8.GetString(raw);return Encoding.UTF8.GetBytes(kind switch{"numericString"=>value.Replace("\"keyEpoch\":1","\"keyEpoch\":\"1\""),"unknown"=>value.Replace("{","{\"secret\":1,"),"duplicate"=>value.Replace("{","{\"keyEpoch\":1,"),_=>value.Replace("\"format\"","\"Format\"")});}
    private static TaskCompletionSource SignalSource()=>new(TaskCreationOptions.RunContinuationsAsynchronously);
    private static async Task Past(DateTimeOffset deadline){var delay=deadline-DateTimeOffset.UtcNow;if(delay>TimeSpan.Zero)await Task.Delay(delay+TimeSpan.FromMilliseconds(80));}
    private static Task Hold(AppDbContext db,string table,ProfileSetup.State s,VaultCollection c,Profile p)=>table switch{"account"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE"),"profile"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE id={p.Id} FOR UPDATE"),"vault_access_session"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE handle_verifier={s.Verifier} FOR UPDATE"),_=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE id={c.Id} FOR UPDATE")};

    private CollectionTrashStore Store()=>new(fixture);
    private Task<VaultResult<CollectionTrashDetails>> Trash(ProfileSetup.State s,VaultCollection target,long revision=1)=>Store().TrashAsync(new(s.Actor,s.Verifier,target.PublicId,revision),default);
    private async Task Success(VaultCollection original,VaultResult<CollectionTrashDetails> result)
    {
        Assert.Null(result.Error);Assert.NotNull(result.Data);var value=result.Data;var row=await Row(original.Id);
        Assert.Equal(original.PublicId,value.CollectionId);Assert.NotEqual(Guid.Empty,value.TrashOperationId);
        Assert.Equal(original.Revision+1,value.Revision);Assert.True(value.ServerSequence>original.ServerSequence);Assert.InRange(value.ServerSequence,1,ProtocolBinary.MaxInteger);
        Assert.Equal(TimeSpan.FromHours(720),value.PurgeAt-value.DeletedAt);Assert.Equal(TimeSpan.Zero,value.DeletedAt.Offset);Assert.Equal(0,value.DeletedAt.Ticks%10);Assert.Equal(TimeSpan.Zero,value.PurgeAt.Offset);
        Assert.Equal(original.AccountId,row.AccountId);Assert.Equal(original.KeyEpoch,row.KeyEpoch);Assert.Equal(original.Envelope,row.Envelope);Assert.Equal(original.EditedAt,row.EditedAt);
        Assert.Equal(value.Revision,row.Revision);Assert.Equal(value.ServerSequence,row.ServerSequence);Assert.Equal(value.DeletedAt,row.DeletedAt);Assert.Equal(value.PurgeAt,row.PurgeAt);Assert.NotEqual(original.ConcurrencyStamp,row.ConcurrencyStamp);
        await using var db=fixture.CreateContext();var operation=Assert.Single(await db.TrashOperations.Where(x=>x.AccountId==original.AccountId).ToArrayAsync());
        Assert.Equal(value.TrashOperationId,operation.PublicId);Assert.Equal("collection",operation.RootResourceKind);Assert.Equal(original.PublicId,operation.RootResourceId);Assert.Equal(value.DeletedAt,operation.DeletedAt);Assert.Equal(value.PurgeAt,operation.PurgeAt);
        var entry=Assert.Single(await db.TrashEntries.Where(x=>x.OperationId==operation.Id).ToArrayAsync());Assert.Equal("collection",entry.ResourceKind);Assert.Equal(original.PublicId,entry.ResourceId);
        var queue=Assert.Single(await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+operation.PublicId).ToArrayAsync());Assert.Equal(value.PurgeAt,queue.DueAt);
    }
    private sealed class ThrowingFactory(Exception exception):IDbContextFactory<AppDbContext>
    {public AppDbContext CreateDbContext()=>throw exception;public Task<AppDbContext> CreateDbContextAsync(CancellationToken ct=default)=>Task.FromException<AppDbContext>(exception);}
    private sealed class Capture(bool explain=false):DbCommandInterceptor
    {
        public string Sql{get;private set;}="";public NpgsqlParameter[] Parameters{get;private set;}=[];public List<string> Locks{get;}=[];public List<object> LockValues{get;}=[];public List<object> AccountLockValues{get;}=[];
        public string? Plan{get;private set;}public List<(string Sql,object[] Values)> LockParameters{get;}=[];
        private void Note(DbCommand c){if(c.CommandText.Contains("FOR UPDATE")||c.CommandText.Contains("FOR NO KEY UPDATE")||c.CommandText.Contains("FOR SHARE")){Locks.Add(c.CommandText);LockValues.AddRange(c.Parameters.Cast<DbParameter>().Select(p=>p.Value!));LockParameters.Add((c.CommandText,c.Parameters.Cast<DbParameter>().Select(p=>p.Value!).ToArray()));if(c.CommandText.Contains("FROM cerberus.account "))AccountLockValues.AddRange(c.Parameters.Cast<DbParameter>().Select(p=>p.Value!));}}
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<int> r,CancellationToken ct=default){Note(c);return ValueTask.FromResult(r);}
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData e,InterceptionResult<DbDataReader> r,CancellationToken ct=default){Note(c);if(Sql.Length==0&&c.CommandText.StartsWith("WITH actor")){Sql=c.CommandText;Parameters=c.Parameters.Cast<NpgsqlParameter>().Select(p=>p.Clone()).ToArray();if(explain){await using var query=c.Connection!.CreateCommand();query.Transaction=c.Transaction;query.CommandText="EXPLAIN (ANALYZE, FORMAT JSON) "+Sql;foreach(var p in Parameters)query.Parameters.Add(p.Clone());Plan=(string)(await query.ExecuteScalarAsync(ct))!;}}return r;}
    }
}
