using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

public sealed class SoftwareGrantModelTests
{
    private static AppDbContext Context() => new(new DbContextOptionsBuilder<AppDbContext>()
        .UseNpgsql("Host=localhost;Database=cerberus;Username=postgres;Password=postgres").Options);

    [UnitFact]
    public void GivenSoftwareGrant_WhenMapping_ThenExposeOwnerApplicationLookupAndPublicIdentity()
    {
        using var db = Context();
        Assert.NotNull(db.SoftwareGrants);
        var grant = db.Model.FindEntityType(typeof(SoftwareGrant))!;
        Assert.Equal("software_grant", grant.GetTableName());
        Assert.Equal("cerberus", grant.GetSchema());
        Assert.Equal(["Id"], grant.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Contains(grant.GetKeys(), x => x.Properties.Select(p => p.Name).SequenceEqual(["AccountId", "Id"]));
        Assert.True(grant.FindIndex(grant.FindProperty(nameof(SoftwareGrant.PublicId))!)!.IsUnique);
        var lookup = Assert.Single(grant.GetIndexes(), x => x.Properties.Select(p => p.Name)
            .SequenceEqual(["AccountId", "HeimdallSoftwarePublicId"]));
        Assert.False(lookup.IsUnique);
        Assert.True(grant.FindProperty(nameof(SoftwareGrant.Revision))!.IsConcurrencyToken);
        Assert.Equal(typeof(SoftwareGrantState), grant.FindProperty(nameof(SoftwareGrant.State))!.ClrType);
        var owner = Assert.Single(grant.GetForeignKeys());
        Assert.Equal(typeof(Account), owner.PrincipalEntityType.ClrType);
        Assert.Equal(["AccountId"], owner.Properties.Select(x => x.Name));
        Assert.Equal(DeleteBehavior.Cascade, owner.DeleteBehavior);
    }

    [UnitTheory]
    [InlineData(typeof(SoftwareGrant), "software_grant", "RecipientKey", "recipient_key")]
    [InlineData(typeof(SoftwareGrantRecord), "software_grant_record", "RecipientKeyEnvelope", "recipient_key_envelope")]
    [InlineData(typeof(SoftwareGrantCollection), "software_grant_collection", "RecipientKeyEnvelope", "recipient_key_envelope")]
    public void GivenGrantMaterial_WhenMapping_ThenUseRequiredBinaryColumns(Type type, string table, string property, string column)
    {
        using var db = Context();
        var entity = db.Model.FindEntityType(type)!;
        Assert.Equal(table, entity.GetTableName());
        Assert.Equal("cerberus", entity.GetSchema());
        var material = entity.FindProperty(property)!;
        Assert.Equal(typeof(byte[]), material.ClrType);
        Assert.False(material.IsNullable);
        Assert.Equal("bytea", material.GetColumnType());
        Assert.Equal(column, material.GetColumnName(StoreObjectIdentifier.Table(table, "cerberus")));
        foreach (var field in entity.GetProperties())
            Assert.False(field.IsNullable);
    }

    [UnitTheory]
    [InlineData(typeof(SoftwareGrantRecord), typeof(VaultRecord), "RecordId", "record_id")]
    [InlineData(typeof(SoftwareGrantCollection), typeof(VaultCollection), "CollectionId", "collection_id")]
    public void GivenTypedGrantAssociation_WhenMapping_ThenRequireSameOwnerForGrantAndTarget(Type type, Type targetType, string targetId, string targetColumn)
    {
        using var db = Context();
        Assert.NotNull(db.SoftwareGrantRecords);
        Assert.NotNull(db.SoftwareGrantCollections);
        var entity = db.Model.FindEntityType(type)!;
        Assert.Equal(["GrantId", targetId], entity.FindPrimaryKey()!.Properties.Select(x => x.Name));
        Assert.Equal(targetColumn, entity.FindProperty(targetId)!.GetColumnName());
        Assert.Equal("account_id", entity.FindProperty("AccountId")!.GetColumnName());
        Assert.Equal("grant_id", entity.FindProperty("GrantId")!.GetColumnName());
        Assert.Equal(2, entity.GetForeignKeys().Count());
        var grant = Assert.Single(entity.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == typeof(SoftwareGrant));
        Assert.Equal(["AccountId", "GrantId"], grant.Properties.Select(x => x.Name));
        Assert.Equal(["AccountId", "Id"], grant.PrincipalKey.Properties.Select(x => x.Name));
        var target = Assert.Single(entity.GetForeignKeys(), x => x.PrincipalEntityType.ClrType == targetType);
        Assert.Equal(["AccountId", targetId], target.Properties.Select(x => x.Name));
        Assert.Equal(["AccountId", "Id"], target.PrincipalKey.Properties.Select(x => x.Name));
        Assert.All(entity.GetForeignKeys(), x => Assert.Equal(DeleteBehavior.Cascade, x.DeleteBehavior));
        Assert.Contains(entity.GetIndexes(), x => x.Properties.Select(p => p.Name).SequenceEqual(["AccountId", targetId]));
        Assert.Contains(entity.GetIndexes(), x => x.Properties.Select(p => p.Name).SequenceEqual(["AccountId", "GrantId"]));
    }

    [UnitFact]
    public void GivenSoftwareGrant_WhenMapping_ThenUseSnakeCaseGrantColumns()
    {
        using var db = Context();
        var grant = db.Model.FindEntityType(typeof(SoftwareGrant))!;
        Assert.Equal("public_id", grant.FindProperty(nameof(SoftwareGrant.PublicId))!.GetColumnName());
        Assert.Equal("account_id", grant.FindProperty(nameof(SoftwareGrant.AccountId))!.GetColumnName());
        Assert.Equal("heimdall_software_public_id", grant.FindProperty(nameof(SoftwareGrant.HeimdallSoftwarePublicId))!.GetColumnName());
        Assert.Equal("revision", grant.FindProperty(nameof(SoftwareGrant.Revision))!.GetColumnName());
        Assert.Equal("state", grant.FindProperty(nameof(SoftwareGrant.State))!.GetColumnName());
    }
}

[Collection("PostgreSQL")]
public sealed class SoftwareGrantMigrationTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenCleanDatabase_WhenMigratingSoftwareGrantsUpDownUp_ThenPreserveExistingRowsAndRecreateGrantTables()
    {
        await using var fixtureContext = fixture.CreateContext();
        var connection = new NpgsqlConnectionStringBuilder(fixtureContext.Database.GetConnectionString());
        await using var admin = new NpgsqlConnection(connection.ConnectionString);
        await admin.OpenAsync();
        var database = "software_grants_" + Guid.NewGuid().ToString("N");
        await using (var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", admin))
            await create.ExecuteNonQueryAsync();
        try
        {
            connection.Database = database;
            connection.Pooling = false;
            await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(connection.ConnectionString).Options);
            var migrator = db.Database.GetService<IMigrator>();
            const string previous = "20261009035540_TypedTerminalErasure";
            await migrator.MigrateAsync(previous);
            var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid(), DetailsEnvelope = [1, 2, 3] };
            db.Accounts.Add(account);
            await db.SaveChangesAsync();
            await db.Database.MigrateAsync();
            var grant = new SoftwareGrant { PublicId = Guid.NewGuid(), AccountId = account.Id, HeimdallSoftwarePublicId = Guid.NewGuid(), RecipientKey = [4, 5, 6] };
            var record = new VaultRecord { PublicId = Guid.NewGuid(), AccountId = account.Id, Envelope = [7], EditedAt = DateTimeOffset.UnixEpoch };
            var collection = new VaultCollection { PublicId = Guid.NewGuid(), AccountId = account.Id, Envelope = [8], EditedAt = DateTimeOffset.UnixEpoch };
            db.AddRange(grant, record, collection);
            await db.SaveChangesAsync();
            db.SoftwareGrantRecords.Add(new() { AccountId = account.Id, GrantId = grant.Id, RecordId = record.Id, RecipientKeyEnvelope = [9] });
            db.SoftwareGrantCollections.Add(new() { AccountId = account.Id, GrantId = grant.Id, CollectionId = collection.Id, RecipientKeyEnvelope = [10] });
            await db.SaveChangesAsync();
            grant.State = SoftwareGrantState.Revoked;
            await db.SaveChangesAsync();
            Assert.Equal(1, await db.SoftwareGrantRecords.CountAsync());
            Assert.Equal(1, await db.SoftwareGrantCollections.CountAsync());
            await migrator.MigrateAsync(previous);
            await db.Database.MigrateAsync();
            db.ChangeTracker.Clear();
            Assert.Equal(new byte[] { 1, 2, 3 }, (await db.Accounts.SingleAsync()).DetailsEnvelope);
            Assert.Equal(new byte[] { 7 }, (await db.Records.SingleAsync()).Envelope);
            Assert.Equal(new byte[] { 8 }, (await db.Collections.SingleAsync()).Envelope);
            Assert.Empty(await db.SoftwareGrants.ToArrayAsync());
            Assert.Empty(await db.SoftwareGrantRecords.ToArrayAsync());
            Assert.Empty(await db.SoftwareGrantCollections.ToArrayAsync());
            Assert.EndsWith("_SoftwareGrants", (await db.Database.GetAppliedMigrationsAsync()).Last());
        }
        finally
        {
            await using var drop = new NpgsqlCommand($"DROP DATABASE \"{database}\" WITH (FORCE)", admin);
            await drop.ExecuteNonQueryAsync();
        }
    }
}
