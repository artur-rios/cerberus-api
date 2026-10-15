using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public sealed class RetentionExpiryQueueTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("trash")][InlineData("account-purge")]
    public async Task GivenFutureExpiry_WhenCallerClockIsFast_ThenNeitherDiscoverNorClaim(string kind)
    {
        var work=await Seed(kind,1);var store=new RetentionWorkStore(fixture);var fast=DateTimeOffset.UtcNow.AddDays(100);
        Assert.DoesNotContain(work.PublicId,await store.GetDueAsync(fast,100000,default));
        Assert.Null(await store.TryClaimAsync(work.PublicId,fast,TimeSpan.FromMinutes(5),default));
        await using var db=fixture.CreateContext();Assert.Equal(0,(await db.RetentionWorkItems.SingleAsync(x=>x.PublicId==work.PublicId)).Attempts);
    }
    [FunctionalTheory][InlineData("trash")][InlineData("account-purge")]
    public async Task GivenDueExpiry_WhenCallerClockIsSlow_ThenDiscoverAndClaimUsingDatabaseTime(string kind)
    {
        var work=await Seed(kind,-1);var store=new RetentionWorkStore(fixture);var slow=DateTimeOffset.UnixEpoch;
        Assert.Contains(work.PublicId,await store.GetDueAsync(slow,100000,default));
        var claim=await store.TryClaimAsync(work.PublicId,slow,TimeSpan.FromMinutes(5),default);Assert.NotNull(claim);
        Assert.InRange(claim.ExpiresAt,DateTimeOffset.UtcNow.AddMinutes(4),DateTimeOffset.UtcNow.AddMinutes(6));
        Assert.True(await store.TryCompleteAsync(claim,slow,default));
        await using var db=fixture.CreateContext();var done=await db.RetentionWorkItems.SingleAsync(x=>x.PublicId==work.PublicId);
        Assert.InRange(done.CompletedAt!.Value,DateTimeOffset.UtcNow.AddMinutes(-1),DateTimeOffset.UtcNow.AddMinutes(1));
    }
    [FunctionalTheory][InlineData("trash")][InlineData("account-purge")]
    public async Task GivenExpiredPersistedLease_WhenCallerClockIsSlow_ThenRejectCompletion(string kind)
    {
        var work=await Seed(kind,-1);var store=new RetentionWorkStore(fixture);
        var claim=await store.TryClaimAsync(work.PublicId,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(5),default);Assert.NotNull(claim);
        await using var db=fixture.CreateContext();await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET claim_expires_at=statement_timestamp()-interval '1 second' WHERE public_id={work.PublicId}");
        Assert.False(await store.TryCompleteAsync(claim,DateTimeOffset.UnixEpoch,default));
        Assert.Null((await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.PublicId==work.PublicId)).CompletedAt);
    }
    private async Task<RetentionWorkItem> Seed(string kind,int dayOffset)
    {
        await using var db=fixture.CreateContext();var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync();
        var work=new RetentionWorkItem{OperationKey=kind+"/"+Guid.NewGuid(),DueAt=now.AddDays(dayOffset)};db.Add(work);await db.SaveChangesAsync();return work;
    }
}
