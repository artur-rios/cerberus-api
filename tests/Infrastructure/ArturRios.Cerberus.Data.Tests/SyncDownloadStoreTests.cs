using System.Security.Cryptography;
using System.Text.Json;
using System.Data.Common;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Data.Sync;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class SyncDownloadStoreTests(PostgresFixture fixture)
{
    private static readonly CerberusOptions Options = new() { MaxPageSize = 100, SyncRetention = "30.00:00:00" };
    [FunctionalFact]
    public async Task GivenSelectedProfile_WhenDownloadingThenContinuing_ThenReturnInitialSnapshotAndEmptyUnchangedDelta()
    {
        var s = await Setup(); var store = new SyncDownloadStore(fixture, Options);
        var first = await store.DownloadAsync(s.Request, default);
        Assert.Null(first.Error); var p = first.Page!;
        Assert.Equal(s.ProfilePublicId, p.ProfileId); Assert.True(p.ClearScope); Assert.False(p.HasMore);
        Assert.IsType<SyncProfileData>(Assert.Single(p.Items).Data);
        Assert.Equal(p.Checkpoint!.After, p.Checkpoint.Boundary);
        var next = await store.DownloadAsync(s.Request with { Checkpoint = p.Checkpoint }, default);
        Assert.Null(next.Error); Assert.Empty(next.Page!.Items); Assert.False(next.Page.ClearScope);
        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.SyncInventories.CountAsync(x => x.StreamId == db.SyncStreams.Where(w => w.PublicId == p.Checkpoint.StreamId).Select(w => w.Id).Single()));
        Assert.Equal(1, await db.Accounts.Where(x => x.Id == s.Owner).Select(x => x.PolicyRevision).SingleAsync());
    }
    [FunctionalTheory]
    [InlineData("updated")][InlineData("trashed")][InlineData("deleted")][InlineData("typedCollision")]
    public async Task GivenPreviouslyDisclosedRecord_WhenItsStateChanges_ThenReturnOnlyCurrentDeltaOrMinimalKnownTombstone(string change)
    {
        var s = await Setup(withRecord: true); var store = new SyncDownloadStore(fixture, Options);
        var first = await store.DownloadAsync(s.Request, default); Assert.Null(first.Error); Assert.Equal(2, first.Page!.Items.Count);
        await using var db = fixture.CreateContext();
        if (change == "updated") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.record SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence') WHERE id={s.Record}");
        if (change == "trashed") await db.Records.Where(x => x.Id == s.Record).ExecuteUpdateAsync(x => x.SetProperty(r => r.DeletedAt, DateTimeOffset.UtcNow));
        if (change is "deleted" or "typedCollision")
        {
            db.TerminalErasures.Add(new TerminalErasure { ResourceKind = change == "deleted" ? "record" : "folder", ResourceId = s.RecordPublicId, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
            if (change == "deleted") await db.Records.Where(x => x.Id == s.Record).ExecuteDeleteAsync();
        }
        var result = await store.DownloadAsync(s.Request with { Checkpoint = first.Page.Checkpoint }, default);
        Assert.Null(result.Error);
        if (change == "typedCollision") Assert.Empty(result.Page!.Items);
        else
        {
            var item = Assert.Single(result.Page!.Items, x => x.ResourceKind == "record");
            Assert.Equal(s.RecordPublicId, item.ResourceId);
            Assert.Equal(change == "updated" ? "upsert" : change == "trashed" ? "removed" : "permanently-deleted", item.EventType);
            if (change == "updated") Assert.Equal(2, Assert.IsType<SyncRecordData>(item.Data).Revision);
            else { Assert.Null(item.Data); Assert.Equal(change == "deleted", item.DeletedAt is not null); Assert.Equal(item, result.Page.Items[0]); }
        }
    }
    [FunctionalFact]
    public async Task GivenSoftRemovalAlreadyDelivered_WhenPermanentDeletionFollows_ThenRetainKnownIdentityAndEmitTombstone()
    {
        var s = await Setup(withRecord: true); var store = new SyncDownloadStore(fixture, Options);
        var first = await store.DownloadAsync(s.Request, default); Assert.Null(first.Error);
        await using var db = fixture.CreateContext();
        await db.Records.Where(x => x.Id == s.Record).ExecuteUpdateAsync(x => x.SetProperty(r => r.DeletedAt, DateTimeOffset.UtcNow));
        var removed = await store.DownloadAsync(s.Request with { Checkpoint = first.Page!.Checkpoint }, default); Assert.Null(removed.Error);
        Assert.Contains(removed.Page!.Items, x => x.ResourceId == s.RecordPublicId && x.EventType == "removed");
        db.TerminalErasures.Add(new TerminalErasure { ResourceKind = "record", ResourceId = s.RecordPublicId, DeletedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(); await db.Records.Where(x => x.Id == s.Record).ExecuteDeleteAsync();
        var terminal = await store.DownloadAsync(s.Request with { Checkpoint = removed.Page.Checkpoint }, default); Assert.Null(terminal.Error);
        var item = Assert.Single(terminal.Page!.Items);
        Assert.Equal(s.RecordPublicId, item.ResourceId); Assert.Equal("permanently-deleted", item.EventType); Assert.Null(item.Data);
    }

    [FunctionalFact]
    public async Task GivenMultipageSnapshot_WhenContinuingAndReplaying_ThenKeepBoundaryAndRevalidateEachPage()
    {
        var s = await Setup(withRecord: true); var store = new SyncDownloadStore(fixture, Options); var request = s.Request with { PageSize = 1 };
        var first = await store.DownloadAsync(request, default); Assert.Null(first.Error); Assert.True(first.Page!.HasMore);
        var second = await store.DownloadAsync(request with { Checkpoint = first.Page.Checkpoint }, default);
        Assert.Null(second.Error); Assert.False(second.Page!.HasMore); Assert.Equal(first.Page.Checkpoint!.Boundary, second.Page.Checkpoint!.Boundary);
        Assert.NotEqual(Assert.Single(first.Page.Items).ResourceId, Assert.Single(second.Page.Items).ResourceId);
        var replay = await store.DownloadAsync(request with { Checkpoint = first.Page.Checkpoint }, default);
        Assert.Null(replay.Error); Assert.Equal(second.Page.Items, replay.Page!.Items);
        Assert.Equal(second.Page.Checkpoint, replay.Page.Checkpoint);
    }
    [FunctionalFact]
    public async Task GivenVisibilityChangedDuringSnapshot_WhenContinuing_ThenRequireClearScopeBeforeFurtherData()
    {
        var s = await Setup(withRecord: true); var store = new SyncDownloadStore(fixture, Options); var request = s.Request with { PageSize = 1 };
        var first = await store.DownloadAsync(request, default); Assert.Null(first.Error); Assert.True(first.Page!.HasMore);
        await using var db = fixture.CreateContext(); await db.ProfileRecords.Where(x => x.ProfileId == s.Profile).ExecuteDeleteAsync();
        var result = await store.DownloadAsync(request with { Checkpoint = first.Page.Checkpoint }, default);
        Assert.Null(result.Error); Assert.True(result.Page!.ResynchronizationRequired); Assert.True(result.Page.ClearScope);
        Assert.Empty(result.Page.Items); Assert.Null(result.Page.Checkpoint);
    }
    [FunctionalFact]
    public async Task GivenExpiredRetainedCheckpoint_WhenContinuing_ThenDeleteItsHistoryAndRequireFreshSnapshot()
    {
        var s = await Setup(); var store = new SyncDownloadStore(fixture, Options);
        var first = await store.DownloadAsync(s.Request, default); Assert.Null(first.Error); var checkpoint = first.Page!.Checkpoint!;
        await using var db = fixture.CreateContext();
        var streamId = await db.SyncStreams.Where(x => x.PublicId == checkpoint.StreamId).Select(x => x.Id).SingleAsync();
        await db.SyncStreams.Where(x => x.Id == streamId).ExecuteUpdateAsync(x => x
            .SetProperty(v => v.CreatedAt, DateTimeOffset.UtcNow.AddDays(-31)).SetProperty(v => v.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var result = await store.DownloadAsync(s.Request with { Checkpoint = checkpoint }, default);
        Assert.Null(result.Error); Assert.True(result.Page!.ResynchronizationRequired); Assert.Empty(result.Page.Items);
        Assert.False(await db.SyncStreams.AnyAsync(x => x.Id == streamId)); Assert.False(await db.SyncChanges.AnyAsync(x => x.StreamId == streamId));
        Assert.False(await db.SyncInventories.AnyAsync(x => x.StreamId == streamId));
    }
    [FunctionalFact]
    public async Task GivenFreshUnlockOfTheSameProfile_WhenContinuing_ThenKeepDurableCursorAcrossHandleReplacement()
    {
        var s = await Setup(); var store = new SyncDownloadStore(fixture, Options);
        var first = await store.DownloadAsync(s.Request, default); Assert.Null(first.Error);
        await using var db = fixture.CreateContext();
        await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Request.AccessVerifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true));
        var verifier = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        db.VaultAccessSessions.Add(new VaultAccessSession { AccountId = s.Owner, ProfileId = s.Profile, HandleVerifier = verifier,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 });
        await db.SaveChangesAsync();
        var result = await store.DownloadAsync(s.Request with { AccessVerifier = verifier, Checkpoint = first.Page!.Checkpoint }, default);
        Assert.Null(result.Error); Assert.False(result.Page!.ResynchronizationRequired); Assert.Empty(result.Page.Items);
    }
    [FunctionalTheory]
    [InlineData("identity", "not_found")][InlineData("closing", "not_found")][InlineData("erased", "not_found")]
    [InlineData("accountwide", "vault_access_denied")][InlineData("expired", "vault_access_denied")]
    [InlineData("revoked", "vault_access_denied")][InlineData("policy", "vault_access_denied")]
    [InlineData("profile", "vault_access_denied")]
    public async Task GivenInvalidCurrentAuthority_WhenDownloading_ThenReturnNoContentOrCheckpoint(string state, string error)
    {
        var s = await Setup(); await using var db = fixture.CreateContext(); var request = s.Request;
        if (state == "identity") request = request with { Actor = Guid.NewGuid() };
        if (state == "closing") await db.Accounts.Where(x => x.Id == s.Owner).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
        if (state == "erased") { db.TerminalErasures.Add(new TerminalErasure { ResourceId = await db.Accounts.Where(x => x.Id == s.Owner).Select(x => x.PublicId).SingleAsync(), ResourceKind = "account", DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        if (state == "accountwide") await db.VaultAccessSessions.Where(x => x.HandleVerifier == request.AccessVerifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ProfileId, (long?)null));
        if (state == "expired") await db.VaultAccessSessions.Where(x => x.HandleVerifier == request.AccessVerifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        if (state == "revoked") await db.VaultAccessSessions.Where(x => x.HandleVerifier == request.AccessVerifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true));
        if (state == "policy") await db.Accounts.Where(x => x.Id == s.Owner).ExecuteUpdateAsync(x => x.SetProperty(a => a.PolicyRevision, 2));
        if (state == "profile") await db.Profiles.Where(x => x.Id == s.Profile).ExecuteUpdateAsync(x => x.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow));
        var result = await new SyncDownloadStore(fixture, Options).DownloadAsync(request, default);
        Assert.Equal(error, result.Error); Assert.Null(result.Page);
        Assert.False(await db.SyncStreams.AnyAsync(x => x.AccountId == s.Owner));
    }

    [FunctionalTheory]
    [InlineData("same", "record")][InlineData("alternate", "record")][InlineData("direct", "record")]
    [InlineData("same", "folder")][InlineData("alternate", "folder")][InlineData("direct", "folder")]
    public async Task GivenThirdRecipientEditBetweenInventoryPages_WhenDownloadingDelta_ThenNeverRemoveStillVisibleResource(string route, string kind)
    {
        using var ownerKeys = new ProtectionFixture(); using var readerKeys = new ProtectionFixture();
        using var writerKeys = new ProtectionFixture(); using var scopedKeys = new ProtectionFixture();
        var owner = await ProfileSetup.Create(fixture, ownerKeys); var reader = route == "direct" ? owner : await ProfileSetup.Create(fixture, readerKeys);
        var writer = await ProfileSetup.Create(fixture, writerKeys); var resources = await AssociationSetup.Items(fixture, owner);
        var writeCollection = route == "alternate" ? (await AssociationSetup.Items(fixture, owner)).Collection : resources.Collection;
        if (route != "direct") await AssociationSetup.Grant(fixture, owner, ownerKeys, reader, readerKeys, resources.Collection);
        await AssociationSetup.Grant(fixture, owner, ownerKeys, writer, writerKeys, writeCollection, CollectionGrantAccess.ReadWrite);
        await using var db = fixture.CreateContext();
        var records = Enumerable.Range(0, 101).Select(_ => new VaultRecord { PublicId = Guid.NewGuid(), AccountId = owner.InternalId,
            Envelope = JsonSerializer.SerializeToUtf8Bytes(ProfileSetup.Envelope(), ProtectionFixture.Json), EditedAt = DateTimeOffset.UnixEpoch }).ToArray();
        var folders = records.Select(r => new VaultFolder { PublicId = r.PublicId, AccountId = r.AccountId, Envelope = r.Envelope, EditedAt = r.EditedAt }).ToArray();
        if (kind == "record") db.Records.AddRange(records); else db.Folders.AddRange(folders);
        await db.SaveChangesAsync();
        foreach (var collection in new[] { resources.Collection, writeCollection }.DistinctBy(x => x.Id))
        {
            if (kind == "record") db.CollectionRecords.AddRange(records.Select(r => new CollectionRecord { AccountId = owner.InternalId, CollectionId = collection.Id, RecordId = r.Id }));
            else db.CollectionFolders.AddRange(folders.Select(f => new CollectionFolder { AccountId = owner.InternalId, CollectionId = collection.Id, FolderId = f.Id }));
        }
        await db.SaveChangesAsync();
        var input = ProfileSetup.Input(reader, route == "direct" ? ownerKeys : readerKeys, scopedKeys) with
        { CollectionIds = route == "direct" ? [] : [resources.Collection.PublicId], RecordIds = route == "direct" && kind == "record" ? records.Select(r => r.PublicId).ToArray() : [],
            FolderIds = route == "direct" && kind == "folder" ? folders.Select(f => f.PublicId).ToArray() : [] };
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(reader.Actor, reader.Verifier, input), default)).Error);
        var profile = await db.Profiles.SingleAsync(x => x.PublicId == input.ProfileId);
        var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        db.VaultAccessSessions.Add(new() { AccountId = reader.InternalId, ProfileId = profile.Id, HandleVerifier = verifier,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 });
        await db.SaveChangesAsync();
        var request = new SyncDownloadRequest(reader.Actor, verifier, 100);
        var first = await new SyncDownloadStore(fixture, Options).DownloadAsync(request, default); Assert.Null(first.Error);
        var complete = first;
        while (complete.Page!.HasMore)
        {
            complete = await new SyncDownloadStore(fixture, Options).DownloadAsync(request with { Checkpoint = complete.Page.Checkpoint }, default);
            Assert.Null(complete.Error);
        }
        var target = (kind == "record" ? records.Select(r => (r.PublicId, r.ServerSequence)) : folders.Select(f => (f.PublicId, f.ServerSequence))).MaxBy(x => x.ServerSequence);
        Task? editing = null;
        var writerBarrier = new CaptureWriter();
        async Task Edit()
        {
            var contexts = new ProfileSetup.Factory(fixture, writerBarrier);
            if (kind == "record")
            { var r = await new RecordUpdateStore(contexts).UpdateAsync(new(writer.Actor, writer.Verifier, target.PublicId, new(1, ProfileSetup.Envelope(), DateTimeOffset.UtcNow)), default); Assert.Null(r.Error); Assert.Equal(2, r.Data!.Revision); }
            else
            { var f = await new FolderUpdateStore(contexts).UpdateAsync(new(writer.Actor, writer.Verifier, target.PublicId, new(1, ProfileSetup.Envelope(), DateTimeOffset.UtcNow)), default); Assert.Null(f.Error); Assert.Equal(2, f.Data!.Revision); }
        }
        var barrier = new BeforeSecondInventoryPage(kind, async () =>
        {
            editing = Edit();
            await writerBarrier.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            // Old implementation lets this writer complete between scan pages;
            // fixed implementation visibly blocks it on the collection lock.
            while (!editing.IsCompleted)
            {
                if (await db.Database.SqlQuery<bool>($"SELECT cardinality(pg_blocking_pids({writerBarrier.Pid}))>0 AS \"Value\"").SingleAsync(timeout.Token)) break;
                await Task.Delay(10, timeout.Token);
            }
        });
        var delta = await new SyncDownloadStore(new ProfileSetup.Factory(fixture, barrier), Options)
            .DownloadAsync(request with { Checkpoint = complete.Page.Checkpoint }, default);
        Assert.Equal(2, barrier.Pages); Assert.Null(delta.Error); Assert.False(delta.Page!.ResynchronizationRequired);
        Assert.DoesNotContain(delta.Page.Items, x => x.ResourceId == target.PublicId && x.EventType != "upsert");
        await editing!.WaitAsync(TimeSpan.FromSeconds(5));
        var following = await new SyncDownloadStore(fixture, Options).DownloadAsync(request with { Checkpoint = delta.Page.Checkpoint }, default);
        Assert.Null(following.Error);
        var item = Assert.Single(delta.Page.Items.Concat(following.Page!.Items), x => x.ResourceId == target.PublicId);
        Assert.Equal(kind, item.ResourceKind); Assert.NotNull(item.Data); Assert.Equal(2, item.Data.Revision);
    }

    [FunctionalFact]
    public async Task GivenRegressedDescriptorSequence_WhenStartingNewWindow_ThenRollbackWithoutAdvancingCheckpoint()
    {
        var s = await Setup(withRecord: true); var store = new SyncDownloadStore(fixture, Options);
        var first = await store.DownloadAsync(s.Request, default); Assert.Null(first.Error);
        await using var db = fixture.CreateContext();
        await db.Records.Where(x => x.Id == s.Record).ExecuteUpdateAsync(x => x.SetProperty(r => r.Revision, 2));
        var original = await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT setval('cerberus.server_sequence',1,true)");
            var result = await store.DownloadAsync(s.Request with { Checkpoint = first.Page!.Checkpoint }, default);
            Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Page);
            var stream = await db.SyncStreams.AsNoTracking().SingleAsync(x => x.PublicId == first.Page.Checkpoint!.StreamId);
            Assert.Equal(first.Page.Checkpoint!.WindowVersion, stream.WindowVersion); Assert.Equal(first.Page.Checkpoint.Boundary, stream.Boundary);
            Assert.Equal(2, await db.SyncChanges.CountAsync(x => x.StreamId == stream.Id));
        }
        finally { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{original},true)"); }
    }

    [FunctionalTheory]
    [InlineData("invalid")][InlineData("profile")][InlineData("missing")][InlineData("pageSize")][InlineData("window")][InlineData("skip")]
    public async Task GivenUnusableCheckpoint_WhenContinuing_ThenReturnAuthorizedClearScopeWithoutAdvancingHistory(string kind)
    {
        var s = await Setup(); var store = new SyncDownloadStore(fixture, Options);
        var first = await store.DownloadAsync(s.Request, default); Assert.Null(first.Error); var cp = first.Page!.Checkpoint!;
        var request = s.Request with { Checkpoint = cp };
        request = kind switch
        {
            "invalid" => request with { InvalidCursor = true }, "profile" => request with { Checkpoint = cp with { ProfileId = Guid.NewGuid() } },
            "missing" => request with { Checkpoint = cp with { StreamId = Guid.NewGuid() } }, "pageSize" => request with { PageSize = 1 },
            "window" => request with { Checkpoint = cp with { WindowVersion = cp.WindowVersion + 1 } },
            _ => request with { Checkpoint = cp with { After = cp.Boundary + 1, Boundary = cp.Boundary + 1 } }
        };
        var before = await SyncState(s.Owner); var result = await store.DownloadAsync(request, default);
        Assert.Null(result.Error); Assert.True(result.Page!.ResynchronizationRequired); Assert.True(result.Page.ClearScope);
        Assert.Empty(result.Page.Items); Assert.Null(result.Page.Checkpoint); Assert.Equal(before, await SyncState(s.Owner));
    }

    [FunctionalTheory]
    [InlineData("policy", "vault_access_denied")][InlineData("generation", "vault_access_denied")]
    [InlineData("session", "vault_access_denied")][InlineData("profile", "vault_access_denied")]
    [InlineData("revision", "revision_conflict")][InlineData("closing", "not_found")]
    public async Task GivenAuthorityChangeWhileWaitingForRoot_WhenDownloading_ThenRecheckAndRollbackWithoutContent(string kind, string error)
    {
        var s = await Setup(); await using var writer = fixture.CreateContext();
        await using var tx = await writer.Database.BeginTransactionAsync();
        await writer.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.Owner} FOR NO KEY UPDATE");
        var signal = new BeforeRoot();
        var downloading = new SyncDownloadStore(new ProfileSetup.Factory(fixture, signal), Options).DownloadAsync(s.Request, default);
        try
        {
            await signal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            if (kind == "policy") await writer.Accounts.Where(x => x.Id == s.Owner).ExecuteUpdateAsync(x => x.SetProperty(a => a.PolicyRevision, 2));
            if (kind == "generation") await writer.Accounts.Where(x => x.Id == s.Owner).ExecuteUpdateAsync(x => x.SetProperty(a => a.RevocationGeneration, 2));
            if (kind == "closing") await writer.Accounts.Where(x => x.Id == s.Owner).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
            if (kind == "session") await writer.VaultAccessSessions.Where(x => x.HandleVerifier == s.Request.AccessVerifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true));
            if (kind == "profile") await writer.Profiles.Where(x => x.Id == s.Profile).ExecuteUpdateAsync(x => x.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow));
            if (kind == "revision") await writer.Profiles.Where(x => x.Id == s.Profile).ExecuteUpdateAsync(x => x.SetProperty(p => p.Revision, 2));
            await tx.CommitAsync(); var result = await downloading.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(error, result.Error); Assert.Null(result.Page);
            await using var db = fixture.CreateContext(); Assert.False(await db.SyncStreams.AnyAsync(x => x.AccountId == s.Owner));
        }
        finally { await tx.DisposeAsync(); await downloading.WaitAsync(TimeSpan.FromSeconds(10)); }
    }

    [FunctionalFact]
    public async Task GivenConcurrentSameCursor_WhenContinuing_ThenSerializeCheckpointAndReturnSameCurrentPage()
    {
        var s = await Setup(withRecord: true); var store = new SyncDownloadStore(fixture, Options); var request = s.Request with { PageSize = 1 };
        await AddCollection(s);
        var first = await store.DownloadAsync(request, default); Assert.Null(first.Error);
        var results = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => store.DownloadAsync(request with { Checkpoint = first.Page!.Checkpoint }, default)));
        Assert.All(results, r => Assert.Null(r.Error)); Assert.All(results, r => Assert.Equal(results[0].Page!.Checkpoint, r.Page!.Checkpoint));
        Assert.All(results, r => Assert.Equal(results[0].Page!.Items, r.Page!.Items));
    }

    [FunctionalFact]
    public async Task GivenOverlappingStreamsWithCollections_WhenBothHoldRootShares_ThenSerializeCollectionScanWithoutRootUpgradeDeadlock()
    {
        var s = await Setup(withRecord: true); await AddCollection(s); var store = new SyncDownloadStore(fixture, Options);
        var first = await store.DownloadAsync(s.Request, default); var second = await store.DownloadAsync(s.Request, default);
        Assert.Null(first.Error); Assert.Null(second.Error);
        await using var db = fixture.CreateContext(); await db.Records.Where(x => x.Id == s.Record).ExecuteUpdateAsync(x => x.SetProperty(r => r.Revision, 2));
        var barrier = new BothBeforeCollections(); var guarded = new SyncDownloadStore(new ProfileSetup.Factory(fixture, barrier), Options);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        var results = await Task.WhenAll(guarded.DownloadAsync(s.Request with { Checkpoint = first.Page!.Checkpoint }, ct.Token),
            guarded.DownloadAsync(s.Request with { Checkpoint = second.Page!.Checkpoint }, ct.Token));
        Assert.Equal(2, barrier.Arrivals); Assert.All(results, r => Assert.Null(r.Error));
        Assert.All(results, r => Assert.Equal(2, Assert.IsType<SyncRecordData>(Assert.Single(r.Page!.Items).Data).Revision));
    }
    private async Task AddCollection(State s)
    {
        await using var db = fixture.CreateContext(); var c = new VaultCollection { PublicId = Guid.NewGuid(), AccountId = s.Owner,
            Envelope = JsonSerializer.SerializeToUtf8Bytes(ProfileSetup.Envelope(), ProtectionFixture.Json), EditedAt = DateTimeOffset.UnixEpoch };
        db.Collections.Add(c); await db.SaveChangesAsync(); db.ProfileCollections.Add(new() { ProfileId = s.Profile, CollectionId = c.Id }); await db.SaveChangesAsync();
    }
    private sealed class BothBeforeCollections : DbCommandInterceptor
    {
        private int arrivals;
        public int Arrivals => arrivals;
        private readonly TaskCompletionSource both = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM cerberus.collection c JOIN cerberus.account owner", StringComparison.Ordinal))
            { if (Interlocked.Increment(ref arrivals) == 2) both.TrySetResult(); await both.Task.WaitAsync(cancellationToken); }
            return result;
        }
    }

    [FunctionalFact]
    public async Task GivenUnrelatedTerminalIdentity_WhenDownloadingDelta_ThenNeverDiscloseItsTombstone()
    {
        var s = await Setup(); var store = new SyncDownloadStore(fixture, Options);
        var first = await store.DownloadAsync(s.Request, default); Assert.Null(first.Error);
        await using var db = fixture.CreateContext(); db.TerminalErasures.Add(new() { ResourceKind = "record", ResourceId = Guid.NewGuid(), DeletedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(); var result = await store.DownloadAsync(s.Request with { Checkpoint = first.Page!.Checkpoint }, default);
        Assert.Null(result.Error); Assert.Empty(result.Page!.Items);
    }

    [FunctionalTheory]
    [InlineData("future")][InlineData("stamp")][InlineData("complete")][InlineData("terminalVisible")]
    public async Task GivenCorruptRetainedMetadata_WhenContinuing_ThenFailClosedWithoutCheckpointAdvance(string kind)
    {
        var s = await Setup(); var store = new SyncDownloadStore(fixture, Options); var first = await store.DownloadAsync(s.Request, default);
        Assert.Null(first.Error); var cp = first.Page!.Checkpoint!; await using var db = fixture.CreateContext();
        var stream = await db.SyncStreams.SingleAsync(x => x.PublicId == cp.StreamId);
        if (kind == "future") { stream.CreatedAt = DateTimeOffset.UtcNow.AddDays(1); stream.ExpiresAt = stream.CreatedAt.AddDays(2); }
        if (kind == "stamp") stream.VisibilityStamp = [1];
        if (kind == "complete") stream.WindowComplete = false;
        if (kind == "terminalVisible") await db.SyncInventories.Where(x => x.StreamId == stream.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Terminal, true));
        await db.SaveChangesAsync(); var before = await SyncState(s.Owner);
        var result = await store.DownloadAsync(s.Request with { Checkpoint = cp }, default);
        Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Page); Assert.Equal(before, await SyncState(s.Owner));
    }

    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenFailureAfterDescriptorWrites_WhenDownloading_ThenRollbackAndRetryCleanly(bool cancelled)
    {
        var s = await Setup(withRecord: true); using var ct = new CancellationTokenSource();
        var store = new SyncDownloadStore(new ProfileSetup.Factory(fixture, new AfterDescriptors(ct, cancelled)), Options);
        if (cancelled) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.DownloadAsync(s.Request, ct.Token));
        else { var failed = await store.DownloadAsync(s.Request, ct.Token); Assert.Equal("persistence_unavailable", failed.Error); Assert.Null(failed.Page); }
        await using var db = fixture.CreateContext(); Assert.False(await db.SyncStreams.AnyAsync(x => x.AccountId == s.Owner));
        var retry = await new SyncDownloadStore(fixture, Options).DownloadAsync(s.Request, default);
        Assert.Null(retry.Error); Assert.Equal(2, retry.Page!.Items.Count);
    }

    [FunctionalFact]
    public async Task GivenUnavailableDatabaseOrCancelledCaller_WhenDownloading_ThenFailClosedOrPropagate()
    {
        var s = await Setup(); var dead = new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=unavailable;Username=none;Timeout=1;Pooling=false").Options);
        var result = await new SyncDownloadStore(dead, Options).DownloadAsync(s.Request, default);
        Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Page);
        using var ct = new CancellationTokenSource(); ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new SyncDownloadStore(fixture, Options).DownloadAsync(s.Request, ct.Token));
    }

    [FunctionalTheory]
    [InlineData("grant")][InlineData("member")][InlineData("collection")][InlineData("owner")][InlineData("terminal")][InlineData("corruptGrant")]
    public async Task GivenPreviouslyVisibleSharedContent_WhenPermissionOrNativeProvisioningChanges_ThenReturnRemovalsOrFailClosed(string change)
    {
        using var ownerKeys = new ProtectionFixture(); using var readerKeys = new ProtectionFixture(); using var scoped = new ProtectionFixture();
        var owner = await ProfileSetup.Create(fixture, ownerKeys); var reader = await ProfileSetup.Create(fixture, readerKeys);
        var items = await AssociationSetup.Items(fixture, owner); var grant = await AssociationSetup.Grant(fixture, owner, ownerKeys, reader, readerKeys, items.Collection);
        await using var db = fixture.CreateContext();
        db.CollectionRecords.Add(new() { AccountId = owner.InternalId, CollectionId = items.Collection.Id, RecordId = items.Record.Id });
        db.CollectionFolders.Add(new() { AccountId = owner.InternalId, CollectionId = items.Collection.Id, FolderId = items.Folder.Id }); await db.SaveChangesAsync();
        var input = ProfileSetup.Input(reader, readerKeys, scoped) with { CollectionIds = [items.Collection.PublicId] };
        Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(reader.Actor, reader.Verifier, input), default)).Error);
        var profile = await db.Profiles.SingleAsync(x => x.PublicId == input.ProfileId); var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        db.VaultAccessSessions.Add(new() { AccountId = reader.InternalId, ProfileId = profile.Id, HandleVerifier = verifier,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 });
        await db.SaveChangesAsync(); var request = new SyncDownloadRequest(reader.Actor, verifier, 10); var store = new SyncDownloadStore(fixture, Options);
        var first = await store.DownloadAsync(request, default); Assert.Null(first.Error); Assert.Equal(5, first.Page!.Items.Count);
        Assert.IsType<SyncGrantData>(Assert.Single(first.Page.Items, x => x.ResourceKind == "grant").Data);
        Assert.IsType<SyncFolderData>(Assert.Single(first.Page.Items, x => x.ResourceKind == "folder").Data);
        if (change == "grant") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked));
        if (change == "member") await db.CollectionRecords.Where(x => x.CollectionId == items.Collection.Id).ExecuteDeleteAsync();
        if (change == "collection") await db.Collections.Where(x => x.Id == items.Collection.Id).ExecuteUpdateAsync(x => x.SetProperty(c => c.DeletedAt, DateTimeOffset.UtcNow));
        if (change == "owner") await db.Accounts.Where(x => x.Id == owner.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
        if (change == "terminal") { db.TerminalErasures.Add(new() { ResourceKind = "record", ResourceId = items.Record.PublicId, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        if (change == "corruptGrant") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.RecipientKeyEnvelope, "invalid"u8.ToArray()));
        var before = await SyncState(reader.InternalId); var result = await store.DownloadAsync(request with { Checkpoint = first.Page.Checkpoint }, default);
        if (change == "corruptGrant")
        { Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Page); Assert.Equal(before, await SyncState(reader.InternalId)); return; }
        Assert.Null(result.Error); var removed = Assert.Single(result.Page!.Items, x => x.ResourceKind == "record");
        Assert.Equal(change == "terminal" ? "permanently-deleted" : "removed", removed.EventType); Assert.Null(removed.Data);
        Assert.DoesNotContain(result.Page.Items, x => x.ResourceKind == "record" && x.EventType == "upsert");
        if (change is "grant" or "collection" or "owner")
        { Assert.Contains(result.Page.Items, x => x.ResourceKind == "grant" && x.EventType == "removed"); Assert.Contains(result.Page.Items, x => x.ResourceKind == "folder" && x.EventType == "removed"); }
    }

    [FunctionalFact]
    public async Task GivenNaturalExpiryAfterInventory_WhenFinalizing_ThenDenyAndRollbackDescriptors()
    {
        var s = await Setup(withRecord: true); await using var db = fixture.CreateContext();
        var deadline = await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp()+INTERVAL '2 seconds' AS \"Value\"").SingleAsync();
        await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Request.AccessVerifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ExpiresAt, deadline));
        var barrier = new BeforeDescriptors(async () =>
        {
            var wait = deadline - DateTimeOffset.UtcNow;
            if (wait > TimeSpan.Zero) await Task.Delay(wait + TimeSpan.FromMilliseconds(100));
        });
        var result = await new SyncDownloadStore(new ProfileSetup.Factory(fixture, barrier), Options).DownloadAsync(s.Request, default);
        Assert.True(barrier.Reached); Assert.Equal("vault_access_denied", result.Error); Assert.Null(result.Page);
        Assert.False(await db.SyncStreams.AnyAsync(x => x.AccountId == s.Owner));
    }

    [FunctionalFact]
    public async Task GivenExhaustedDescriptorSequence_WhenStartingSnapshot_ThenRollbackAndReturnConflict()
    {
        var s = await Setup(); await using var db = fixture.CreateContext();
        var previous = await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();
        try
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{ArturRios.Cerberus.Domain.Protection.ProtocolBinary.MaxInteger},true)");
            var result = await new SyncDownloadStore(fixture, Options).DownloadAsync(s.Request, default);
            Assert.Equal("revision_conflict", result.Error); Assert.Null(result.Page); Assert.False(await db.SyncStreams.AnyAsync(x => x.AccountId == s.Owner));
        }
        finally { await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{previous},true)"); }
    }

    private sealed class BeforeDescriptors(Func<Task> action) : DbCommandInterceptor
    {
        public bool Reached { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        { if (command.CommandText.Contains("FROM cerberus.sync_change", StringComparison.Ordinal)) { Reached = true; await action(); } return result; }
    }

    private async Task<string> SyncState(long owner)
    {
        await using var db = fixture.CreateContext(); var ids = await db.SyncStreams.Where(x => x.AccountId == owner).Select(x => x.Id).ToArrayAsync();
        return JsonSerializer.Serialize(new { Streams = await db.SyncStreams.AsNoTracking().Where(x => x.AccountId == owner).OrderBy(x => x.Id).ToArrayAsync(),
            Inventory = await db.SyncInventories.AsNoTracking().Where(x => ids.Contains(x.StreamId)).OrderBy(x => x.Id).ToArrayAsync(),
            Changes = await db.SyncChanges.AsNoTracking().Where(x => ids.Contains(x.StreamId)).OrderBy(x => x.Sequence).ToArrayAsync() });
    }
    private sealed class BeforeRoot : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        { if (command.CommandText.Contains("ORDER BY public_id FOR SHARE", StringComparison.Ordinal)) Reached.TrySetResult(); return ValueTask.FromResult(result); }
    }
    private sealed class AfterDescriptors(CancellationTokenSource source, bool cancel) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("FROM cerberus.sync_change", StringComparison.Ordinal))
            { if (cancel) { source.Cancel(); cancellationToken.ThrowIfCancellationRequested(); } throw new TimeoutException("Synthetic checkpoint dependency fault."); }
            return ValueTask.FromResult(result);
        }
    }

    private sealed class BeforeSecondInventoryPage(string kind, Func<Task> edit) : DbCommandInterceptor
    {
        public int Pages { get; private set; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("relevant_grants AS", StringComparison.Ordinal)
                && command.CommandText.Contains("jsonb_build_object('" + kind + "Id'", StringComparison.Ordinal))
                if (++Pages == 2) await edit();
            return result;
        }
    }
    private sealed class CaptureWriter : DbCommandInterceptor
    {
        public TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Pid { get; private set; }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (command.CommandText.Contains("ORDER BY c.public_id FOR SHARE", StringComparison.Ordinal))
            { Pid = ((Npgsql.NpgsqlConnection)command.Connection!).ProcessID; Reached.TrySetResult(); }
            return ValueTask.FromResult(result);
        }
    }

    private async Task<State> Setup(bool withRecord = false)
    {
        using var client = new ProtectionFixture(); using var scoped = new ProtectionFixture();
        await using var db = fixture.CreateContext(); var actor = Guid.NewGuid();
        var a = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = actor }; db.Accounts.Add(a); await db.SaveChangesAsync();
        var id = Guid.NewGuid();
        var envelope = new EncryptedEnvelope("cerberus-content-v1", 1, ProtectionFixture.Encode(new byte[32]), ProtectionFixture.Encode(new byte[12]), "AQID", ProtectionFixture.Encode(new byte[16]));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtectionFixture.Json);
        var p = new Profile { AccountId = a.Id, PublicId = id, EditedAt = DateTimeOffset.UtcNow, Envelope = bytes,
            KeyWrappers = JsonSerializer.SerializeToUtf8Bytes(new ProfileKeyWrappers("Master", scoped.Material.UnlockVerifier, client.Wrap(a.PublicId, "profile", id, actor), null), ProtectionFixture.Json) };
        db.Profiles.Add(p); await db.SaveChangesAsync();
        VaultRecord? r = null;
        if (withRecord)
        {
            r = new VaultRecord { PublicId = Guid.NewGuid(), AccountId = a.Id, Envelope = bytes, EditedAt = DateTimeOffset.UtcNow };
            db.Records.Add(r); await db.SaveChangesAsync(); db.ProfileRecords.Add(new ProfileRecord { AccountId = a.Id, ProfileId = p.Id, RecordId = r.Id });
        }
        var verifier = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        db.VaultAccessSessions.Add(new VaultAccessSession { AccountId = a.Id, ProfileId = p.Id, HandleVerifier = verifier,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 });
        await db.SaveChangesAsync(); return new(a.Id, p.Id, id, r?.Id ?? 0, r?.PublicId ?? Guid.Empty, new(actor, verifier, 10));
    }
    private sealed record State(long Owner, long Profile, Guid ProfilePublicId, long Record, Guid RecordPublicId, SyncDownloadRequest Request);
}
