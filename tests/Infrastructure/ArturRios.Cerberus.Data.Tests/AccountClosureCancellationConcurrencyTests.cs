using System.Data.Common;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountClosureCancellationStoreTests
{
    [FunctionalFact]
    public async Task GivenConcurrentCancellationRequests_WhenCommitting_ThenOnlyOneRestoresAndNoOldAccessReturns()
    {
        using var key=new ProtectionFixture();var s=await Setup(key);await Close(s);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var count=0;
        var hook=new CancelBefore(c=>Root(c),async _=>{if(Interlocked.Increment(ref count)==2)ready.TrySetResult();await release.Task.WaitAsync(ct.Token);});
        var store=new AccountClosureCancellationStore(new ProfileSetup.Factory(fixture,hook));var a=store.CancelAsync(Request(s),ct.Token);var b=store.CancelAsync(Request(s),ct.Token);
        try{await ready.Task.WaitAsync(ct.Token);}finally{release.TrySetResult();}
        var result=await Task.WhenAll(a,b);Assert.Single(result,x=>x.Error is null);Assert.Single(result,x=>x.Error=="revision_conflict");
        await using var db=fixture.CreateContext();Assert.Equal(3,await db.Accounts.Where(x=>x.Id==s.InternalId).Select(x=>x.Revision).SingleAsync());
        Assert.False(await db.VaultAccessSessions.AnyAsync(x=>x.AccountId==s.InternalId&&!x.Revoked));
    }
    [FunctionalTheory][InlineData("root","deadline")][InlineData("work","deadline")][InlineData("root","freshness")]
    [InlineData("work","freshness")][InlineData("root","cancel")][InlineData("work","cancel")]
    public async Task GivenRealLockWait_WhenDeadlineOrFreshnessExpiresOrCallerCancels_ThenNeverRestore(string table,string change)
    {
        using var key=new ProtectionFixture();var s=await Setup(key);await Close(s);await using var held=fixture.CreateContext();
        if(change=="deadline")
        {
            await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET closure_requested_at=statement_timestamp()-interval '720 hours'+interval '2 seconds',closure_purge_at=statement_timestamp()+interval '2 seconds' WHERE id={s.InternalId}");
            await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET due_at=(SELECT closure_purge_at FROM cerberus.account WHERE id={s.InternalId}) WHERE operation_key={"account-purge/"+s.AccountId}");
        }
        await using var tx=await held.Database.BeginTransactionAsync();
        if(table=="root")await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
        else await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.retention_work_item WHERE operation_key={"account-purge/"+s.AccountId} FOR UPDATE");
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hook=new CancelBefore(c=>table=="root"?Root(c):c.CommandText.Contains("FROM cerberus.retention_work_item")&&c.CommandText.Contains("FOR UPDATE"),_=>{ready.TrySetResult();return Task.CompletedTask;});
        var request=Request(s);if(change=="freshness")request=request with{AuthenticationIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-55};
        var pending=new AccountClosureCancellationStore(new ProfileSetup.Factory(fixture,hook)).CancelAsync(request,ct.Token);
        try{await ready.Task.WaitAsync(ct.Token);Assert.False(pending.IsCompleted);if(change=="cancel")ct.Cancel();else await Task.Delay(TimeSpan.FromSeconds(change=="freshness"?6:3),ct.Token);}
        finally{await tx.CommitAsync();}
        if(change=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);
        else Assert.Equal(change=="deadline"?"closure_expired":"fresh_authentication_required",(await pending).Error);
        await using var db=fixture.CreateContext();Assert.Equal(AccountState.ClosurePending,await db.Accounts.Where(x=>x.Id==s.InternalId).Select(x=>x.State).SingleAsync());
        Assert.Null(await db.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+s.AccountId).Select(x=>x.CompletedAt).SingleAsync());
    }
    [FunctionalTheory][InlineData(10,null)][InlineData(0,"closure_expired")][InlineData(-1,"closure_expired")]
    public async Task GivenBeforeAtOrAfterOwnDeadline_WhenCancelling_ThenOnlyStrictlyBeforeCanRestore(int seconds,string? error)
    {
        using var key=new ProtectionFixture();var s=await Setup(key);await Close(s);await using var db=fixture.CreateContext();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET closure_purge_at=statement_timestamp()+({seconds}*interval '1 second'),closure_requested_at=statement_timestamp()+({seconds}*interval '1 second')-interval '720 hours' WHERE id={s.InternalId}");
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET due_at=(SELECT closure_purge_at FROM cerberus.account WHERE id={s.InternalId}) WHERE operation_key={"account-purge/"+s.AccountId}");
        var result=await new AccountClosureCancellationStore(fixture).CancelAsync(Request(s),default);Assert.Equal(error,result.Error);
        Assert.Equal(error is null?AccountState.Active:AccountState.ClosurePending,await db.Accounts.Where(x=>x.Id==s.InternalId).Select(x=>x.State).SingleAsync());
    }
    private static bool Root(DbCommand c)=>c.CommandText.Contains("FROM cerberus.account")&&c.CommandText.Contains("FOR UPDATE");
    private sealed class CancelBefore(Func<DbCommand,bool> match,Func<DbCommand,Task> action):DbCommandInterceptor
    {
        private async Task Check(DbCommand c){if(match(c))await action(c);}
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){await Check(c);return r;}
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){await Check(c);return r;}
    }
}
