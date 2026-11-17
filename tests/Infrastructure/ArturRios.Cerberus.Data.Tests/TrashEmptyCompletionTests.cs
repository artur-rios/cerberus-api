using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Tests;

public partial class TrashEmptyStoreTests
{
    [FunctionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenCallerClockDisagreesWithDatabaseLease_WhenCompletingTrashPurge_ThenOnlyPersistedDatabaseTimeControlsCompletion(bool futureLive)
    {
        using var keys = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, keys);
        await using var db = fixture.CreateContext();
        var work = new RetentionWorkItem { OperationKey = "trash-erase/" + s.AccountId, DueAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        db.RetentionWorkItems.Add(work);
        await db.SaveChangesAsync();
        var jobs = new RetentionWorkStore(fixture);
        var claim = await jobs.TryClaimAsync(work.PublicId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(claim);
        if (!futureLive)
            await db.RetentionWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(x => x.SetProperty(w => w.ClaimExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var before = DateTimeOffset.UtcNow;
        var result = await jobs.TryCompleteAsync(claim!, DateTimeOffset.UtcNow.AddHours(futureLive ? 1 : -1), default);
        Assert.Equal(futureLive, result);
        var after = await db.RetentionWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id);
        if (futureLive)
        { Assert.NotNull(after.CompletedAt); Assert.True(after.CompletedAt >= before); Assert.True(after.CompletedAt <= DateTimeOffset.UtcNow); }
        else
            Assert.Null(after.CompletedAt);
    }
    [FunctionalFact]
    public async Task GivenLeaseExpiresDuringActualJobRowWait_WhenCompletingTrashPurge_ThenPastCallerClockCannotAcknowledgeIt()
    {
        using var keys = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, keys);
        await using var db = fixture.CreateContext();
        var work = new RetentionWorkItem { OperationKey = "trash-erase/" + s.AccountId, DueAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        db.RetentionWorkItems.Add(work);
        await db.SaveChangesAsync();
        var claim = await new RetentionWorkStore(fixture).TryClaimAsync(work.PublicId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), default);
        Assert.NotNull(claim);
        await using var held = fixture.CreateContext();
        await using var tx = await held.Database.BeginTransactionAsync();
        await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.retention_work_item WHERE id={work.Id} FOR UPDATE");
        // The holder holds the job row, so it arms the lease there when the store reaches the wait.
        var signal = new PermanentDeleteSetup.LockSignal("retention_work_item");
        var arm = RealTime.ArmInHolder(signal.Reached.Task, held, "UPDATE cerberus.retention_work_item SET claim_expires_at=@deadline WHERE id=@id", ("id", work.Id));
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = new RetentionWorkStore(new ProfileSetup.Factory(fixture, signal, arm)).TryCompleteAsync(claim!, DateTimeOffset.UtcNow.AddHours(-1), ct.Token);
        try
        { await signal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(pending.IsCompleted); await RealTime.Past(await arm.Deadline); }
        finally { await tx.CommitAsync(); }
        Assert.False(await pending);
        Assert.Null((await db.RetentionWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id)).CompletedAt);
    }
}
