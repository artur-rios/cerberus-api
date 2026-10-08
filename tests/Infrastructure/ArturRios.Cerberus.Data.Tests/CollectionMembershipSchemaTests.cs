using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class CollectionMembershipSchemaTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenTypedEqualPublicIds_WhenIncludingInMultipleCollections_ThenKeepIndependentResourcesAndMemberships()
    {
        using var owner = new ProtectionFixture();
        var state = await ProfileSetup.Create(fixture, owner);
        var items = await AssociationSetup.Items(fixture, state, Guid.NewGuid());
        var another = await AssociationSetup.Items(fixture, state);
        await using var db = fixture.CreateContext();
        var before = await Content(db, state.InternalId);
        foreach (var collection in new[] { items.Collection, another.Collection })
        {
            db.CollectionRecords.Add(new() { AccountId = state.InternalId, CollectionId = collection.Id, RecordId = items.Record.Id });
            db.CollectionFolders.Add(new() { AccountId = state.InternalId, CollectionId = collection.Id, FolderId = items.Folder.Id });
        }
        await db.SaveChangesAsync();
        Assert.Equal(2, await db.CollectionRecords.CountAsync(x => x.RecordId == items.Record.Id));
        Assert.Equal(2, await db.CollectionFolders.CountAsync(x => x.FolderId == items.Folder.Id));
        Assert.Equal(items.Record.PublicId, items.Folder.PublicId);
        Assert.Equal(items.Record.PublicId, items.Collection.PublicId);
        Assert.Equal(before, await Content(db, state.InternalId));
    }

    [FunctionalTheory]
    [InlineData("record", "target")][InlineData("folder", "target")]
    [InlineData("record", "collection")][InlineData("folder", "collection")]
    [InlineData("record", "marker")][InlineData("folder", "marker")]
    public async Task GivenCrossOwnerOrForgedMembership_WhenSaving_ThenDatabaseRejectsOwnershipEscape(string kind, string invalid)
    {
        using var owner = new ProtectionFixture();
        var state = await ProfileSetup.Create(fixture, owner);
        var foreign = await ProfileSetup.Create(fixture, owner);
        var own = await AssociationSetup.Items(fixture, state);
        var other = await AssociationSetup.Items(fixture, foreign);
        await using var db = fixture.CreateContext();
        var before = await Content(db, state.InternalId);
        var account = invalid == "marker" ? foreign.InternalId : state.InternalId;
        var collection = invalid == "collection" ? other.Collection.Id : own.Collection.Id;
        if (kind == "record")
            db.CollectionRecords.Add(new() { AccountId = account, CollectionId = collection, RecordId = invalid == "target" ? other.Record.Id : own.Record.Id });
        else
            db.CollectionFolders.Add(new() { AccountId = account, CollectionId = collection, FolderId = invalid == "target" ? other.Folder.Id : own.Folder.Id });
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        Assert.Equal(before, await Content(db, state.InternalId));
        Assert.False(await db.CollectionRecords.AnyAsync(x => x.CollectionId == collection));
        Assert.False(await db.CollectionFolders.AnyAsync(x => x.CollectionId == collection));
    }

    [FunctionalTheory][InlineData("record")][InlineData("folder")]
    public async Task GivenExistingTypedMembership_WhenSavingDuplicate_ThenDatabaseRejectsDuplicateWithoutChangingContent(string kind)
    {
        using var owner = new ProtectionFixture();
        var state = await ProfileSetup.Create(fixture, owner);
        var items = await AssociationSetup.Items(fixture, state);
        await using var db = fixture.CreateContext();
        var before = await Content(db, state.InternalId);
        void Add()
        {
            if (kind == "record") db.CollectionRecords.Add(new() { AccountId = state.InternalId, CollectionId = items.Collection.Id, RecordId = items.Record.Id });
            else db.CollectionFolders.Add(new() { AccountId = state.InternalId, CollectionId = items.Collection.Id, FolderId = items.Folder.Id });
        }
        Add(); await db.SaveChangesAsync(); db.ChangeTracker.Clear(); Add();
        var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        Assert.Equal(PostgresErrorCodes.UniqueViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
        Assert.Equal(before, await Content(db, state.InternalId));
        Assert.Equal(kind == "record" ? 1 : 0, await db.CollectionRecords.CountAsync(x => x.CollectionId == items.Collection.Id));
        Assert.Equal(kind == "folder" ? 1 : 0, await db.CollectionFolders.CountAsync(x => x.CollectionId == items.Collection.Id));
    }

    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")]
    public async Task GivenTypedMemberships_WhenDeletingOneResource_ThenCascadeOnlyItsLinksAndPreserveOtherKinds(string kind)
    {
        using var owner = new ProtectionFixture();
        var state = await ProfileSetup.Create(fixture, owner);
        var items = await AssociationSetup.Items(fixture, state, Guid.NewGuid());
        var other = await AssociationSetup.Items(fixture, state);
        await using var db = fixture.CreateContext();
        foreach (var collection in new[] { items.Collection, other.Collection })
        {
            db.CollectionRecords.Add(new() { AccountId = state.InternalId, CollectionId = collection.Id, RecordId = items.Record.Id });
            db.CollectionFolders.Add(new() { AccountId = state.InternalId, CollectionId = collection.Id, FolderId = items.Folder.Id });
        }
        await db.SaveChangesAsync();
        var otherBefore = JsonSerializer.Serialize(new { other.Record, other.Folder, other.Collection });
        if (kind == "record") await db.Records.Where(x => x.Id == items.Record.Id).ExecuteDeleteAsync();
        if (kind == "folder") await db.Folders.Where(x => x.Id == items.Folder.Id).ExecuteDeleteAsync();
        if (kind == "collection") await db.Collections.Where(x => x.Id == items.Collection.Id).ExecuteDeleteAsync();
        Assert.Equal(kind != "record", await db.Records.AnyAsync(x => x.Id == items.Record.Id));
        Assert.Equal(kind != "folder", await db.Folders.AnyAsync(x => x.Id == items.Folder.Id));
        Assert.Equal(kind != "collection", await db.Collections.AnyAsync(x => x.Id == items.Collection.Id));
        Assert.Equal(kind == "record" ? 0 : kind == "collection" ? 1 : 2, await db.CollectionRecords.CountAsync(x => x.RecordId == items.Record.Id));
        Assert.Equal(kind == "folder" ? 0 : kind == "collection" ? 1 : 2, await db.CollectionFolders.CountAsync(x => x.FolderId == items.Folder.Id));
        Assert.Equal(otherBefore, JsonSerializer.Serialize(new
        {
            Record = await db.Records.AsNoTracking().SingleAsync(x => x.Id == other.Record.Id),
            Folder = await db.Folders.AsNoTracking().SingleAsync(x => x.Id == other.Folder.Id),
            Collection = await db.Collections.AsNoTracking().SingleAsync(x => x.Id == other.Collection.Id)
        }));
    }

    [FunctionalFact]
    public async Task GivenPreviousMigrationWithNativeContent_WhenUpgrading_ThenPreserveRowsAndEnforceNewMemberships()
    {
        // This database belongs only to this test; the shared fixture database is untouched.
        await using var fixtureContext = fixture.CreateContext();
        var connection = new NpgsqlConnectionStringBuilder(fixtureContext.Database.GetConnectionString());
        await using var admin = new NpgsqlConnection(connection.ConnectionString);
        await admin.OpenAsync();
        var name = "membership_upgrade_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", admin)) await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = name; connection.Pooling = false;
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection.ConnectionString).Options);
            await db.Database.GetService<IMigrator>().MigrateAsync("20261008195528_ProfileAssociations");
            using var owner = new ProtectionFixture();
            var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid(), DetailsEnvelope = JsonSerializer.SerializeToUtf8Bytes(ProfileSetup.Envelope(), ProtectionFixture.Json) };
            db.Accounts.Add(account); await db.SaveChangesAsync();
            var bytes = JsonSerializer.SerializeToUtf8Bytes(ProfileSetup.Envelope(), ProtectionFixture.Json);
            var id = Guid.NewGuid();
            var record = new VaultRecord { AccountId = account.Id, PublicId = id, Envelope = bytes, EditedAt = DateTimeOffset.UnixEpoch };
            var folder = new VaultFolder { AccountId = account.Id, PublicId = id, Envelope = bytes, EditedAt = DateTimeOffset.UnixEpoch };
            var collection = new VaultCollection { AccountId = account.Id, PublicId = id, Envelope = bytes, EditedAt = DateTimeOffset.UnixEpoch };
            db.Records.Add(record); db.Folders.Add(folder); db.Collections.Add(collection); await db.SaveChangesAsync();
            var before = await Content(db, account.Id);
            Assert.DoesNotContain("CollectionMembership", string.Join(',', await db.Database.GetAppliedMigrationsAsync()));
            await db.Database.MigrateAsync();
            Assert.Contains("CollectionMembership", string.Join(',', await db.Database.GetAppliedMigrationsAsync()));
            Assert.Equal(before, await Content(db, account.Id));
            db.CollectionRecords.Add(new() { AccountId = account.Id, CollectionId = collection.Id, RecordId = record.Id });
            db.CollectionFolders.Add(new() { AccountId = account.Id, CollectionId = collection.Id, FolderId = folder.Id });
            await db.SaveChangesAsync();
            Assert.True(await db.CollectionRecords.AnyAsync(x => x.CollectionId == collection.Id && x.RecordId == record.Id));
            Assert.True(await db.CollectionFolders.AnyAsync(x => x.CollectionId == collection.Id && x.FolderId == folder.Id));
            Assert.Equal(before, await Content(db, account.Id));
            var foreign = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid(), DetailsEnvelope = bytes };
            db.Accounts.Add(foreign); await db.SaveChangesAsync();
            var foreignRecord = new VaultRecord { AccountId = foreign.Id, PublicId = Guid.NewGuid(), Envelope = bytes, EditedAt = DateTimeOffset.UnixEpoch };
            db.Records.Add(foreignRecord); await db.SaveChangesAsync();
            db.CollectionRecords.Add(new() { AccountId = account.Id, CollectionId = collection.Id, RecordId = foreignRecord.Id });
            var error = await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
            Assert.Equal(PostgresErrorCodes.ForeignKeyViolation, Assert.IsType<PostgresException>(error.InnerException).SqlState);
            Assert.Equal(before, await Content(db, account.Id));
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }

    private static async Task<string> Content(AppDbContext db, long account) => JsonSerializer.Serialize(new
    {
        Account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == account),
        Records = await db.Records.AsNoTracking().Where(x => x.AccountId == account).OrderBy(x => x.Id).ToArrayAsync(),
        Folders = await db.Folders.AsNoTracking().Where(x => x.AccountId == account).OrderBy(x => x.Id).ToArrayAsync(),
        Collections = await db.Collections.AsNoTracking().Where(x => x.AccountId == account).OrderBy(x => x.Id).ToArrayAsync()
    });
}
