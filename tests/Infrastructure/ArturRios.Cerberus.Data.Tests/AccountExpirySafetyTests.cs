using System.Data.Common;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountDeletionStoreTests
{
    [FunctionalTheory][InlineData("account")][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")][InlineData("grant")]
    public async Task GivenLeaseExpiresAtActualTypedIntent_WhenAccountExpiryRuns_ThenRollbackEveryIntent(string kind)
    {
        var w=await Create();var claim=await ExpiredClosure(w);await using var db=fixture.CreateContext();var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(j=>j.ClaimExpiresAt,deadline));var before=await Snapshot(db,w.Owner);
        var hook=new BeforeExpiryIntent(kind,()=>WaitForExpiry(deadline));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ExpiryExecutor(factory:new ProfileSetup.Factory(fixture,hook)).ExecuteAsync(claim,default));Assert.True(hook.Reached);Assert.Equal(before,await Snapshot(db,w.Owner));Assert.Empty(Directory.GetFiles(directory,"*.json"));
        Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==w.Owner.AccountId));Assert.True(await db.Records.AnyAsync(x=>x.Id==w.Owned.Record.Id));
    }
    [FunctionalTheory][InlineData("owner")][InlineData("foreign")][InlineData("collection")][InlineData("job")]
    public async Task GivenLeaseExpiresDuringCanonicalRowWait_WhenAccountExpiryRuns_ThenNoIntentOrErasure(string resource)
    {
        var w=await Create();var claim=await ExpiredClosure(w);await using var held=fixture.CreateContext();var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await held.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(j=>j.ClaimExpiresAt,deadline));var before=await Snapshot(held,w.Owner);
        await using var tx=await held.Database.BeginTransactionAsync();var table=resource=="job"?"retention_work_item":resource=="collection"?"collection":"account";
        if(resource=="job")await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.retention_work_item WHERE public_id={claim.WorkId} FOR UPDATE");
        else if(resource=="collection")await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE id={w.Foreign.Collection.Id} FOR UPDATE");
        else await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={(resource=="owner"?w.Owner.InternalId:w.Other.InternalId)} FOR UPDATE");
        var signal=new PermanentDeleteSetup.LockSignal(table);using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(25));var pending=ExpiryExecutor(factory:new ProfileSetup.Factory(fixture,signal)).ExecuteAsync(claim,timeout.Token);
        try{await signal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);await WaitForExpiry(deadline);}finally{await tx.CommitAsync();}
        await Assert.ThrowsAsync<InvalidOperationException>(()=>pending);Assert.Equal(before,await Snapshot(held,w.Owner));Assert.Empty(Directory.GetFiles(directory,"*.json"));Assert.False(await held.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==w.Owner.AccountId));
    }
    [FunctionalFact]
    public async Task GivenCancellationAdmittedBeforeDeadline_WhenAccountExpiryWaits_ThenPreserveActiveWinnerAfterDeadline()
    {
        var w=await Create();var claim=await ExpiredClosure(w,true);var deadline=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddSeconds(5).ToUnixTimeMilliseconds());
        await using(var db=fixture.CreateContext()){
            await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ClosureRequestedAt,deadline.AddDays(-30)).SetProperty(a=>a.ClosurePurgeAt,deadline));
            await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(j=>j.DueAt,deadline));
        }
        var admitted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var gate=new AfterExpiryCancellation(async()=>{admitted.TrySetResult();await release.Task.WaitAsync(TimeSpan.FromSeconds(25));});
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));var cancelling=new AccountClosureCancellationStore(new ProfileSetup.Factory(fixture,gate)).CancelAsync(new(w.Owner.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),2),timeout.Token);Task? expiring=null;
        try{await admitted.Task.WaitAsync(TimeSpan.FromSeconds(15));var signal=new PermanentDeleteSetup.LockSignal("account");expiring=ExpiryExecutor(factory:new ProfileSetup.Factory(fixture,signal)).ExecuteAsync(claim,timeout.Token);await signal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(expiring.IsCompleted);await WaitForExpiry(deadline);}finally{release.TrySetResult();}
        Assert.Null((await cancelling).Error);Assert.NotNull(expiring);await Assert.ThrowsAsync<InvalidOperationException>(()=>expiring);await using var check=fixture.CreateContext();Assert.Equal(ArturRios.Cerberus.Domain.Accounts.AccountState.Active,await check.Accounts.Where(x=>x.Id==w.Owner.InternalId).Select(x=>x.State).SingleAsync());Assert.True(await check.Records.AnyAsync(x=>x.Id==w.Owned.Record.Id));Assert.True(await check.RetentionWorkItems.AnyAsync(x=>x.PublicId==claim.WorkId&&x.CompletedAt!=null&&x.ClaimToken==null&&x.ClaimExpiresAt==null));Assert.Empty(Directory.GetFiles(directory,"*.json"));
    }
    [FunctionalFact]
    public async Task GivenPhysicalCascadeFault_WhenAccountExpiryRetries_ThenRollbackNativeAndForeignEffects()
    {
        var w=await Create();var claim=await ExpiredClosure(w);await using var db=fixture.CreateContext();var before=await Snapshot(db,w.Owner);var foreign=await Foreign(db,w);
        await Assert.ThrowsAsync<TimeoutException>(()=>ExpiryExecutor(factory:new ProfileSetup.Factory(fixture,new AfterCascade())).ExecuteAsync(claim,default));Assert.Equal(before,await Snapshot(db,w.Owner));Assert.Equal(foreign,await Foreign(db,w));Assert.NotEmpty(Directory.GetFiles(directory,"*.json"));
        await ExpiryExecutor().ExecuteAsync(claim,default);await Purged(db,w);Assert.Equal(foreign,await Foreign(db,w));
    }
    private static async Task WaitForExpiry(DateTimeOffset deadline){var wait=deadline-DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(30);if(wait>TimeSpan.Zero)await Task.Delay(wait);}
    private sealed class BeforeExpiryIntent(string kind,Func<Task> wait):DbCommandInterceptor
    {
        internal bool Reached;
        private async Task Visit(DbCommand c)
        {
            if(!Reached&&c.CommandText.StartsWith("INSERT INTO cerberus.terminal_erasure",StringComparison.Ordinal)
                &&(kind=="account"?c.CommandText.Contains("SELECT 'account'",StringComparison.Ordinal):c.Parameters.Cast<DbParameter>().Any(x=>x.Value is string value&&value==kind)))
            {Reached=true;await wait();}
        }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){await Visit(c);return r;}
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){await Visit(c);return r;}
    }
    private sealed class AfterExpiryCancellation(Func<Task> action):DbCommandInterceptor
    {
        private bool reached;
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData d,DbDataReader r,CancellationToken ct=default)
        {if(!reached&&c.CommandText.Contains("UPDATE cerberus.retention_work_item w SET completed_at",StringComparison.Ordinal)){reached=true;await action();}return r;}
    }
}
