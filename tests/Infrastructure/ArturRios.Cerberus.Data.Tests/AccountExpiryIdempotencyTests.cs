using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountDeletionStoreTests
{
    [FunctionalFact]
    public async Task GivenAlreadyPurgedAccountWithLiveOriginalJob_WhenAccountExpiryRepeats_ThenCompleteIdempotently()
    {
        var w=await Create();var claim=await ExpiredClosure(w);await ExpiryExecutor().ExecuteAsync(claim,default);var files=Directory.GetFiles(directory,"*.json").Order().ToArray();Assert.NotEmpty(files);
        await ExpiryExecutor().ExecuteAsync(claim,default);Assert.Equal(files,Directory.GetFiles(directory,"*.json").Order().ToArray());Assert.True(await new RetentionWorkStore(fixture).TryCompleteAsync(claim,DateTimeOffset.UnixEpoch,default));await using var db=fixture.CreateContext();await Purged(db,w);
    }
    [UnitTheory][InlineData("account-purge")][InlineData("account-purge/00000000-0000-0000-0000-000000000000")]
    [InlineData("account-purge/12345678-1234-1234-1234-1234567890AB")][InlineData("account-purge/12345678-1234-1234-1234-1234567890ab/extra")]
    public async Task GivenMalformedAccountExpiryKey_WhenExecuting_ThenRejectBeforeDatabaseOrLedger(string key)
    {
        var claim=new RetentionClaim(Guid.NewGuid(),Guid.NewGuid(),key,DateTimeOffset.UtcNow.AddMinutes(5));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new AccountExpiryHandler(new NoAccountExpiryDatabase(),Ledger).ExecuteAsync(claim,default));Assert.Empty(Directory.GetFiles(directory,"*.json"));
    }
    private sealed class NoAccountExpiryDatabase:IDbContextFactory<AppDbContext>
    {public AppDbContext CreateDbContext()=>throw new IOException("Unexpected database access");}
}
