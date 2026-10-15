using System.Data.Common;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

public partial class RetentionMaintenanceTests
{
    [FunctionalFact]
    public async Task GivenDueTrashAndClosure_WhenMaintainingOneJobPage_ThenShareOneSchedulingBudget()
    {
        using var keys=new ProtectionFixture();var s=await Owner(keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));Assert.Null((await new AccountClosureStore(fixture).RequestAsync(new(s.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),1),default)).Error);
        await using var db=fixture.CreateContext();var due=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp()-interval '1 second' AS \"Value\"").SingleAsync();await db.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ClosureRequestedAt,due.AddDays(-30)).SetProperty(a=>a.ClosurePurgeAt,due));var original=new[]{"trash/"+root.OperationId,"account-purge/"+s.AccountId};await db.RetentionWorkItems.Where(x=>original.Contains(x.OperationKey)).ExecuteDeleteAsync();
        Assert.Equal(1,(await Run(pageSize:1)).ScheduledJobs);Assert.Equal(1,await db.RetentionWorkItems.CountAsync(x=>original.Contains(x.OperationKey)));Assert.Equal(1,(await Run(pageSize:1)).ScheduledJobs);Assert.Equal(2,await db.RetentionWorkItems.CountAsync(x=>original.Contains(x.OperationKey)));
    }
    [FunctionalFact]
    public async Task GivenPredeadlineRestoreWhileJobIsMissing_WhenMaintenanceWaitsAfterDeadline_ThenNeverRecreateObsoleteExpiry()
    {
        using var keys=new ProtectionFixture();var s=await Owner(keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");var deadline=DateTimeOffset.UtcNow.AddSeconds(5);await TrashRestoreSetup.Deadline(fixture,root,deadline);await using(var db=fixture.CreateContext())await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+root.OperationId).ExecuteDeleteAsync();
        var admitted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var gate=new AfterMaintenanceRestore(async()=>{admitted.TrySetResult();await release.Task.WaitAsync(TimeSpan.FromSeconds(25));});
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(35));var restoring=new TrashRestoreStore(new ProfileSetup.Factory(fixture,gate)).RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),timeout.Token);Task<ArturRios.Cerberus.Shared.Operations.RetentionRunResult>? pending=null;
        try{await admitted.Task.WaitAsync(TimeSpan.FromSeconds(15));await MaintenanceWaitPast(deadline);var signal=new PermanentDeleteSetup.LockSignal("account");pending=Run(new ProfileSetup.Factory(fixture,signal));await signal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);}finally{release.TrySetResult();}
        Assert.Null((await restoring).Error);Assert.NotNull(pending);Assert.Equal(0,(await pending).ScheduledJobs);await using var check=fixture.CreateContext();Assert.False(await check.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="trash/"+root.OperationId));Assert.True(await check.Records.AnyAsync(x=>x.PublicId==root.ResourceId&&x.DeletedAt==null&&x.Revision==root.Revision+1));
    }
    [FunctionalFact]
    public async Task GivenClosureStateChangesWhileOwnerIsLocked_WhenRepairingMissingJob_ThenRecheckCurrentStateAfterWait()
    {
        using var keys=new ProtectionFixture();var s=await Owner(keys);Assert.Null((await new AccountClosureStore(fixture).RequestAsync(new(s.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),1),default)).Error);await using var held=fixture.CreateContext();var due=await held.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp()-interval '1 second' AS \"Value\"").SingleAsync();await held.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ClosureRequestedAt,due.AddDays(-30)).SetProperty(a=>a.ClosurePurgeAt,due));await held.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+s.AccountId).ExecuteDeleteAsync();
        await using var tx=await held.Database.BeginTransactionAsync();await held.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.Active).SetProperty(a=>a.ClosureRequestedAt,(DateTimeOffset?)null).SetProperty(a=>a.ClosurePurgeAt,(DateTimeOffset?)null).SetProperty(a=>a.Revision,3).SetProperty(a=>a.RevocationGeneration,3));
        var signal=new PermanentDeleteSetup.LockSignal("account");var pending=Run(new ProfileSetup.Factory(fixture,signal));try{await signal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);}finally{await tx.CommitAsync();}
        Assert.Equal(0,(await pending).ScheduledJobs);Assert.False(await held.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="account-purge/"+s.AccountId));Assert.Equal(AccountState.Active,await held.Accounts.Where(x=>x.Id==s.InternalId).Select(x=>x.State).SingleAsync());
    }
    [FunctionalFact]
    public async Task GivenJobCreatedAndClaimedWhileOwnerWaits_WhenRepairing_ThenPreserveWinningClaimExactly()
    {
        using var keys=new ProtectionFixture();var s=await Owner(keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));await using var held=fixture.CreateContext();var op=await held.TrashOperations.SingleAsync(x=>x.PublicId==root.OperationId);await held.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+root.OperationId).ExecuteDeleteAsync();
        await using var tx=await held.Database.BeginTransactionAsync();await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");var job=new RetentionWorkItem{OperationKey="trash/"+root.OperationId,DueAt=op.PurgeAt,ClaimToken=Guid.NewGuid(),ClaimExpiresAt=DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.AddHours(1).ToUnixTimeMilliseconds()),Attempts=7};held.Add(job);await held.SaveChangesAsync();var snapshot=System.Text.Json.JsonSerializer.Serialize(job);
        var signal=new PermanentDeleteSetup.LockSignal("account");var pending=Run(new ProfileSetup.Factory(fixture,signal));try{await signal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);}finally{await tx.CommitAsync();}
        Assert.Equal(0,(await pending).ScheduledJobs);Assert.Equal(snapshot,System.Text.Json.JsonSerializer.Serialize(await held.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.PublicId==job.PublicId)));
    }
    private static async Task MaintenanceWaitPast(DateTimeOffset deadline){var wait=deadline-DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(30);if(wait>TimeSpan.Zero)await Task.Delay(wait);}
    private sealed class AfterMaintenanceRestore(Func<Task> action):DbCommandInterceptor
    {
        private bool reached;
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default)
        {if(!reached&&c.CommandText.Contains("DELETE FROM cerberus.retention_work_item",StringComparison.Ordinal)){reached=true;await action();}return r;}
    }
}
