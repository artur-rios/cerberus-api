using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class OfflineNativeMutationTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenExternalBatchTransaction_WhenNativeCreateSucceeds_ThenOuterRollbackRemovesResourceAndDoesNotPrematurelyCommit()
    {
        using var keys = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys);
        await using var db = fixture.CreateContext(); await using var outer = await db.Database.BeginTransactionAsync();
        var factory = new TransactionContextFactory(fixture, db); var id = Guid.NewGuid();
        var result = await new RecordCreateStore(factory).CreateAsync(new(s.Actor, s.Verifier, new(id, ProfileSetup.Envelope(), DateTimeOffset.UnixEpoch, [])), default);
        Assert.Null(result.Error); Assert.True(await db.Records.AnyAsync(x => x.PublicId == id));
        await using (var outside = fixture.CreateContext()) Assert.False(await outside.Records.AnyAsync(x => x.PublicId == id));
        await outer.RollbackAsync(); await using var final = fixture.CreateContext(); Assert.False(await final.Records.AnyAsync(x => x.PublicId == id));
    }
    [FunctionalTheory]
    [InlineData("record", -1)][InlineData("record", 0)][InlineData("record", 1)]
    [InlineData("folder", -1)][InlineData("folder", 0)][InlineData("folder", 1)]
    [InlineData("collection", -1)][InlineData("collection", 0)][InlineData("collection", 1)]
    [InlineData("profile", -1)][InlineData("profile", 0)][InlineData("profile", 1)]
    [InlineData("record", 2)][InlineData("folder", 2)][InlineData("collection", 2)][InlineData("profile", 2)]
    public async Task GivenNativeLockedResource_WhenApplyingOfflineTimestamp_ThenOlderLosesAndEqualOrNewerAdvancesPersistedSequence(string kind, int offset)
    {
        using var keys = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys);
        var items = await AssociationSetup.Items(fixture, s); var profileInput = ProfileSetup.Input(s, keys, scoped);
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor, s.Verifier, profileInput), default)).Error);
        var time = DateTimeOffset.FromUnixTimeSeconds(1000); await using var db = fixture.CreateContext();
        var id = kind switch { "record" => items.Record.PublicId, "folder" => items.Folder.PublicId, "collection" => items.Collection.PublicId, _ => profileInput.ProfileId };
        if (kind == "record") await db.Records.Where(x => x.PublicId == id).ExecuteUpdateAsync(x => x.SetProperty(r => r.EditedAt, time));
        if (kind == "folder") await db.Folders.Where(x => x.PublicId == id).ExecuteUpdateAsync(x => x.SetProperty(r => r.EditedAt, time));
        if (kind == "collection") await db.Collections.Where(x => x.PublicId == id).ExecuteUpdateAsync(x => x.SetProperty(r => r.EditedAt, time));
        if (kind == "profile") await db.Profiles.Where(x => x.PublicId == id).ExecuteUpdateAsync(x => x.SetProperty(r => r.EditedAt, time));
        var previous = kind == "record" ? items.Record.ServerSequence : kind == "folder" ? items.Folder.ServerSequence : kind == "collection" ? items.Collection.ServerSequence
            : await db.Profiles.Where(x => x.PublicId == id).Select(x => x.ServerSequence).SingleAsync();
        var inspect = offset == 2;
        var envelope = ProfileSetup.Envelope(); var edited = time.AddSeconds(offset); string? error; long revision, sequence; DateTimeOffset returnedTime;
        if (kind == "record")
        { var r = await new RecordUpdateStore(fixture).UpdateAsync(new(s.Actor, s.Verifier, id, new(1, envelope, edited), OfflineLatestEdit: true, OfflineInspectOnly: inspect), default); error = r.Error; revision = r.Data!.Revision; sequence = r.Data.ServerSequence; returnedTime = r.Data.EditedAt; }
        else if (kind == "folder")
        { var r = await new FolderUpdateStore(fixture).UpdateAsync(new(s.Actor, s.Verifier, id, new(1, envelope, edited), OfflineLatestEdit: true, OfflineInspectOnly: inspect), default); error = r.Error; revision = r.Data!.Revision; sequence = r.Data.ServerSequence; returnedTime = r.Data.EditedAt; }
        else if (kind == "collection")
        { var r = await new CollectionUpdateStore(fixture).UpdateAsync(new(s.Actor, s.Verifier, id, new(1, envelope, edited), OfflineLatestEdit: true, OfflineInspectOnly: inspect), default); error = r.Error; revision = r.Data!.Revision; sequence = r.Data.ServerSequence; returnedTime = r.Data.EditedAt; }
        else
        { var r = await new ProfileUpdateStore(fixture).UpdateAsync(new(s.Actor, s.Verifier, id, new(1, envelope, edited), OfflineLatestEdit: true, OfflineInspectOnly: inspect), default); error = r.Error; revision = r.Data!.Revision; sequence = r.Data.ServerSequence; returnedTime = r.Data.EditedAt; }
        Assert.Equal(offset < 0 ? "superseded" : null, error); Assert.Equal(offset < 0 || inspect ? 1 : 2, revision);
        if (offset < 0 || inspect) Assert.Equal(previous, sequence); else Assert.True(sequence > previous);
        Assert.Equal(offset < 0 || inspect ? time : edited, returnedTime);
    }
}
