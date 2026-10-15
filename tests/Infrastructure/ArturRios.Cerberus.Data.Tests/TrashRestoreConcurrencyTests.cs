using System.Data.Common;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class TrashRestoreConcurrencyTests(PostgresFixture fixture)
{
    [FunctionalTheory]
    [InlineData("account",false)][InlineData("session",false)][InlineData("retention_work_item",false)]
    [InlineData("trash_operation",false)][InlineData("trash_entry",false)][InlineData("profile",false)]
    [InlineData("collection",false)][InlineData("folder",false)][InlineData("record",false)]
    [InlineData("account",true)][InlineData("session",true)][InlineData("retention_work_item",true)]
    [InlineData("trash_operation",true)][InlineData("trash_entry",true)][InlineData("profile",true)]
    [InlineData("collection",true)][InlineData("folder",true)][InlineData("record",true)]
    public async Task GivenExpiryDuringRequiredDatabaseLockWait_WhenRestoring_ThenRejectAndPreserveWholeOperation(string table,bool session)
    {
        using var keys=new ProtectionFixture();var w=await Linked(keys);var deadline=DateTimeOffset.UtcNow.AddSeconds(2);
        await using var db=fixture.CreateContext();
        if(session)await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.State.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));
        else await TrashRestoreSetup.Deadline(fixture,w.Root,deadline);
        var before=await TrashRestoreSetup.Snapshot(fixture,w.State);
        await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();
        await Lock(held,table,w);
        var signal=new PermanentDeleteSetup.LockSignal(table=="session"?"vault_access_session":table);
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending=new TrashRestoreStore(new ProfileSetup.Factory(fixture,signal)).RestoreAsync(Request(w),ct.Token);
        try {await signal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);await Past(deadline);}
        finally {await tx.CommitAsync();}
        var result=await pending;Assert.Equal(session?"vault_access_denied":"trash_expired",result.Error);Assert.Null(result.Data);
        Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,w.State));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenExpiryImmediatelyBeforeRootStatement_WhenRestoring_ThenFreshDatabaseGuardRejects(bool session)
    {
        using var keys=new ProtectionFixture();var w=await Linked(keys);var deadline=DateTimeOffset.UtcNow.AddSeconds(2);
        if(session){await using var db=fixture.CreateContext();await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.State.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));}
        else await TrashRestoreSetup.Deadline(fixture,w.Root,deadline);
        var before=await TrashRestoreSetup.Snapshot(fixture,w.State);
        var hook=new Boundary("root",false,()=>Past(deadline));
        var r=await new TrashRestoreStore(new ProfileSetup.Factory(fixture,hook)).RestoreAsync(Request(w),default);
        Assert.True(hook.Reached);Assert.Equal(session?"vault_access_denied":"trash_expired",r.Error);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,w.State));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenPredeadlineRootAdmissionAndLaterNaturalExpiry_WhenRestoringCascade_ThenCompleteAdmittedUnit(bool session)
    {
        using var keys=new ProtectionFixture();var w=await Linked(keys,true);var deadline=DateTimeOffset.UtcNow.AddSeconds(2);
        await using(var db=fixture.CreateContext()){
            if(session)await db.VaultAccessSessions.Where(x=>x.HandleVerifier==w.State.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));
            else {await TrashRestoreSetup.Deadline(fixture,w.Root,deadline);await db.Records.Where(x=>x.Id==w.Items.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,DateTimeOffset.FromUnixTimeMilliseconds(deadline.ToUnixTimeMilliseconds()).AddDays(-30)).SetProperty(r=>r.PurgeAt,DateTimeOffset.FromUnixTimeMilliseconds(deadline.ToUnixTimeMilliseconds())));}}
        var hook=new Boundary("root",true,()=>Past(deadline));
        var r=await new TrashRestoreStore(new ProfileSetup.Factory(fixture,hook)).RestoreAsync(Request(w),default);
        Assert.True(hook.Reached);Assert.Null(r.Error);Assert.Equal(2,r.Data!.Items.Count);Assert.True(r.Data.RestoredAt<deadline);await TrashRestoreSetup.NoOperation(fixture,w.Root.OperationId);
        await using var check=fixture.CreateContext();Assert.Null((await check.Records.SingleAsync(x=>x.Id==w.Items.Record.Id)).DeletedAt);
    }
    [FunctionalTheory][InlineData("restore")][InlineData("purge")]
    public async Task GivenConcurrentRestoreAndAnotherWriter_WhenRestoring_ThenExactlyOneWinnerAndNoResurrection(string other)
    {
        using var keys=new ProtectionFixture();var w=await Linked(keys);
        using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var first=new TrashRestoreStore(fixture).RestoreAsync(Request(w),ct.Token);
        var second=other=="restore"?new TrashRestoreStore(fixture).RestoreAsync(Request(w),ct.Token).ContinueWith(t=>t.Result.Error,ct.Token):Purge();
        var result=await first;var error=await second;
        Assert.Equal(1,(result.Error is null?1:0)+(error is null?1:0));
        Assert.Contains(result.Error??error,new[]{"not_found","revision_conflict"});
        await using var db=fixture.CreateContext();
        if(other=="purge"&&error is null){Assert.False(await db.Records.AnyAsync(x=>x.Id==w.Root.InternalId));Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="record"&&x.ResourceId==w.Root.ResourceId));}
        else {var row=await db.Records.SingleAsync(x=>x.Id==w.Root.InternalId);Assert.Null(row.DeletedAt);Assert.Equal(w.Root.Revision+1,row.Revision);}
        async Task<string?> Purge(){var path=Directory.CreateTempSubdirectory("cerberus-uc51-race-").FullName;try {return(await new ArturRios.Cerberus.Data.Records.RecordPermanentDeleteStore(fixture,new FileErasureLedger(path),new ArturRios.Cerberus.Shared.Configuration.CerberusOptions{RetentionInterval="00:00:30"},TimeProvider.System).DeleteAsync(new(w.State.Actor,w.State.Verifier,w.Root.ResourceId,w.Root.Revision),ct.Token)).Error;}finally{Directory.Delete(path,true);}}
    }
    [FunctionalFact]
    public async Task GivenPreviouslyClaimedTrashJob_WhenRestoreCommits_ThenOldWorkerCannotCompleteIt()
    {
        using var keys=new ProtectionFixture();var w=await Linked(keys);await using var db=fixture.CreateContext();
        var job=await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="trash/"+w.Root.OperationId);job.DueAt=DateTimeOffset.UtcNow.AddMinutes(-1);await db.SaveChangesAsync();
        var jobs=new RetentionWorkStore(fixture);var claim=await jobs.TryClaimAsync(job.PublicId,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(5),default);Assert.NotNull(claim);
        Assert.Null((await new TrashRestoreStore(fixture).RestoreAsync(Request(w),default)).Error);
        Assert.False(await jobs.TryCompleteAsync(claim!,DateTimeOffset.UtcNow,default));await TrashRestoreSetup.NoOperation(fixture,w.Root.OperationId);
    }
    [FunctionalTheory]
    [InlineData("root",false)][InlineData("root",true)][InlineData("member",false)][InlineData("member",true)]
    [InlineData("parent",false)][InlineData("parent",true)][InlineData("link",false)][InlineData("link",true)]
    [InlineData("operation",false)][InlineData("operation",true)][InlineData("job",false)][InlineData("job",true)]
    public async Task GivenFailureAfterActualMutation_WhenRestoring_ThenRollbackEveryRowAndAllowRetry(string phase,bool cancel)
    {
        using var keys=new ProtectionFixture();var w=await Linked(keys,phase=="member");var before=await TrashRestoreSetup.Snapshot(fixture,w.State);
        using var ct=new CancellationTokenSource();var hook=new Boundary(phase,true,()=>{if(cancel){ct.Cancel();throw new OperationCanceledException(ct.Token);}throw new NpgsqlException("fixture mutation failure");});
        var store=new TrashRestoreStore(new ProfileSetup.Factory(fixture,hook));
        if(cancel)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.RestoreAsync(Request(w),ct.Token));
        else {var r=await store.RestoreAsync(Request(w),default);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);}
        Assert.True(hook.Reached,phase);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,w.State));
        Assert.Null((await new TrashRestoreStore(fixture).RestoreAsync(Request(w),default)).Error);
    }
    private async Task<World> Linked(ProtectionFixture keys,bool cascade=false)
    {
        var s=await ProfileSetup.Create(fixture,keys);var items=await AssociationSetup.Items(fixture,s);var profile=await AssociationSetup.Profile(fixture,s,keys);
        await using(var db=fixture.CreateContext()){
            await db.Records.Where(x=>x.Id==items.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,items.Folder.Id));
            db.ProfileRecords.Add(new(){AccountId=s.InternalId,ProfileId=profile,RecordId=items.Record.Id});db.ProfileFolders.Add(new(){AccountId=s.InternalId,ProfileId=profile,FolderId=items.Folder.Id});db.CollectionRecords.Add(new(){AccountId=s.InternalId,CollectionId=items.Collection.Id,RecordId=items.Record.Id});await db.SaveChangesAsync();}
        var root=await TrashRestoreSetup.TrashAsync(fixture,s,cascade?"folder":"record",cascade?items.Folder.PublicId:items.Record.PublicId,1,cascade?items.Folder.Id:items.Record.Id,items.Record.EditedAt);
        return new(s,items,profile,root);
    }
    private sealed record World(ProfileSetup.State State,AssociationSetup.ResourceSet Items,long Profile,TrashRestoreSetup.Root Root);
    private static TrashRestoreRequest Request(World w)=>new(w.State.Actor,w.State.Verifier,w.Root.OperationId,w.Root.Revision);
    private static async Task Past(DateTimeOffset deadline){var left=deadline-DateTimeOffset.UtcNow;if(left>TimeSpan.Zero)await Task.Delay(left+TimeSpan.FromMilliseconds(100));}
    private static Task<int> Lock(AppDbContext db,string table,World w)=>table switch{
        "account"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={w.State.InternalId} FOR UPDATE"),
        "session"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE handle_verifier={w.State.Verifier} FOR UPDATE"),
        "retention_work_item"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.retention_work_item WHERE operation_key={"trash/"+w.Root.OperationId} FOR UPDATE"),
        "trash_operation"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.trash_operation WHERE public_id={w.Root.OperationId} FOR UPDATE"),
        "trash_entry"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.trash_entry WHERE resource_kind={w.Root.Kind} AND resource_id={w.Root.ResourceId} FOR UPDATE"),
        "profile"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE id={w.Profile} FOR UPDATE"),
        "collection"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE id={w.Items.Collection.Id} FOR UPDATE"),
        "folder"=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.folder WHERE id={w.Items.Folder.Id} FOR UPDATE"),
        _=>db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE id={w.Items.Record.Id} FOR UPDATE")};
    private sealed class Boundary(string phase,bool after,Func<Task> callback):DbCommandInterceptor
    {
        internal bool Reached{get;private set;}
        private bool Matches(DbCommand command)=>phase switch{
            "root"=>command.CommandText.Contains("SET ")&&command.CommandText.Contains("deleted_at=NULL")&&command.CommandText.Contains("o.root_resource_kind=@kind"),
            "member"=>command.CommandText.Contains("WITH session AS (SELECT @owner")&&command.CommandText.Contains("deleted_at=NULL"),
            "parent"=>command.CommandText.StartsWith("UPDATE cerberus.")&&command.CommandText.Contains("revision=r.revision+1")&&!command.CommandText.Contains("deleted_at=NULL"),
            "link"=>command.CommandText.Contains("INSERT INTO cerberus.profile_")||command.CommandText.Contains("INSERT INTO cerberus.collection_"),
            "operation"=>command.CommandText.Contains("DELETE FROM cerberus.trash_operation"),
            "job"=>command.CommandText.Contains("DELETE FROM cerberus.retention_work_item"),_=>false};
        private async Task Apply(DbCommand command,bool executed){if(!Reached&&after==executed&&Matches(command)){Reached=true;await callback();}}
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){await Apply(c,false);return r;}
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default){await Apply(c,true);return r;}
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){await Apply(c,false);return r;}
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData d,DbDataReader r,CancellationToken ct=default){await Apply(c,true);return r;}
    }
}
