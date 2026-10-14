using System.Data.Common;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountClosureStoreTests
{
    [FunctionalTheory][InlineData("suppressed")][InlineData("timeout")][InlineData("cancel")]
    public async Task GivenSuppressedWriteDependencyFailureOrCancellation_WhenClosing_ThenRollbackAndPermitCleanRetry(string kind)
    {
        using var key=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,key);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var hook=new ReductionFailure(kind,ct);var store=new AccountClosureStore(new ProfileSetup.Factory(fixture,hook));
        if(kind=="cancel")await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.RequestAsync(Request(s),ct.Token));
        else Assert.Equal("persistence_unavailable",(await store.RequestAsync(Request(s),ct.Token)).Error);
        await using var db=fixture.CreateContext();var a=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId);
        Assert.Equal(AccountState.Active,a.State);Assert.Equal(1,a.Revision);Assert.Equal(1,a.RevocationGeneration);
        Assert.True(await db.VaultAccessSessions.AnyAsync(x=>x.AccountId==s.InternalId&&!x.Revoked));
        Assert.False(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="account-purge/"+s.AccountId));
        Assert.Null((await new AccountClosureStore(fixture).RequestAsync(Request(s),default)).Error);
    }
    private sealed class ReductionFailure(string kind,CancellationTokenSource source):DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> result,CancellationToken ct=default)
        {
            if(c.CommandText.Contains("UPDATE cerberus.account SET state="))
            {
                if(kind=="suppressed")return ValueTask.FromResult(InterceptionResult<int>.SuppressWithResult(0));
            }
            if(c.CommandText.Contains("UPDATE cerberus.vault_access_session"))
            {
                if(kind=="timeout")throw new TimeoutException("private dependency detail");
                if(kind=="cancel"){source.Cancel();ct.ThrowIfCancellationRequested();}
            }
            return ValueTask.FromResult(result);
        }
    }
}
