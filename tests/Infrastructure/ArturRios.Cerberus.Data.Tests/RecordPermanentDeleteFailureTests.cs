using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

public sealed partial class RecordPermanentDeleteStoreTests
{
    [FunctionalTheory]
    [InlineData("direct", "missing", "not_found")]
    [InlineData("direct", "snapshot", "persistence_unavailable")]
    [InlineData("direct", "profileTrash", "vault_access_denied")]
    [InlineData("direct", "wrongProfile", "not_found")]
    [InlineData("folder", "folderTrash", "not_found")]
    [InlineData("collection", "collectionTrash", "not_found")]
    [InlineData("collection", "removedCollection", "not_found")]
    [InlineData("descendant", "folderTrash", "not_found")]
    public async Task GivenChangedRestrictedTrashScope_WhenPermanentlyDeleting_ThenNoIntentOrErasure(string route, string change, string error)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, route, true);
        await using var db = fixture.CreateContext();
        var entry = await db.TrashEntries.SingleAsync(x => x.ResourceKind == "record" && x.ResourceId == w.Record.PublicId);
        if (change == "missing") db.TrashEntries.Remove(entry);
        if (change == "snapshot") entry.AssociationSnapshot = "bad-snapshot"u8.ToArray();
        if (change == "wrongProfile") entry.AssociationSnapshot = JsonSerializer.SerializeToUtf8Bytes(new RecordAssociationSnapshot(null, [Guid.NewGuid()], []), ProtectionFixture.Json);
        if (change == "profileTrash") await db.Profiles.Where(x => x.Id == w.Selection).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow));
        if (change == "folderTrash") await db.Folders.Where(x => x.Id == w.Items.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow));
        if (change == "collectionTrash") await db.Collections.Where(x => x.Id == w.Items.Collection.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow));
        if (change == "removedCollection") await db.ProfileCollections.Where(x => x.ProfileId == w.Selection && x.CollectionId == w.Items.Collection.Id).ExecuteDeleteAsync();
        await db.SaveChangesAsync();
        var before = await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId);
        Assert.Equal(error, (await Store().DeleteAsync(w.Request(), default)).Error);
        Assert.Equal(before, await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
    }

    [FunctionalTheory]
    [InlineData("badSnapshot")][InlineData("expiredRetention")]
    public async Task GivenAccountwideOwnedTrash_WhenDeleting_ThenEraseDespiteUnusedHistoryOrElapsedRestoreWindow(string kind)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", true);
        await using var db = fixture.CreateContext();
        if (kind == "badSnapshot") await db.TrashEntries.Where(x => x.ResourceKind == "record" && x.ResourceId == w.Record.PublicId).ExecuteUpdateAsync(x => x.SetProperty(v => v.AssociationSnapshot, "broken"u8.ToArray()));
        else await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.PurgeAt, DateTimeOffset.UtcNow.AddDays(-1)));
        Assert.Null((await Store().DeleteAsync(w.Request(), default)).Error);
        Assert.False(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
    }

    [FunctionalTheory]
    [InlineData("revisionZero", "persistence_unavailable")]
    [InlineData("revisionUnsafe", "persistence_unavailable")]
    [InlineData("sequenceZero", "persistence_unavailable")]
    [InlineData("sequenceUnsafe", "persistence_unavailable")]
    [InlineData("parentRevisionMax", "revision_conflict")]
    [InlineData("parentRevisionZero", "persistence_unavailable")]
    [InlineData("parentSequenceZero", "persistence_unavailable")]
    public async Task GivenUnsafeRequiredMetadata_WhenDeleting_ThenNoIntentOrParentMutation(string kind, string error)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "folder", false);
        await using var db = fixture.CreateContext();
        if (kind == "revisionZero") await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revision, 0));
        if (kind == "revisionUnsafe") await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revision, ProtocolBinary.MaxInteger + 1));
        if (kind == "sequenceZero") await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ServerSequence, 0));
        if (kind == "sequenceUnsafe") await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ServerSequence, ProtocolBinary.MaxInteger + 1));
        if (kind == "parentRevisionMax") await db.Folders.Where(x => x.Id == w.Items.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revision, ProtocolBinary.MaxInteger));
        if (kind == "parentRevisionZero") await db.Folders.Where(x => x.Id == w.Items.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revision, 0));
        if (kind == "parentSequenceZero") await db.Folders.Where(x => x.Id == w.Items.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ServerSequence, 0));
        var before = await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId);
        Assert.Equal(error, (await Store().DeleteAsync(w.Request(), default)).Error);
        Assert.Equal(before, await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId));
    }

    [FunctionalTheory]
    [InlineData("record")][InlineData("profile")][InlineData("collection")][InlineData("folder")]
    public async Task GivenFailureAfterIntentMutation_WhenDeleting_ThenRollbackEverythingAndAllowRetry(string table)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "direct", false);
        await using var db = fixture.CreateContext();
        await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.FolderId, w.Items.Folder.Id));
        db.CollectionRecords.Add(new() { AccountId = w.State.InternalId, CollectionId = w.Items.Collection.Id, RecordId = w.Record.Id });
        await db.SaveChangesAsync();
        var before = await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId);
        Assert.Equal("persistence_unavailable", (await Store(factory: new ProfileSetup.Factory(fixture, new FailMutation(table))).DeleteAsync(w.Request(), default)).Error);
        Assert.Equal(before, await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
        Assert.Null((await Store().DeleteAsync(w.Request(), default)).Error);
    }

    [FunctionalTheory]
    [InlineData("recordTerminal", "not_found")][InlineData("ownerTerminal", "not_found")]
    [InlineData("ancestorHidden", "not_found")][InlineData("ancestorCycle", "persistence_unavailable")]
    public async Task GivenAuthorityChangesBeforeFinalIntent_WhenDeleting_ThenReclassifyWithoutMutation(string change, string error)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "folder", false);
        await using var setup = fixture.CreateContext();
        var root = new ArturRios.Cerberus.Domain.Resources.VaultFolder { AccountId = w.State.InternalId, PublicId = Guid.NewGuid(), Envelope = w.Items.Folder.Envelope, EditedAt = w.Items.Folder.EditedAt };
        setup.Folders.Add(root);
        await setup.SaveChangesAsync();
        await setup.Folders.Where(x => x.Id == w.Items.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ParentFolderId, root.Id));
        var hook = new BeforeIntent(async () =>
        {
            await using var db = fixture.CreateContext();
            if (change.EndsWith("Terminal")) { db.TerminalErasures.Add(new() { ResourceKind = change == "ownerTerminal" ? "account" : "record", ResourceId = change == "ownerTerminal" ? w.State.AccountId : w.Record.PublicId, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
            if (change == "ancestorHidden") await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow));
            if (change == "ancestorCycle") await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ParentFolderId, w.Items.Folder.Id));
        });
        Assert.Equal(error, (await Store(factory: new ProfileSetup.Factory(fixture, hook)).DeleteAsync(w.Request(), default)).Error);
        await using var check = fixture.CreateContext();
        Assert.True(await check.Records.AnyAsync(x => x.Id == w.Record.Id && x.DeletedAt == null));
        Assert.False(await check.RetentionWorkItems.AnyAsync(x => x.OperationKey == "record-purge/" + w.Record.PublicId));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
    }

    [FunctionalFact]
    public async Task GivenFreshExpiryAtFinalStatement_WhenDeleting_Then403AndNoTerminalIntent()
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        await using var db = fixture.CreateContext();
        await db.VaultAccessSessions.Where(x => x.HandleVerifier == w.State.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ExpiresAt, deadline));
        var hook = new BeforeIntent(async () => { var left = deadline - DateTimeOffset.UtcNow; if (left > TimeSpan.Zero) await Task.Delay(left + TimeSpan.FromMilliseconds(50)); });
        var before = await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId);
        Assert.Equal("vault_access_denied", (await Store(factory: new ProfileSetup.Factory(fixture, hook)).DeleteAsync(w.Request(), default)).Error);
        Assert.Equal(before, await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId));
    }

    [FunctionalTheory]
    [InlineData("edit")][InlineData("move")][InlineData("trash")][InlineData("permanent")]
    public async Task GivenActualConcurrentExpectedRevisionWriters_WhenDeleting_ThenKeepOneWinnerAndNoResurrection(string writer)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        var other = writer switch
        {
            "edit" => new RecordUpdateStore(fixture).UpdateAsync(new(w.State.Actor, w.State.Verifier, w.Record.PublicId, new(1, ProfileSetup.Envelope(), DateTimeOffset.UtcNow)), default).ContinueWith(t => t.Result.Error),
            "move" => new RecordMoveStore(fixture).MoveAsync(new(w.State.Actor, w.State.Verifier, w.Record.PublicId, 1, w.Items.Folder.PublicId), default).ContinueWith(t => t.Result.Error),
            "trash" => new RecordTrashStore(fixture).TrashAsync(new(w.State.Actor, w.State.Verifier, w.Record.PublicId, 1), default).ContinueWith(t => t.Result.Error),
            _ => Store().DeleteAsync(w.Request(), default).ContinueWith(t => t.Result.Error)
        };
        var deletion = Store().DeleteAsync(w.Request(), default);
        await Task.WhenAll(other, deletion);
        var failures = new[] { await other, (await deletion).Error };
        Assert.Single(failures,x => x is null);
        Assert.Contains(failures.Single(x => x is not null), new[] { "revision_conflict", "not_found" });
        await using var check = fixture.CreateContext();
        if (await check.TerminalErasures.AnyAsync(x => x.ResourceKind == "record" && x.ResourceId == w.Record.PublicId)) Assert.False(await check.Records.AnyAsync(x => x.Id == w.Record.Id));
        else Assert.Equal(2, (await check.Records.SingleAsync(x => x.Id == w.Record.Id)).Revision);
    }

    private sealed class FailMutation(string table) : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken ct = default)
        { if (command.CommandText.Contains("UPDATE cerberus." + table)) throw new TimeoutException("fixture intent mutation failure"); return ValueTask.FromResult(result); }
    }
    private sealed class BeforeIntent(Func<Task> callback) : DbCommandInterceptor
    {
        private bool invoked;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (!invoked && command.CommandText.Contains("UPDATE cerberus.record")) { invoked = true; await callback(); } return result; }
    }
}
