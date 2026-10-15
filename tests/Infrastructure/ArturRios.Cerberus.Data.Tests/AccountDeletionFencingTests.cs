using System.Data.Common;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountDeletionStoreTests
{
    private async Task<(World World,RetentionClaim Claim)> Pending()
    {
        var w=await Create();Assert.Equal("persistence_unavailable",(await Store(ledger:new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>throw new IOException("fixture pending"))).DeleteAsync(Request(w.Owner),default)).Error);
        await using var db=fixture.CreateContext();var job=await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="account-erase/"+w.Owner.AccountId);
        return(w,new(job.PublicId,job.ClaimToken!.Value,job.OperationKey,job.ClaimExpiresAt!.Value));
    }
    [FunctionalTheory][InlineData("expired")][InlineData("stolen")]
    public async Task GivenClaimChangesDuringLedgerIo_WhenPurging_ThenKeepCiphertextAndRetryUnderNewClaim(string fault)
    {
        var(w,claim)=await Pending();string? expected=null;var injected=false;
        var ledger=new PermanentDeleteSetup.LedgerBoundary(Ledger,async()=>
        {
            if(injected)return;injected=true;await using var db=fixture.CreateContext();
            if(fault=="expired")await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));
            else await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ClaimToken,Guid.NewGuid()));
            expected=await Snapshot(db,w.Owner);
        });
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new AccountPurgeHandler(fixture,ledger).ExecuteAsync(claim,default));
        await using var check=fixture.CreateContext();Assert.Equal(expected,await Snapshot(check,w.Owner));Assert.True(await check.Records.AnyAsync(x=>x.Id==w.Owned.Record.Id));Assert.NotEmpty(Directory.EnumerateFiles(directory,"*.json"));
        await check.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));
        var next=await new RetentionWorkStore(fixture).TryClaimAsync(claim.WorkId,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(1),default);Assert.NotNull(next);
        var handler=new AccountPurgeHandler(fixture,Ledger);await handler.ExecuteAsync(next!,default);var files=Directory.GetFiles(directory,"*.json").Length;await handler.ExecuteAsync(next!,default);
        Assert.Equal(files,Directory.GetFiles(directory,"*.json").Length);Assert.True(await new RetentionWorkStore(fixture).TryCompleteAsync(next!,DateTimeOffset.UtcNow,default));await Purged(check,w);
    }
    [FunctionalTheory][InlineData("account")][InlineData("registration_operation")][InlineData("retention_work_item")]
    public async Task GivenClaimExpiresImmediatelyBeforeMutation_WhenPurging_ThenGuardedStatementCannotDelete(string table)
    {
        var(w,claim)=await Pending();await using var db=fixture.CreateContext();var deadline=DateTimeOffset.UtcNow.AddSeconds(2);
        await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ClaimExpiresAt,deadline));var before=await Snapshot(db,w.Owner);var foreign=await Foreign(db,w);
        var hook=new BeforeDelete(table,async()=>{var left=deadline-DateTimeOffset.UtcNow;if(left>TimeSpan.Zero)await Task.Delay(left+TimeSpan.FromMilliseconds(50));});
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new AccountPurgeHandler(new ProfileSetup.Factory(fixture,hook),Ledger).ExecuteAsync(claim,default));
        Assert.True(hook.Reached);Assert.Equal(0,hook.Affected);Assert.Equal(before,await Snapshot(db,w.Owner));Assert.Equal(foreign,await Foreign(db,w));
    }
    [FunctionalFact]
    public async Task GivenFaultAfterPhysicalCascade_WhenPurging_ThenRollbackEveryOwnedAndForeignEffectBeforeRetry()
    {
        var(w,claim)=await Pending();await using var db=fixture.CreateContext();var before=await Snapshot(db,w.Owner);var foreign=await Foreign(db,w);
        await Assert.ThrowsAsync<TimeoutException>(()=>new AccountPurgeHandler(new ProfileSetup.Factory(fixture,new AfterCascade()),Ledger).ExecuteAsync(claim,default));
        Assert.Equal(before,await Snapshot(db,w.Owner));Assert.Equal(foreign,await Foreign(db,w));Assert.NotEmpty(Directory.GetFiles(directory,"*.json"));
        await new AccountPurgeHandler(fixture,Ledger).ExecuteAsync(claim,default);await Purged(db,w);
    }
    [FunctionalTheory][InlineData("root","access")][InlineData("session","access")][InlineData("root","freshness")]
    [InlineData("session","freshness")][InlineData("root","cancel")][InlineData("session","cancel")]
    public async Task GivenRealAuthorityLockWait_WhenAccessOrIdentityExpiresOrCallerCancels_ThenNoIntentOrReduction(string table,string fault)
    {
        var w=await Create();await using var held=fixture.CreateContext();var request=Request(w.Owner);
        if(fault=="access")await held.VaultAccessSessions.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,DateTimeOffset.UtcNow.AddSeconds(2)));
        if(fault=="freshness")request=request with{AuthenticationIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-55};
        var before=await Snapshot(held,w.Owner);await using var tx=await held.Database.BeginTransactionAsync();
        if(table=="root")await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={w.Owner.InternalId} FOR UPDATE");
        else await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={w.Owner.InternalId} FOR UPDATE");
        var ready=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var signal=new BeforeAuthorityWait(table,()=>ready.TrySetResult());var task=Store(new ProfileSetup.Factory(fixture,signal)).DeleteAsync(request,ct.Token);
        try{await ready.Task.WaitAsync(ct.Token);Assert.False(task.IsCompleted);if(fault=="cancel")ct.Cancel();else await Task.Delay(TimeSpan.FromSeconds(fault=="freshness"?6:3),ct.Token);}
        finally{await tx.CommitAsync();}
        if(fault=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>task);
        else Assert.Equal(fault=="access"?"vault_access_denied":"fresh_authentication_required",(await task).Error);
        Assert.Equal(before,await Snapshot(held,w.Owner));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenExpiryAfterSessionAdmission_WhenContinuing_ThenKeepSessionAdmissionButRequireFreshIdentityAtClosureCas(bool identity)
    {
        var w=await Create();await using var db=fixture.CreateContext();var request=Request(w.Owner);
        if(identity)request=request with{AuthenticationIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-55};
        else await db.VaultAccessSessions.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,DateTimeOffset.UtcNow.AddSeconds(2)));
        var before=await Snapshot(db,w.Owner);var hook=new AfterAdmission(identity?6:3);
        var result=await Store(new ProfileSetup.Factory(fixture,hook)).DeleteAsync(request,default);Assert.True(hook.Reached);Assert.Equal(1,hook.Affected);
        if(identity){Assert.Equal("fresh_authentication_required",result.Error);Assert.Equal(before,await Snapshot(db,w.Owner));}
        else{Assert.Null(result.Error);await Purged(db,w);}
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenExpiredDatabaseClaim_WhenCompletingWithOldCallerClock_ThenNeverAcknowledgeErasure(bool afterWait)
    {
        var(w,claim)=await Pending();await new AccountPurgeHandler(fixture,Ledger).ExecuteAsync(claim,default);
        await using var db=fixture.CreateContext();var old=DateTimeOffset.UtcNow;var deadline=old.AddSeconds(afterWait?2:-1);
        await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ClaimExpiresAt,deadline));
        var factory=afterWait?new ProfileSetup.Factory(fixture,new CompletionDelay(deadline)):fixture as IDbContextFactory<AppDbContext>;
        Assert.False(await new RetentionWorkStore(factory!).TryCompleteAsync(claim,old.AddMinutes(-1),default));Assert.Null(await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).Select(x=>x.CompletedAt).SingleAsync());
    }
    private sealed class BeforeDelete(string table,Func<Task> action):DbCommandInterceptor
    {
        public bool Reached{get;private set;}public int Affected{get;private set;}=-1;
        private bool Match(DbCommand c)=>c.CommandText.StartsWith("DELETE FROM cerberus."+table+" ",StringComparison.Ordinal);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default)
        {if(!Reached&&Match(c)){Reached=true;await action();}return r;}
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default)
        {if(Match(c))Affected=r;return ValueTask.FromResult(r);}
    }
    private sealed class AfterCascade:DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default)
        {if(c.CommandText.StartsWith("DELETE FROM cerberus.account ",StringComparison.Ordinal))throw new TimeoutException("fixture post-cascade");return ValueTask.FromResult(r);}
    }
    private sealed class BeforeAuthorityWait(string table,Action signal):DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default)
        {if(c.CommandText.Contains(table=="root"?"FROM cerberus.account":"FROM cerberus.vault_access_session")&&c.CommandText.Contains("FOR UPDATE"))signal();return ValueTask.FromResult(r);}
    }
    private sealed class AfterAdmission(int seconds):DbCommandInterceptor
    {
        public bool Reached{get;private set;}public int Affected{get;private set;}
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default)
        {if(!Reached&&c.CommandText.Contains("UPDATE cerberus.account a SET concurrency_stamp=")){Reached=true;Affected=r;await Task.Delay(TimeSpan.FromSeconds(seconds),ct);}return r;}
    }
    private sealed class CompletionDelay(DateTimeOffset deadline):DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default)
        {if(c.CommandText.Contains("UPDATE cerberus.retention_work_item SET completed_at=")){var left=deadline-DateTimeOffset.UtcNow;if(left>TimeSpan.Zero)await Task.Delay(left+TimeSpan.FromMilliseconds(50),ct);}return r;}
    }
}
