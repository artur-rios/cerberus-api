using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Sync;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class SyncSchemaTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenSyncMigrations_WhenRollingBackThenReapplying_ThenPreserveExistingAccountSchema()
    {
        await using var db = fixture.CreateContext();
        var a = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid() };
        db.Accounts.Add(a); await db.SaveChangesAsync();
        var beforeSync = db.Database.GetMigrations().Last(x => string.CompareOrdinal(x, "20261014020134") < 0);
        var migrator = db.GetService<IMigrator>();
        try
        {
            await migrator.MigrateAsync(beforeSync);
            Assert.Equal(0, await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM pg_tables WHERE schemaname='cerberus' AND tablename IN('sync_stream','sync_inventory','sync_change')").SingleAsync());
            Assert.True(await db.Accounts.AnyAsync(x => x.Id == a.Id));
        }
        finally { await migrator.MigrateAsync(); }
        Assert.Equal(3, await db.Database.SqlQuery<long>($"SELECT count(*) AS \"Value\" FROM pg_tables WHERE schemaname='cerberus' AND tablename IN('sync_stream','sync_inventory','sync_change')").SingleAsync());
        Assert.False(db.Database.HasPendingModelChanges());
    }
    [FunctionalFact]
    public async Task GivenScopedSyncStream_WhenDeletingItsProfile_ThenCascadeOnlyItsMinimalHistory()
    {
        await using var db = fixture.CreateContext();
        var a = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid() };
        db.Accounts.Add(a); await db.SaveChangesAsync();
        var p = new Profile { AccountId = a.Id, PublicId = Guid.NewGuid() };
        db.Profiles.Add(p); await db.SaveChangesAsync();
        var stream = new SyncStream { PublicId = Guid.NewGuid(), AccountId = a.Id, ProfileId = p.Id,
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30), PageSize = 10 };
        db.SyncStreams.Add(stream); await db.SaveChangesAsync();
        var id = Guid.NewGuid();
        var change = new SyncChange { StreamId = stream.Id, WindowVersion = 1, ResourceKind = "record", ResourceId = id, EventType = "upsert" };
        db.SyncChanges.Add(change);
        db.SyncInventories.Add(new SyncInventory { StreamId = stream.Id, ResourceKind = "record", ResourceId = id, Fingerprint = new byte[32] });
        await db.SaveChangesAsync();
        Assert.True(change.Sequence > 0);
        await db.Profiles.Where(x => x.Id == p.Id).ExecuteDeleteAsync();
        Assert.False(await db.SyncStreams.AnyAsync(x => x.Id == stream.Id));
        Assert.False(await db.SyncChanges.AnyAsync(x => x.StreamId == stream.Id));
        Assert.False(await db.SyncInventories.AnyAsync(x => x.StreamId == stream.Id));
        Assert.True(await db.Accounts.AnyAsync(x => x.Id == a.Id));
    }
    [FunctionalFact]
    public async Task GivenForeignProfile_WhenPersistingStream_ThenRejectCrossAccountScope()
    {
        await using var db = fixture.CreateContext();
        var own = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid() };
        var other = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid() };
        db.Accounts.AddRange(own, other); await db.SaveChangesAsync();
        var p = new Profile { PublicId = Guid.NewGuid(), AccountId = other.Id };
        db.Profiles.Add(p); await db.SaveChangesAsync();
        db.SyncStreams.Add(new SyncStream { PublicId = Guid.NewGuid(), AccountId = own.Id, ProfileId = p.Id,
            CreatedAt = DateTimeOffset.UtcNow, ExpiresAt = DateTimeOffset.UtcNow.AddDays(30), PageSize = 10 });
        await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
    }
}
