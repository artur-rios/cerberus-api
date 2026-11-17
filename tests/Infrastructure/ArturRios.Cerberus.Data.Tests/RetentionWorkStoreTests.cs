using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public sealed class RetentionWorkStoreTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.Parse("2026-10-07T00:00:00Z");

    [FunctionalFact]
    public async Task GivenConcurrentWorkers_WhenClaimingOneOperation_ThenOnlyOneWins()
    {
        var id = await SeedAsync(s_now);
        var claims = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ =>
            new RetentionWorkStore(fixture).TryClaimAsync(id, s_now, TimeSpan.FromMinutes(5), default)));
        var claim = Assert.Single(claims, x => x is not null)!;
        Assert.Equal(id, claim.WorkId);
        await using var context = fixture.CreateContext();
        Assert.Equal(1, (await context.RetentionWorkItems.SingleAsync(x => x.PublicId == id)).Attempts);
    }

    [FunctionalFact]
    public async Task GivenCrashedWorker_WhenLeaseExpires_ThenNewClaimRejectsStaleCompletion()
    {
        var id = await SeedAsync(s_now);
        var store = new RetentionWorkStore(fixture);
        var old = await store.TryClaimAsync(id, s_now, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(old);
        Assert.Null(await store.TryClaimAsync(id, s_now.AddMinutes(4), TimeSpan.FromMinutes(5), default));
        var replacement = await store.TryClaimAsync(id, s_now.AddMinutes(5), TimeSpan.FromMinutes(5), default);
        Assert.NotNull(replacement);
        Assert.False(await store.TryCompleteAsync(old, s_now.AddMinutes(6), default));
        Assert.True(await store.TryCompleteAsync(replacement, s_now.AddMinutes(6), default));
        Assert.Null(await store.TryClaimAsync(id, s_now.AddMinutes(11), TimeSpan.FromMinutes(5), default));
        Assert.False(await store.TryCompleteAsync(replacement, s_now.AddMinutes(6), default));
    }

    [FunctionalFact]
    public async Task GivenFutureOrClaimedWork_WhenReadingDuePage_ThenRespectDeadlineAndLimit()
    {
        var due = await SeedAsync(s_now);
        var future = await SeedAsync(s_now.AddDays(1));
        var store = new RetentionWorkStore(fixture);
        Assert.Contains(due, await store.GetDueAsync(s_now, 100, default));
        Assert.DoesNotContain(future, await store.GetDueAsync(s_now, 100, default));
        Assert.Single(await store.GetDueAsync(s_now, 1, default));
        Assert.NotNull(await store.TryClaimAsync(due, s_now, TimeSpan.FromMinutes(5), default));
        Assert.DoesNotContain(due, await store.GetDueAsync(s_now, 100, default));
        Assert.Null(await store.TryClaimAsync(future, s_now, TimeSpan.FromMinutes(5), default));
    }

    private async Task<Guid> SeedAsync(DateTimeOffset due)
    {
        await using var context = fixture.CreateContext();
        var work = new RetentionWorkItem { OperationKey = Guid.NewGuid().ToString("N"), DueAt = due };
        context.Add(work);
        await context.SaveChangesAsync();
        return work.PublicId;
    }

    [FunctionalFact]
    public async Task GivenSubMicrosecondClockPrecision_WhenCompletingClaim_ThenHonorTokenDespiteDatabaseRounding()
    {
        var id = await SeedAsync(s_now);
        var store = new RetentionWorkStore(fixture);
        var claim = await store.TryClaimAsync(id, s_now.AddTicks(1), TimeSpan.FromMinutes(5), default);
        Assert.NotNull(claim);
        Assert.True(await store.TryCompleteAsync(claim, s_now.AddMinutes(1), default));
    }
}
