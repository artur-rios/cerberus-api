using System.Data.Common;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace ArturRios.Cerberus.Data.Tests;

public partial class TrashEmptyStoreTests
{
    [FunctionalFact]
    public async Task GivenLivePurgePausedAtLedger_WhenPureRetryArrives_ThenDoNotStealClaimOrHoldDatabaseLocks()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");
        var entered=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending=Store(new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>{entered.TrySetResult();return release.Task.WaitAsync(ct.Token);})).EmptyAsync(Request(s),ct.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));await using var db=fixture.CreateContext();var before=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId);
        try{
            var retry=await Store().EmptyAsync(Request(s),ct.Token).WaitAsync(TimeSpan.FromSeconds(10));Assert.Equal("persistence_unavailable",retry.Error);
            var after=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.Id==before.Id);Assert.Equal(before.ClaimToken,after.ClaimToken);Assert.Equal(before.ClaimExpiresAt,after.ClaimExpiresAt);Assert.Equal(before.ConcurrencyStamp,after.ConcurrencyStamp);Assert.Equal(before.Attempts,after.Attempts);
            await using var tx=await db.Database.BeginTransactionAsync(ct.Token);await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='500ms'",ct.Token);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE",ct.Token);await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.retention_work_item WHERE id={before.Id} FOR UPDATE",ct.Token);await tx.CommitAsync(ct.Token);
        }finally{release.TrySetResult();}
        Assert.Null((await pending).Error);Assert.False(await Exists(root));
    }
    [FunctionalFact]
    public async Task GivenNewPermanentTargetsDuringOldLedgerIo_WhenOldClaimResumes_ThenCannotPurgeBeforeNewCompleteLedger()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var first=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");TrashRestoreSetup.Root? second=null;Guid oldToken=default,newToken=default;var injected=false;
        var ledger=new PermanentDeleteSetup.LedgerBoundary(Ledger,async()=>{
            if(injected)return;injected=true;await using(var db=fixture.CreateContext()){oldToken=(await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId)).ClaimToken!.Value;}
            second=await TrashRestoreSetup.RootAsync(fixture,s,keys,"collection");Assert.Equal("persistence_unavailable",(await Store(new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>throw new IOException("new target ledger outage"))).EmptyAsync(Request(s),default)).Error);
            await using var check=fixture.CreateContext();newToken=(await check.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId)).ClaimToken!.Value;
        });
        var result=await Store(ledger).EmptyAsync(Request(s),default);Assert.Equal("persistence_unavailable",result.Error);Assert.NotEqual(oldToken,newToken);Assert.True(await Exists(first));Assert.True(await Exists(second!));
        await Expire(s);var retry=await Store().EmptyAsync(Request(s),default);Assert.Null(retry.Error);Assert.Equal(2,retry.Data!.TotalPurged);Assert.False(await Exists(first));Assert.False(await Exists(second!));Assert.Equal(2,Directory.GetFiles(directory,"*.json").Length);
    }
    [FunctionalFact]
    public async Task GivenNewRecoverableTrashAfterAdmission_WhenPurgeCompletes_ThenKeepUnmarkedNewTrashAndItsOriginalOperation()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var first=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");TrashRestoreSetup.Root? later=null;var injected=false;
        var ledger=new PermanentDeleteSetup.LedgerBoundary(Ledger,async()=>{if(injected)return;injected=true;later=await TrashRestoreSetup.RootAsync(fixture,s,keys,"collection");});
        var result=await Store(ledger).EmptyAsync(Request(s),default);Assert.Null(result.Error);Assert.Equal(1,result.Data!.TotalPurged);Assert.False(await Exists(first));Assert.True(await Exists(later!));
        await using var db=fixture.CreateContext();Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="collection"&&x.ResourceId==later!.ResourceId));Assert.True(await db.TrashOperations.AnyAsync(x=>x.PublicId==later!.OperationId));
        Assert.Equal(later!.PurgeAt,(await db.Collections.AsNoTracking().SingleAsync(x=>x.PublicId==later.ResourceId)).PurgeAt);
        Assert.Null((await Store().EmptyAsync(Request(s),default)).Error);Assert.False(await Exists(later));
    }
    [FunctionalFact]
    public async Task GivenLedgerFailsAfterFirstDurableEntry_WhenEmptyingTwoKinds_ThenNoPhysicalRemovalUntilAllEntriesDurable()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var first=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");var second=await TrashRestoreSetup.RootAsync(fixture,s,keys,"folder");var calls=0;
        var ledger=new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>{if(++calls==2)throw new IOException("fixture partial ledger outage");return Task.CompletedTask;});
        Assert.Equal("persistence_unavailable",(await Store(ledger).EmptyAsync(Request(s),default)).Error);Assert.True(await Exists(first));Assert.True(await Exists(second));Assert.Single(Directory.GetFiles(directory,"*.json"));
        await Expire(s);var retry=await Store().EmptyAsync(Request(s),default);Assert.Null(retry.Error);Assert.Equal(2,retry.Data!.TotalPurged);Assert.False(await Exists(first));Assert.False(await Exists(second));Assert.Equal(2,Directory.GetFiles(directory,"*.json").Length);
    }
    [FunctionalTheory][InlineData("record",false)][InlineData("folder",false)][InlineData("collection",false)][InlineData("profile",false)]
    [InlineData("record",true)][InlineData("folder",true)][InlineData("collection",true)][InlineData("profile",true)]
    [InlineData("trash_entry",false)][InlineData("trash_operation",false)][InlineData("retention_work_item",false)]
    public async Task GivenFaultAfterPhysicalMutation_WhenEmptying_ThenRollbackPhysicalPassAndKeepDurableIrreversibleIntent(string table,bool cancel)
    {
        var kind=table.StartsWith("trash_",StringComparison.Ordinal)||table=="retention_work_item"?"record":table;
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);var before=await TrashRestoreSetup.Snapshot(fixture,s);using var ct=new CancellationTokenSource();
        var hook=new AfterPurge(table,()=>{if(cancel){ct.Cancel();throw new OperationCanceledException(ct.Token);}throw new TimeoutException("fixture physical purge failure");});var store=Store(factory:new ProfileSetup.Factory(fixture,hook));
        if(cancel)await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.EmptyAsync(Request(s),ct.Token));else Assert.Equal("persistence_unavailable",(await store.EmptyAsync(Request(s),default)).Error);
        Assert.True(hook.Reached);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));Assert.True(await Exists(root));Assert.Single(Directory.GetFiles(directory,"*.json"));
        await using(var db=fixture.CreateContext()){Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind==kind&&x.ResourceId==root.ResourceId));Assert.Null((await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId)).CompletedAt);}
        await Expire(s);Assert.Null((await Store().EmptyAsync(Request(s),default)).Error);Assert.False(await Exists(root));
    }
    [FunctionalFact]
    public async Task GivenConcurrentRestoreAndEmpty_WhenBothRun_ThenOnlyCurrentTrashCanBeErased()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var restore=new TrashRestoreStore(fixture).RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),ct.Token);var empty=Store().EmptyAsync(Request(s),ct.Token);
        var restored=await restore;var purged=await empty;Assert.Null(purged.Error);
        if(restored.Error is null){Assert.Equal(0,purged.Data!.TotalPurged);Assert.True(await Exists(root));await using var db=fixture.CreateContext();Assert.Null((await db.Records.AsNoTracking().SingleAsync(x=>x.PublicId==root.ResourceId)).DeletedAt);Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="record"&&x.ResourceId==root.ResourceId));}
        else{Assert.Equal("not_found",restored.Error);Assert.Equal(1,purged.Data!.TotalPurged);Assert.False(await Exists(root));}
    }
    [FunctionalFact]
    public async Task GivenPendingTrashPurge_WhenAccountIsPermanentlyDeleted_ThenRemoveItsCoalescedJobToo()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");
        Assert.Equal("persistence_unavailable",(await Store(new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>throw new IOException("fixture pending trash"))).EmptyAsync(Request(s),default)).Error);
        var deleted=await new AccountDeletionStore(fixture,Ledger,new CerberusOptions{RetentionInterval="00:05:00"},TimeProvider.System).DeleteAsync(new(s.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),s.Verifier,1),default);Assert.Null(deleted.Error);Assert.Equal("completed",deleted.Details!.State);
        await using var db=fixture.CreateContext();Assert.False(await db.Accounts.AnyAsync(x=>x.Id==s.InternalId));Assert.False(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId));
    }
    private async Task Expire(ProfileSetup.State s){await using var db=fixture.CreateContext();await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash-erase/"+s.AccountId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));}
    private sealed class AfterPurge(string table,Action action):DbCommandInterceptor
    {
        internal bool Reached{get;private set;}
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default){if(!Reached&&c.CommandText.StartsWith("DELETE FROM cerberus."+table+" ",StringComparison.Ordinal)){Reached=true;action();}return ValueTask.FromResult(r);}
    }
}
