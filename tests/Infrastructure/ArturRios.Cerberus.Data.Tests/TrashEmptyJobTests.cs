using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Tests;

public partial class TrashEmptyStoreTests
{
    [FunctionalFact]
    public async Task GivenPayloadAlreadyMissingButOwnedTrashMetadataRemains_WhenEmptying_ThenCleanOrphansWithZeroNativeCounts()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.PublicId==root.ResourceId).ExecuteDeleteAsync();
        var r=await Store(new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>throw new IOException("no cipher ledger entry needed"))).EmptyAsync(Request(s),default);Assert.Null(r.Error);Assert.Equal(0,r.Data!.TotalPurged);Assert.All(r.Data.Counts,x=>Assert.Equal(0,x.Count));await TrashRestoreSetup.NoOperation(fixture,root.OperationId);Assert.Empty(Directory.GetFiles(directory,"*.json"));
        await using var check=fixture.CreateContext();Assert.NotNull((await check.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId)).CompletedAt);
    }
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenOriginalExpiryWorkerHasLiveClaim_WhenEmptying_ThenPreserveItsJobForCompletion(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);await using var db=fixture.CreateContext();var job=await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="trash/"+root.OperationId);job.DueAt=DateTimeOffset.UtcNow.AddMinutes(-1);await db.SaveChangesAsync();
        var jobs=new RetentionWorkStore(fixture);var claim=await jobs.TryClaimAsync(job.PublicId,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(5),default);Assert.NotNull(claim);
        Assert.Null((await Store().EmptyAsync(Request(s),default)).Error);Assert.False(await Exists(root));Assert.False(await db.TrashOperations.AnyAsync(x=>x.PublicId==root.OperationId));var retained=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.Id==job.Id);Assert.Equal(claim!.Token,retained.ClaimToken);Assert.Null(retained.CompletedAt);Assert.True(await jobs.TryCompleteAsync(claim,DateTimeOffset.UtcNow,default));
    }
    [FunctionalFact]
    public async Task GivenWideSessionExpiresAfterGuardedAdmission_WhenIntentContinues_ThenCompleteAdmittedErasure()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));
        var hook=new BeforeEmptyStatement(c=>c.CommandText.Contains("INSERT INTO cerberus.terminal_erasure AS t",StringComparison.Ordinal),()=>WaitPast(deadline));var r=await Store(factory:new ProfileSetup.Factory(fixture,hook)).EmptyAsync(Request(s),default);Assert.True(hook.Reached);Assert.Null(r.Error);Assert.Equal(1,r.Data!.TotalPurged);Assert.False(await Exists(root));
    }
}

public sealed class TrashPurgeKeyTests
{
    [UnitTheory][InlineData("")][InlineData("trash-erase")][InlineData("trash-erase/not-a-guid")]
    [InlineData("trash-erase/00000000-0000-0000-0000-000000000000")]
    [InlineData("trash-erase/ABCDEFAB-1234-1234-1234-123456789ABC")]
    [InlineData("trash-erase/abcdefab123412341234123456789abc")]
    [InlineData("trash/abcdefab-1234-1234-1234-123456789abc")]
    [InlineData("trash-erase/abcdefab-1234-1234-1234-123456789abc/extra")]
    public async Task GivenMalformedTrashPurgeKey_WhenExecuting_ThenRejectBeforeOpeningDatabaseOrLedger(string key)
    {
        var factory=new RefuseDatabase();var ledger=new RefuseLedger();Assert.Equal("trash-erase",new TrashPurgeHandler(factory,ledger).Kind);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new TrashPurgeHandler(factory,ledger).ExecuteAsync(new(Guid.NewGuid(),Guid.NewGuid(),key,DateTimeOffset.UtcNow.AddMinutes(5)),default));Assert.False(factory.Used);Assert.False(ledger.Used);
    }
    private sealed class RefuseDatabase:IDbContextFactory<AppDbContext>
    {internal bool Used{get;private set;}public AppDbContext CreateDbContext(){Used=true;throw new IOException("database must not be touched");}}
    private sealed class RefuseLedger:IErasureLedger
    {
        internal bool Used{get;private set;}
        public Task RecordAsync(ErasureEntry entry,CancellationToken ct){Used=true;throw new IOException("ledger must not be touched");}
        public IAsyncEnumerable<ErasureEntry> ReadAsync(CancellationToken ct){Used=true;throw new IOException("ledger must not be touched");}
    }
}
