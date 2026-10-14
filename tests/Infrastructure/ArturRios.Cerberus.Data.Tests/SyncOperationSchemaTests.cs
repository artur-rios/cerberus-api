using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class SyncOperationSchemaTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenOperationMigration_WhenRollingBackAndReapplying_ThenPreserveExistingDataAndHaveNoModelDrift()
    {
        await using var db = fixture.CreateContext(); var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid() };
        db.Accounts.Add(account); await db.SaveChangesAsync(); var before = db.Database.GetMigrations().Last(x => string.CompareOrdinal(x, "20261014043823") < 0);
        var migrator = db.GetService<IMigrator>();
        try
        {
            await migrator.MigrateAsync(before);
            Assert.Equal(0, await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM pg_tables WHERE schemaname='cerberus' AND tablename='sync_operation'").SingleAsync());
            Assert.True(await db.Accounts.AnyAsync(x => x.Id == account.Id));
        }
        finally { await migrator.MigrateAsync(); }
        Assert.Equal(1, await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM pg_tables WHERE schemaname='cerberus' AND tablename='sync_operation'").SingleAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
    [FunctionalFact]
    public async Task GivenStableOperationId_WhenPersistingForSameAccount_ThenRequireUniqueBindingAndCascadeOnAccountErase()
    {
        await using var db = fixture.CreateContext(); var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid() };
        db.Accounts.Add(account); await db.SaveChangesAsync(); var id = Guid.NewGuid(); var resource = Guid.NewGuid();
        SyncOperation Row() => new() { AccountId = account.Id, OperationId = id, RequestFingerprint = new byte[32], ResourceKind = "record",
            ResourceId = resource, Action = "update", Status = "denied", CreatedAt = DateTimeOffset.UtcNow };
        db.SyncOperations.Add(Row()); await db.SaveChangesAsync();
        db.SyncOperations.Add(Row()); await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync()); db.ChangeTracker.Clear();
        await db.Accounts.Where(x => x.Id == account.Id).ExecuteDeleteAsync(); Assert.False(await db.SyncOperations.AnyAsync(x => x.AccountId == account.Id));
    }
    [FunctionalTheory]
    [InlineData("hash")][InlineData("metadata")][InlineData("outcome")]
    public async Task GivenMalformedOperationMetadata_WhenPersisting_ThenRejectAtDatabaseBoundary(string invalid)
    {
        await using var db = fixture.CreateContext(); var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid() };
        db.Accounts.Add(account); await db.SaveChangesAsync();
        db.SyncOperations.Add(new() { AccountId = account.Id, OperationId = Guid.NewGuid(), ResourceKind = "record", ResourceId = Guid.NewGuid(),
            Action = "update", RequestFingerprint = invalid == "hash" ? [] : new byte[32],
            Status = invalid == "metadata" ? "applied" : invalid == "outcome" ? "unknown" : "denied", CreatedAt = DateTimeOffset.UtcNow });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
