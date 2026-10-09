using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public sealed partial class RecordPermanentDeleteStoreTests
{
    [FunctionalTheory]
    [InlineData("actor", "authentication_required")][InlineData("record", "validation_failed")]
    [InlineData("zero", "validation_failed")][InlineData("negative", "validation_failed")][InlineData("unsafe", "validation_failed")]
    public async Task GivenInvalidInternalRequest_WhenDeleting_ThenRejectWithoutDependencyOrIntent(string field, string error)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        var request = w.Request();
        request = field switch { "actor" => request with { Actor = Guid.Empty }, "record" => request with { RecordId = Guid.Empty }, "zero" => request with { ExpectedRevision = 0 }, "negative" => request with { ExpectedRevision = -1 }, _ => request with { ExpectedRevision = ArturRios.Cerberus.Domain.Protection.ProtocolBinary.MaxInteger + 1 } };
        var dead = new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=absent;Username=fixture;Password=fixture;Timeout=1").Options);
        Assert.Equal(error, (await Store(factory: dead).DeleteAsync(request, default)).Error);
    }

    [FunctionalFact]
    public async Task GivenUnavailableDatabase_WhenDeleting_Then503WithoutLedger()
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        var dead = new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=absent;Username=fixture;Password=fixture;Timeout=1").Options);
        Assert.Equal("persistence_unavailable", (await Store(factory: dead).DeleteAsync(w.Request(), default)).Error);
        Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
    }

    [FunctionalTheory]
    [InlineData("profile")][InlineData("collection")][InlineData("parent")]
    public async Task GivenNewUnheldEarlierAuthorityBeforeRecordLock_WhenDeleting_Then409WithoutLateLockOrIntent(string kind)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        var profile = await AssociationSetup.Profile(fixture, w.State, w.Owner);
        var hook = new BeforeTarget(async () =>
        {
            await using var db = fixture.CreateContext();
            if (kind == "profile") db.ProfileRecords.Add(new() { AccountId = w.State.InternalId, ProfileId = profile, RecordId = w.Record.Id });
            if (kind == "collection") db.CollectionRecords.Add(new() { AccountId = w.State.InternalId, CollectionId = w.Items.Collection.Id, RecordId = w.Record.Id });
            if (kind == "parent") await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.FolderId, w.Items.Folder.Id));
            await db.SaveChangesAsync();
        });
        Assert.Equal("revision_conflict", (await Store(factory: new ProfileSetup.Factory(fixture, hook)).DeleteAsync(w.Request(), default)).Error);
        await using var check = fixture.CreateContext();
        Assert.True(await check.Records.AnyAsync(x => x.Id == w.Record.Id && x.DeletedAt == null && x.Revision == 1));
        Assert.False(await check.TerminalErasures.AnyAsync(x => x.ResourceKind == "record" && x.ResourceId == w.Record.PublicId));
        Assert.False(await check.RetentionWorkItems.AnyAsync(x => x.OperationKey == "record-purge/" + w.Record.PublicId));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
    }

    [FunctionalTheory]
    [InlineData(CollectionGrantAccess.ReadOnly, false)][InlineData(CollectionGrantAccess.ReadWrite, false)]
    [InlineData(CollectionGrantAccess.ReadOnly, true)][InlineData(CollectionGrantAccess.ReadWrite, true)]
    public async Task GivenMalformedForeignNativeRoute_WhenDeleting_Then503OnlyIfCurrentTargetVisible(CollectionGrantAccess access, bool visible)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        using var client = new ProtectionFixture();
        var recipient = await ProfileSetup.Create(fixture, client);
        var grant = await AssociationSetup.Grant(fixture, w.State, w.Owner, recipient, client, w.Items.Collection, access);
        await using var db = fixture.CreateContext();
        await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.RecipientKeyEnvelope, "bad"u8.ToArray()));
        if (visible) { db.CollectionRecords.Add(new() { AccountId = w.State.InternalId, CollectionId = w.Items.Collection.Id, RecordId = w.Record.Id }); await db.SaveChangesAsync(); }
        var before = await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId);
        Assert.Equal(visible ? "persistence_unavailable" : "not_found", (await Store().DeleteAsync(new(recipient.Actor, recipient.Verifier, w.Record.PublicId, 99), default)).Error);
        Assert.Equal(before, await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
    }

    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenParentSequenceExhaustionOrRegression_WhenDeleting_ThenRollbackIntentAndParents(bool regress)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "folder", false);
        await using var db = fixture.CreateContext();
        var previous = await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();
        try
        {
            if (regress) await db.Folders.Where(x => x.Id == w.Items.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ServerSequence, previous + 100000));
            else await db.Database.ExecuteSqlRawAsync("SELECT setval('cerberus.server_sequence',9007199254740991,true)");
            var before = await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId);
            Assert.Equal(regress ? "persistence_unavailable" : "revision_conflict", (await Store().DeleteAsync(w.Request(), default)).Error);
            Assert.Equal(before, await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId));
            Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
        }
        finally { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{previous},true)"); }
    }

    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenCancellationAtLedgerBoundary_WhenDeleting_ThenKeepCommittedTerminalIntentAndOutbox(bool caller)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        using var ct = new CancellationTokenSource();
        var ledger = new PermanentDeleteSetup.LedgerBoundary(Ledger, () =>
        {
            if (caller) { ct.Cancel(); throw new OperationCanceledException(ct.Token); }
            throw new OperationCanceledException();
        });
        if (caller) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store(ledger).DeleteAsync(w.Request(), ct.Token));
        else Assert.Equal("persistence_unavailable", (await Store(ledger).DeleteAsync(w.Request(), default)).Error);
        await using var db = fixture.CreateContext();
        Assert.True(await db.TerminalErasures.AnyAsync(x => x.ResourceKind == "record" && x.ResourceId == w.Record.PublicId));
        Assert.True(await db.RetentionWorkItems.AnyAsync(x => x.OperationKey == "record-purge/" + w.Record.PublicId && x.CompletedAt == null));
        Assert.True(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
    }
}
