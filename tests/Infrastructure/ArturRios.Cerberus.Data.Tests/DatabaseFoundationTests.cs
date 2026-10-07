using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class DatabaseFoundationTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenFoundationSchema_WhenStartingDatabase_ThenApplyVersionedMigrations()
    {
        await using var context = fixture.CreateContext();
        Assert.NotEmpty(await context.Database.GetAppliedMigrationsAsync());
    }

    [FunctionalFact]
    public void GivenFoundationModel_WhenMapping_ThenUseSingularSnakeCaseTables()
    {
        using var context = fixture.CreateContext();
        var entity = context.Model.FindEntityType(typeof(RetentionWorkItem))!;
        Assert.Equal("retention_work_item", entity.GetTableName());
        Assert.Equal("cerberus", entity.GetSchema());
    }

    [FunctionalFact]
    public async Task GivenDuplicateOperationKey_WhenSaving_ThenRejectSecondOperation()
    {
        var operation = Guid.NewGuid().ToString("N");
        await using var context = fixture.CreateContext();
        context.RetentionWorkItems.Add(new RetentionWorkItem { OperationKey = operation, DueAt = DateTimeOffset.UtcNow });
        await context.SaveChangesAsync();
        context.RetentionWorkItems.Add(new RetentionWorkItem { OperationKey = operation, DueAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => context.SaveChangesAsync());
        await using var verification = fixture.CreateContext();
        Assert.Equal(1, await verification.RetentionWorkItems.CountAsync(x => x.OperationKey == operation));
    }

    [FunctionalFact]
    public async Task GivenStaleRevision_WhenSaving_ThenPreserveWinningState()
    {
        var operation = Guid.NewGuid().ToString("N");
        await using var first = fixture.CreateContext();
        var work = new RetentionWorkItem { OperationKey = operation, DueAt = DateTimeOffset.UtcNow };
        first.Add(work);
        await first.SaveChangesAsync();
        await using var stale = fixture.CreateContext();
        var staleWork = await stale.RetentionWorkItems.SingleAsync(x => x.OperationKey == operation);
        work.Attempts = 1;
        await first.SaveChangesAsync();
        staleWork.Attempts = 9;
        await Assert.ThrowsAsync<DbUpdateConcurrencyException>(() => stale.SaveChangesAsync());
        await using var verification = fixture.CreateContext();
        Assert.Equal(1, (await verification.RetentionWorkItems.SingleAsync(x => x.OperationKey == operation)).Attempts);
    }

    [FunctionalFact]
    public async Task GivenRolledBackTransaction_WhenReading_ThenDiscardUncommittedOperation()
    {
        var operation = Guid.NewGuid().ToString("N");
        await using (var context = fixture.CreateContext())
        {
            await using var transaction = await context.Database.BeginTransactionAsync();
            context.Add(new RetentionWorkItem { OperationKey = operation, DueAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        await using var verification = fixture.CreateContext();
        Assert.False(await verification.RetentionWorkItems.AnyAsync(x => x.OperationKey == operation));
    }
}
