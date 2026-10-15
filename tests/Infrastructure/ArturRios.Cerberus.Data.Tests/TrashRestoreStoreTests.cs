using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class TrashRestoreStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenActualNativeDeletion_WhenRestoring_ThenOriginalBytesAndOneMetadataAdvance(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);
        var before=await TrashRestoreSetup.Resource(fixture,root);var account=await TrashRestoreSetup.Protected(fixture,s);
        var result=await Store().RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),default);Assert.Null(result.Error);
        Assert.Equal(root.OperationId,result.Data!.OperationId);Assert.Equal(root.ResourceId,result.Data.RootResourceId);Assert.Equal(kind,result.Data.RootKind);
        var item=Assert.Single(result.Data.Items);Assert.Equal(root.Revision+1,item.Revision);Assert.True(item.ServerSequence>root.Sequence);Assert.Equal(root.EditedAt,item.EditedAt);Assert.Empty(result.Data.Omissions);
        var after=await TrashRestoreSetup.Resource(fixture,root);TrashRestoreSetup.Restored(before,after,root);
        Assert.Equal(account,await TrashRestoreSetup.Protected(fixture,s));await TrashRestoreSetup.NoOperation(fixture,root.OperationId);
        var page=await new TrashListStore(fixture).ListAsync(new(s.Actor,s.Verifier,50,0,null),default);Assert.Null(page.Error);Assert.Empty(page.Data!.Items);
        var unchanged=await TrashRestoreSetup.Snapshot(fixture,s);Assert.Equal("not_found",(await Store().RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),default)).Error);Assert.Equal(unchanged,await TrashRestoreSetup.Snapshot(fixture,s));
    }
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenRestoredResource_WhenTrashedAgain_ThenNewOperationAndFreshDeadline(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);
        Assert.Null((await Store().RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),default)).Error);
        var next=await TrashRestoreSetup.TrashAsync(fixture,s,kind,root.ResourceId,root.Revision+1,root.InternalId,root.EditedAt);
        Assert.NotEqual(root.OperationId,next.OperationId);Assert.True(next.DeletedAt>=root.DeletedAt);Assert.Equal(TimeSpan.FromDays(30),next.PurgeAt-next.DeletedAt);
        Assert.Null((await Store().RestoreAsync(new(s.Actor,s.Verifier,next.OperationId,next.Revision),default)).Error);
    }
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenExpiredOwnDeletion_WhenRestoringBeforePhysicalPurge_Then410AndNoMutation(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);
        await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));var before=await TrashRestoreSetup.Snapshot(fixture,s);
        var result=await Store().RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),default);Assert.Equal("trash_expired",result.Error);Assert.Null(result.Data);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));
    }
    [FunctionalTheory][InlineData("missing")][InlineData("foreign")][InlineData("terminal")]
    public async Task GivenHiddenOperationWithStaleRevision_WhenRestoring_Then404BeforeConflict(string kind)
    {
        using var keys=new ProtectionFixture();var owner=await ProfileSetup.Create(fixture,keys);var actor=kind=="foreign"?await ProfileSetup.Create(fixture,keys):owner;
        var root=await TrashRestoreSetup.RootAsync(fixture,owner,keys,"record");
        if(kind=="terminal"){await using var db=fixture.CreateContext();db.TerminalErasures.Add(new(){ResourceKind="record",ResourceId=root.ResourceId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var before=await TrashRestoreSetup.Snapshot(fixture,owner);var r=await Store().RestoreAsync(new(actor.Actor,actor.Verifier,kind=="missing"?Guid.NewGuid():root.OperationId,99),default);
        Assert.Equal("not_found",r.Error);Assert.Null(r.Data);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,owner));
    }
    [FunctionalFact]
    public async Task GivenStaleVisibleRootRevision_WhenRestoring_Then409WithoutChangingWinner()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");var before=await TrashRestoreSetup.Snapshot(fixture,s);
        var r=await Store().RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision-1),default);Assert.Equal("revision_conflict",r.Error);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));
    }
    [FunctionalFact]
    public async Task GivenSameGuidTerminalOfAnotherKind_WhenRestoring_ThenPreserveTypedIsolation()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");
        await using(var db=fixture.CreateContext()){db.TerminalErasures.Add(new(){ResourceKind="folder",ResourceId=root.ResourceId,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var r=await Store().RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),default);Assert.Null(r.Error);Assert.Equal(root.ResourceId,Assert.Single(r.Data!.Items).ResourceId);
        await using var check=fixture.CreateContext();Assert.True(await check.TerminalErasures.AnyAsync(x=>x.ResourceKind=="folder"&&x.ResourceId==root.ResourceId));
    }
    [FunctionalTheory][InlineData("missing","not_found")][InlineData("closing","not_found")][InlineData("terminal","not_found")]
    [InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")]
    [InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")]
    [InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")]
    [InlineData("nullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)]
    public async Task GivenInvalidCurrentAuthority_WhenRestoring_ThenFailClosed(string damage,string? error)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");
        await using(var db=fixture.CreateContext()){
            var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);
            if(damage=="closing")a.State=AccountState.ClosurePending;if(damage=="terminal")db.TerminalErasures.Add(new(){ResourceKind="account",ResourceId=s.AccountId,DeletedAt=DateTimeOffset.UtcNow});
            if(damage=="revoked")v.Revoked=true;if(damage=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(damage=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);
            if(damage=="policy")v.PolicyRevision++;if(damage=="generation")v.RevocationGeneration++;if(damage=="zeroPolicy")v.PolicyRevision=0;if(damage=="zeroGeneration")v.RevocationGeneration=0;
            if(damage is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(damage is "nullExpiry" or "noExpiry")v.ExpiresAt=null;await db.SaveChangesAsync();}
        var before=await TrashRestoreSetup.Snapshot(fixture,s);var r=await Store().RestoreAsync(new(damage=="missing"?Guid.NewGuid():s.Actor,damage=="access"?new string('a',64):s.Verifier,root.OperationId,root.Revision),default);
        Assert.Equal(error,r.Error);if(error is not null){Assert.Null(r.Data);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));}else await TrashRestoreSetup.NoOperation(fixture,root.OperationId);
    }
    [FunctionalTheory][InlineData("snapshot")][InlineData("envelope")][InlineData("revision")][InlineData("sequence")][InlineData("dates")]
    public async Task GivenRequiredStoredCorruption_WhenRestoring_Then503AndExactRollback(string damage)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");
        await using(var db=fixture.CreateContext()){
            if(damage=="snapshot")await db.TrashEntries.Where(x=>x.ResourceKind=="record"&&x.ResourceId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(e=>e.AssociationSnapshot,"bad"u8.ToArray()));
            if(damage=="envelope")await db.Records.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()));
            if(damage=="revision")await db.Records.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Revision,0));
            if(damage=="sequence")await db.Records.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.ServerSequence,0));
            if(damage=="dates")await db.Records.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.PurgeAt,root.PurgeAt.AddHours(1)));}
        var before=await TrashRestoreSetup.Snapshot(fixture,s);var r=await Store().RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),default);
        Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));
    }
    [FunctionalFact]
    public async Task GivenCancelledCaller_WhenRestoring_ThenPropagateBeforeWork()
    {using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().RestoreAsync(new(Guid.NewGuid(),"a",Guid.NewGuid(),1),ct.Token));}
    [FunctionalFact]
    public async Task GivenFutureDeletionTime_WhenRestoring_ThenRejectCorruptLifecycleWithoutMutation()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");
        await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddDays(30).AddMinutes(1));var before=await TrashRestoreSetup.Snapshot(fixture,s);
        var r=await Store().RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),default);Assert.Equal("persistence_unavailable",r.Error);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));
    }
    [FunctionalFact]
    public async Task GivenUncapturedActiveChildOfRetainedFolder_WhenRestoring_ThenCannotBroadenTheOperation()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"folder");
        var record=new VaultRecord{AccountId=s.InternalId,PublicId=Guid.NewGuid(),FolderId=root.InternalId,Envelope=JsonSerializer.SerializeToUtf8Bytes(ProfileSetup.Envelope(),ProtectionFixture.Json),EditedAt=DateTimeOffset.UnixEpoch};
        await using(var db=fixture.CreateContext()){db.Records.Add(record);await db.SaveChangesAsync();}
        var before=await TrashRestoreSetup.Snapshot(fixture,s);var r=await Store().RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),default);
        Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));
    }
    private TrashRestoreStore Store()=>new(fixture);
}

internal static class TrashRestoreSetup
{
    internal sealed record Root(string Kind,Guid ResourceId,long InternalId,Guid OperationId,long Revision,long Sequence,DateTimeOffset EditedAt,DateTimeOffset DeletedAt,DateTimeOffset PurgeAt);
    internal static async Task<Root> RootAsync(PostgresFixture fixture,ProfileSetup.State s,ProtectionFixture keys,string kind)
    {
        Guid id;long internalId;var edited=DateTimeOffset.UnixEpoch;
        if(kind=="profile"){
            using var scoped=new ProtectionFixture();var input=ProfileSetup.Input(s,keys,scoped);Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);
            await using var db=fixture.CreateContext();var p=await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==input.ProfileId);id=p.PublicId;internalId=p.Id;edited=p.EditedAt;}
        else{var a=await AssociationSetup.Items(fixture,s);id=kind=="record"?a.Record.PublicId:kind=="folder"?a.Folder.PublicId:a.Collection.PublicId;internalId=kind=="record"?a.Record.Id:kind=="folder"?a.Folder.Id:a.Collection.Id;edited=a.Record.EditedAt;}
        return await TrashAsync(fixture,s,kind,id,1,internalId,edited);
    }
    internal static async Task<Root> TrashAsync(PostgresFixture f,ProfileSetup.State s,string kind,Guid id,long revision,long internalId,DateTimeOffset edited)
    {
        switch(kind){
            case "record":var r=await new RecordTrashStore(f).TrashAsync(new(s.Actor,s.Verifier,id,revision),default);Assert.Null(r.Error);return new(kind,id,internalId,r.Data!.TrashOperationId,r.Data.Revision,r.Data.ServerSequence,edited,r.Data.DeletedAt,r.Data.PurgeAt);
            case "folder":var d=await new FolderTrashStore(f).TrashAsync(new(s.Actor,s.Verifier,id,revision),default);Assert.Null(d.Error);return new(kind,id,internalId,d.Data!.TrashOperationId,d.Data.Revision,d.Data.ServerSequence,edited,d.Data.DeletedAt,d.Data.PurgeAt);
            case "collection":var c=await new CollectionTrashStore(f).TrashAsync(new(s.Actor,s.Verifier,id,revision),default);Assert.Null(c.Error);return new(kind,id,internalId,c.Data!.TrashOperationId,c.Data.Revision,c.Data.ServerSequence,edited,c.Data.DeletedAt,c.Data.PurgeAt);
            default:var p=await new ProfileTrashStore(f).TrashAsync(new(s.Actor,s.Verifier,id,revision),default);Assert.Null(p.Error);return new(kind,id,internalId,p.Data!.TrashOperationId,p.Data.Revision,p.Data.ServerSequence,edited,p.Data.DeletedAt,p.Data.PurgeAt);}
    }
    private static string DeadlineSql(string kind)=>kind switch{
        "record"=>"UPDATE cerberus.record SET deleted_at=@deleted,purge_at=@purge WHERE public_id=@id",
        "folder"=>"UPDATE cerberus.folder SET deleted_at=@deleted,purge_at=@purge WHERE public_id=@id",
        "collection"=>"UPDATE cerberus.collection SET deleted_at=@deleted,purge_at=@purge WHERE public_id=@id",
        "profile"=>"UPDATE cerberus.profile SET deleted_at=@deleted,purge_at=@purge WHERE public_id=@id",_=>throw new InvalidOperationException()};
    private static string ResourceSql(string kind)=>kind switch{
        "record"=>"SELECT to_jsonb(r)::text AS \"Value\" FROM cerberus.record r WHERE r.public_id=@id",
        "folder"=>"SELECT to_jsonb(r)::text AS \"Value\" FROM cerberus.folder r WHERE r.public_id=@id",
        "collection"=>"SELECT to_jsonb(r)::text AS \"Value\" FROM cerberus.collection r WHERE r.public_id=@id",
        "profile"=>"SELECT to_jsonb(r)::text AS \"Value\" FROM cerberus.profile r WHERE r.public_id=@id",_=>throw new InvalidOperationException()};
    internal static async Task Deadline(PostgresFixture f,Root root,DateTimeOffset value)
    {value=DateTimeOffset.FromUnixTimeMilliseconds(value.ToUnixTimeMilliseconds());await using var db=f.CreateContext();await db.TrashOperations.Where(x=>x.PublicId==root.OperationId).ExecuteUpdateAsync(x=>x.SetProperty(o=>o.DeletedAt,value.AddDays(-30)).SetProperty(o=>o.PurgeAt,value));await db.Database.ExecuteSqlRawAsync(DeadlineSql(root.Kind),new NpgsqlParameter("deleted",value.AddDays(-30)),new NpgsqlParameter("purge",value),new NpgsqlParameter("id",root.ResourceId));await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+root.OperationId).ExecuteUpdateAsync(x=>x.SetProperty(j=>j.DueAt,value));}
    internal static async Task<JsonElement> Resource(PostgresFixture f,Root r)
    {await using var db=f.CreateContext();var json=await db.Database.SqlQueryRaw<string>(ResourceSql(r.Kind),new NpgsqlParameter("id",r.ResourceId)).SingleAsync();return JsonDocument.Parse(json).RootElement.Clone();}
    internal static void Restored(JsonElement before,JsonElement after,Root root)
    {
        Assert.Equal(JsonValueKind.Null,after.GetProperty("deleted_at").ValueKind);Assert.Equal(JsonValueKind.Null,after.GetProperty("purge_at").ValueKind);
        Assert.Equal(root.Revision+1,after.GetProperty("revision").GetInt64());Assert.True(after.GetProperty("server_sequence").GetInt64()>root.Sequence);
        var excluded=new[]{"deleted_at","purge_at","revision","server_sequence","concurrency_stamp"};
        foreach(var field in before.EnumerateObject().Where(x=>!excluded.Contains(x.Name)))Assert.Equal(field.Value.GetRawText(),after.GetProperty(field.Name).GetRawText());
    }
    internal static async Task NoOperation(PostgresFixture f,Guid id)
    {await using var db=f.CreateContext();Assert.False(await db.TrashOperations.AnyAsync(x=>x.PublicId==id));Assert.False(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="trash/"+id));}
    internal static async Task<string> Protected(PostgresFixture f,ProfileSetup.State s)
    {await using var db=f.CreateContext();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleAsync(x=>x.AccountId==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync()});}
    internal static async Task<string> Snapshot(PostgresFixture f,ProfileSetup.State s)
    {
        await using var db=f.CreateContext();var operations=await db.TrashOperations.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync();var ids=operations.Select(x=>x.Id).ToArray();var keys=operations.Select(x=>"trash/"+x.PublicId).ToArray();
        return JsonSerializer.Serialize(new{Protected=await Protected(f,s),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),PR=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync(),PF=await db.ProfileFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.FolderId).ToArrayAsync(),PC=await db.ProfileCollections.AsNoTracking().Where(x=>db.Profiles.Any(p=>p.Id==x.ProfileId&&p.AccountId==s.InternalId)).OrderBy(x=>x.ProfileId).ThenBy(x=>x.CollectionId).ToArrayAsync(),CR=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),CF=await db.CollectionFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.FolderId).ToArrayAsync(),Operations=operations,Entries=await db.TrashEntries.AsNoTracking().Where(x=>ids.Contains(x.OperationId)).OrderBy(x=>x.Id).ToArrayAsync(),Jobs=await db.RetentionWorkItems.AsNoTracking().Where(x=>keys.Contains(x.OperationKey)).OrderBy(x=>x.Id).ToArrayAsync()});
    }
}
