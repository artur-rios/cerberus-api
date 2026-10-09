using System.Text.Json;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Records;
using Microsoft.EntityFrameworkCore;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Npgsql;
using Testcontainers.PostgreSql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public sealed class TypedTerminalErasureTests(PostgresFixture fixture) : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("cerberus-typed-ledger-").FullName;
    private static readonly DateTimeOffset Deleted = DateTimeOffset.Parse("2026-10-07T00:00:00Z");

    // A global GUID index or lookup falsely merges independent terminal identities.
    [FunctionalFact]
    public async Task GivenSameGuidAcrossKinds_WhenReapplyingErasures_ThenRetainBothIndependentTerminalEvents()
    {
        var id = Guid.NewGuid();
        var store = new TerminalErasureStore(fixture);
        await store.ReapplyAsync(new(id, "record", Deleted), default);
        await store.ReapplyAsync(new(id, "folder", Deleted.AddHours(1)), default);
        await using var db = fixture.CreateContext();
        var entries = await db.TerminalErasures.Where(x => x.ResourceId == id).OrderBy(x => x.ResourceKind).ToListAsync();
        Assert.Equal(new[] { "folder", "record" }, entries.Select(x => x.ResourceKind));
        Assert.Equal(Deleted.AddHours(1), entries[0].DeletedAt);
        Assert.Equal(Deleted, entries[1].DeletedAt);
    }

    // New erasures must coexist with the legacy filename namespace without overwriting it.
    [FunctionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenSameGuidTwoKinds_WhenRecordingAndReopeningLedger_ThenPreserveBothKinds(bool legacy)
    {
        var id = Guid.NewGuid();
        var first = new ErasureEntry(id, "record", Deleted);
        var ledger = new FileErasureLedger(directory);
        if (legacy) await File.WriteAllTextAsync(Path.Combine(directory, id.ToString("N") + ".json"), JsonSerializer.Serialize(first));
        else await ledger.RecordAsync(first, default);
        await ledger.RecordAsync(new(id, "folder", Deleted.AddHours(1)), default);
        var entries = new List<ErasureEntry>();
        await foreach (var entry in new FileErasureLedger(directory).ReadAsync(default)) entries.Add(entry);
        Assert.Equal(2, entries.Count);
        Assert.Contains(first, entries);
        Assert.Contains(new(id, "folder", Deleted.AddHours(1)), entries);
        Assert.True(File.Exists(Path.Combine(directory, "folder-" + id.ToString("N") + ".json")));
        if (legacy) Assert.Equal(JsonSerializer.Serialize(first), await File.ReadAllTextAsync(Path.Combine(directory, id.ToString("N") + ".json")));
    }

    [FunctionalFact]
    public async Task GivenLegacySameKind_WhenRetryingDeletion_ThenKeepOriginalTimeAndReplayIt()
    {
        var id = Guid.NewGuid();
        var entry = new ErasureEntry(id, "record", Deleted);
        await File.WriteAllTextAsync(Path.Combine(directory, id.ToString("N") + ".json"), JsonSerializer.Serialize(entry));
        var ledger = new FileErasureLedger(directory);
        await ledger.RecordAsync(entry with { DeletedAt = Deleted.AddHours(1) }, default);
        var entries = new List<ErasureEntry>();
        await foreach (var item in ledger.ReadAsync(default)) entries.Add(item);
        Assert.Single(entries);
        Assert.Equal(entry, entries[0]);
    }

    // Unrelated terminal kinds must never erase the actor account's namespace.
    [FunctionalTheory]
    [InlineData("profile")][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("grant")]
    public async Task GivenOtherKindErasedWithAccountGuid_WhenReadingAccount_ThenReturnOwnedCiphertext(string kind)
    {
        using var client = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, client);
        await Erase(s.AccountId, kind);
        var result = await new AccountReadStore(fixture).ReadAsync(s.Actor, s.Verifier, DateTimeOffset.UtcNow, default);
        Assert.Null(result.Error);
        Assert.Equal(s.AccountId, result.Account!.Id);
        Assert.Equal(s.AccountEnvelope, result.Account.DetailsEnvelope);
    }

    [FunctionalTheory]
    [InlineData("account")][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("grant")]
    public async Task GivenOtherKindErasedWithProfileGuid_WhenReadingProfile_ThenKeepVisibleOwnedProfile(string kind)
    {
        using var client = new ProtectionFixture();
        using var scoped = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, client);
        var input = ProfileSetup.Input(s, client, scoped);
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor, s.Verifier, input), default)).Error);
        await Erase(input.ProfileId, kind);
        var result = await new ProfileReadStore(fixture).ReadAsync(new(s.Actor, s.Verifier, input.ProfileId), default);
        Assert.Null(result.Error);
        Assert.NotNull(result.Data);
    }

    [FunctionalTheory]
    [InlineData("account")][InlineData("profile")][InlineData("folder")][InlineData("collection")][InlineData("grant")]
    public async Task GivenOtherKindErasedWithRecordGuid_WhenReadingAndMovingRecord_ThenKeepItsOwnIdentityActive(string kind)
    {
        using var client = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, client);
        var items = await AssociationSetup.Items(fixture, s);
        await Erase(items.Record.PublicId, kind);
        var read = await new RecordReadStore(fixture).ReadAsync(new(s.Actor, s.Verifier, items.Record.PublicId), default);
        Assert.Null(read.Error);
        Assert.Equal(items.Record.PublicId, read.Data!.Record.RecordId);
        var moved = await new RecordMoveStore(fixture).MoveAsync(new(s.Actor, s.Verifier, items.Record.PublicId, 1, items.Folder.PublicId), default);
        Assert.Null(moved.Error);
        Assert.Equal(items.Folder.PublicId, moved.Data!.FolderId);
        await using var db = fixture.CreateContext();
        Assert.Equal(items.Folder.Id, (await db.Records.SingleAsync(x => x.Id == items.Record.Id)).FolderId);
    }

    [FunctionalTheory]
    [InlineData("account")][InlineData("profile")][InlineData("folder")][InlineData("collection")][InlineData("grant")]
    public async Task GivenOtherKindTerminalGuid_WhenCreatingRecord_ThenDoNotReserveItsRecordIdentity(string kind)
    {
        using var client = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, client);
        var id = Guid.NewGuid();
        await Erase(id, kind);
        var result = await new RecordCreateStore(fixture).CreateAsync(new(s.Actor, s.Verifier,
            new(id, ProfileSetup.Envelope(), Deleted, [], null)), default);
        Assert.Null(result.Error);
        Assert.Equal(id, result.Data!.RecordId);
        await using var db = fixture.CreateContext();
        Assert.True(await db.TerminalErasures.AnyAsync(x => x.ResourceKind == kind && x.ResourceId == id));
        Assert.True(await db.Records.AnyAsync(x => x.PublicId == id));
    }

    [FunctionalFact]
    public async Task GivenRecordTerminalGuid_WhenCreatingOrReadingRecord_ThenContinueDenyingThatExactKind()
    {
        using var client = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, client);
        var id = Guid.NewGuid();
        await Erase(id, "record");
        Assert.Equal("revision_conflict", (await new RecordCreateStore(fixture).CreateAsync(new(s.Actor, s.Verifier,
            new(id, ProfileSetup.Envelope(), Deleted, [], null)), default)).Error);
        Assert.Equal("not_found", (await new RecordReadStore(fixture).ReadAsync(new(s.Actor, s.Verifier, id), default)).Error);
        await using var db = fixture.CreateContext();
        Assert.False(await db.Records.AnyAsync(x => x.PublicId == id));
    }

    [FunctionalFact]
    public async Task GivenErasedRecordGuid_WhenLookingUpOtherKinds_ThenRequireExactTypedIdentity()
    {
        var id = Guid.NewGuid();
        var store = new TerminalErasureStore(fixture);
        await store.ReapplyAsync(new(id, "record", Deleted), default);
        Assert.True(await store.IsErasedAsync("record", id, default));
        Assert.False(await store.IsErasedAsync("folder", id, default));
        Assert.False(await store.IsErasedAsync("account", id, default));
    }

    [FunctionalTheory]
    [InlineData("unknown", false)][InlineData("Record", false)][InlineData("", false)][InlineData("record", true)]
    public async Task GivenInvalidTerminalIdentity_WhenPersistingOrLookingUp_ThenRejectBeforeDatabase(string kind, bool empty)
    {
        var id = empty ? Guid.Empty : Guid.NewGuid();
        var store = new TerminalErasureStore(fixture);
        await Assert.ThrowsAsync<InvalidDataException>(() => store.ReapplyAsync(new(id, kind, Deleted), default));
        await Assert.ThrowsAsync<InvalidDataException>(() => store.IsErasedAsync(kind, id, default));
        await using var db = fixture.CreateContext();
        Assert.False(await db.TerminalErasures.AnyAsync(x => x.ResourceId == id));
    }

    [FunctionalFact]
    public async Task GivenRepeatedSameKindEvent_WhenReplayingConcurrently_ThenKeepEarliestDeletionTime()
    {
        var id = Guid.NewGuid();
        var store = new TerminalErasureStore(fixture);
        await store.ReapplyAsync(new(id, "record", Deleted.AddHours(1)), default);
        await Task.WhenAll(Enumerable.Range(0, 4).Select(i => store.ReapplyAsync(new(id, "record", Deleted.AddMinutes(i)), default)));
        await using var db = fixture.CreateContext();
        var entry = Assert.Single(await db.TerminalErasures.Where(x => x.ResourceId == id).ToListAsync());
        Assert.Equal(Deleted, entry.DeletedAt);
    }

    [FunctionalTheory]
    [InlineData("owner")][InlineData("collection")][InlineData("grant")][InlineData("folder")][InlineData("record")]
    public async Task GivenUnrelatedTypedErasureOfNativeRouteGuid_WhenReadingSharedRecord_ThenPreserveCurrentNativeVisibility(string target)
    {
        using var owner = new ProtectionFixture();
        using var recipient = new ProtectionFixture();
        var o = await ProfileSetup.Create(fixture, owner);
        var r = await ProfileSetup.Create(fixture, recipient);
        var items = await AssociationSetup.Items(fixture, o);
        var grant = await AssociationSetup.Grant(fixture, o, owner, r, recipient, items.Collection);
        await using (var db = fixture.CreateContext())
        {
            await db.Records.Where(x => x.Id == items.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.FolderId, items.Folder.Id));
            db.CollectionFolders.Add(new() { AccountId = o.InternalId, CollectionId = items.Collection.Id, FolderId = items.Folder.Id });
            await db.SaveChangesAsync();
        }
        var id = target switch { "owner" => o.AccountId, "collection" => items.Collection.PublicId, "grant" => grant.PublicId, "folder" => items.Folder.PublicId, _ => items.Record.PublicId };
        await Erase(id, target == "record" ? "folder" : "record");
        var read = await new RecordReadStore(fixture).ReadAsync(new(r.Actor, r.Verifier, items.Record.PublicId), default);
        Assert.Null(read.Error);
        Assert.Equal(items.Record.PublicId, read.Data!.Record.RecordId);
        await using var check = fixture.CreateContext();
        Assert.Equal(CollectionGrantState.Active, (await check.CollectionGrants.SingleAsync(x => x.Id == grant.Id)).State);
    }

    [FunctionalTheory]
    [InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("grant")][InlineData("account")]
    public async Task GivenOtherKindErasedSelectionGuid_WhenReadingSelectedProfile_ThenKeepSelectionNonnullAndValid(string kind)
    {
        using var owner = new ProtectionFixture();
        using var scoped = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, owner);
        var input = ProfileSetup.Input(s, owner, scoped);
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor, s.Verifier, input), default)).Error);
        await using var db = fixture.CreateContext();
        var id = await db.Profiles.Where(x => x.PublicId == input.ProfileId).Select(x => x.Id).SingleAsync();
        await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ProfileId, id));
        await Erase(input.ProfileId, kind);
        Assert.Null((await new ProfileReadStore(fixture).ReadAsync(new(s.Actor, s.Verifier, input.ProfileId), default)).Error);
        Assert.Equal(id, await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).Select(x => x.ProfileId).SingleAsync());
    }

    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenLegacyDatabase_WhenUpgradingTypedIdentity_ThenPreserveValidRowsAndFailUnknownKindClosed(bool invalid)
    {
        await using var container = new PostgreSqlBuilder("postgres:18.6-alpine").Build();
        await container.StartAsync();
        await using var db = new AppDbContext(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(container.GetConnectionString()).Options);
        var migrator = db.GetService<IMigrator>();
        await migrator.MigrateAsync("20261008224646_CollectionMembership");
        var id = Guid.NewGuid();
        await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.terminal_erasure(resource_id,resource_kind,deleted_at) VALUES({id},{(invalid ? "unknown" : "record")},{Deleted})");
        if (invalid)
        {
            await Assert.ThrowsAsync<PostgresException>(() => migrator.MigrateAsync());
            var entry = await db.TerminalErasures.AsNoTracking().SingleAsync();
            Assert.Equal("unknown", entry.ResourceKind);
        }
        else
        {
            await migrator.MigrateAsync();
            await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.terminal_erasure(resource_id,resource_kind,deleted_at) VALUES({id},{"folder"},{Deleted})");
            Assert.Equal(2, await db.TerminalErasures.CountAsync(x => x.ResourceId == id));
            Assert.Equal(Deleted, (await db.TerminalErasures.SingleAsync(x => x.ResourceKind == "record")).DeletedAt);
            await Assert.ThrowsAsync<PostgresException>(() => db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.terminal_erasure(resource_id,resource_kind,deleted_at) VALUES({Guid.NewGuid()},{"unknown"},{Deleted})"));
        }
    }

    [FunctionalTheory]
    [InlineData("wrongKind")][InlineData("wrongId")][InlineData("unknownKind")][InlineData("symlink")]
    public async Task GivenMalformedTypedLedgerFile_WhenReading_ThenFailClosed(string kind)
    {
        var id = Guid.NewGuid();
        var entry = new ErasureEntry(kind == "wrongId" ? Guid.NewGuid() : id, kind == "wrongKind" ? "folder" : kind == "unknownKind" ? "unknown" : "record", Deleted);
        var file = Path.Combine(directory, "record-" + id.ToString("N") + ".json");
        if (kind == "symlink")
        {
            var real = Path.Combine(directory, "source.tmp");
            await File.WriteAllTextAsync(real, JsonSerializer.Serialize(entry));
            File.CreateSymbolicLink(file, real);
        }
        else await File.WriteAllTextAsync(file, JsonSerializer.Serialize(entry));
        await Assert.ThrowsAsync<InvalidDataException>(async () => { await foreach (var _ in new FileErasureLedger(directory).ReadAsync(default)) { } });
    }

    private async Task Erase(Guid id, string kind)
    {
        await using var db = fixture.CreateContext();
        db.TerminalErasures.Add(new() { ResourceId = id, ResourceKind = kind, DeletedAt = Deleted });
        await db.SaveChangesAsync();
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
