using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Query.Sync;
using ArturRios.Cerberus.Shared.Configuration;
using Moq;

namespace ArturRios.Cerberus.Query.Tests;

public class DownloadSyncChangesHandlerTests
{
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid s_actor = Guid.NewGuid(), s_account = Guid.NewGuid(), s_profile = Guid.NewGuid(), s_stream = Guid.NewGuid();
    private static readonly CerberusOptions s_options = new() { MaxPageSize = 100, RegistrationFingerprintKey = "independent-durable-sync-fixture-key-32-bytes" };
    private static SyncDownloadPage Page(bool reset = false) => new(s_account, s_profile, 1, 1, DateTimeOffset.FromUnixTimeSeconds(1000), [],
        reset ? null : new(s_profile, s_stream, 1, 0, 0), false, true, reset);
    [UnitTheory]
    [InlineData("identity", "authentication_required")]
    [InlineData("missing", "vault_access_required")]
    [InlineData("handle", "validation_failed")]
    [InlineData("zero", "validation_failed")]
    [InlineData("size", "validation_failed")]
    public async Task GivenInvalidContextOrPageSize_WhenDownloading_ThenRejectBeforePersistence(string invalid, string error)
    {
        var handler = new DownloadSyncChangesHandler(new Mock<ISyncDownloadStore>(MockBehavior.Strict).Object, s_options, new SyncCursor(s_options));
        var output = await handler.HandleAsync(new(invalid == "identity" ? Guid.Empty : s_actor, invalid == "missing" ? null : invalid == "handle" ? "invalid" : Handle,
            null, invalid == "zero" ? 0 : invalid == "size" ? 101 : 10));
        Assert.Contains(error, output.Errors);
        Assert.Null(output.Data);
    }
    [UnitTheory]
    [InlineData("not_found")]
    [InlineData("vault_access_denied")]
    [InlineData("persistence_unavailable")]
    public async Task GivenStorageOrAuthorityDenial_WhenDownloading_ThenReturnStableErrorWithoutContent(string error)
    {
        var result = await Handler(new(Error: error)).HandleAsync(new(s_actor, Handle, null, 10));
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
    }
    [UnitFact]
    public async Task GivenCurrentCheckpoint_WhenDownloading_ThenIssueDurableCursorForSameProfile()
    {
        var output = await Handler(new(Page())).HandleAsync(new(s_actor, Handle, null, 10));
        Assert.True(output.Success);
        Assert.True(output.Data!.ClearScope);
        Assert.True(new SyncCursor(s_options).TryDecode(output.Data.NextCursor, out var c));
        Assert.Equal(s_actor, c!.Actor);
        Assert.Equal(s_profile, c.ProfileId);
        Assert.Equal(s_stream, c.StreamId);
        Assert.Equal(10, c.PageSize);
        Assert.Equal(0, c.After);
        Assert.Equal(0, c.Boundary);
    }
    [UnitTheory]
    [InlineData("invalid")]
    [InlineData("actor")]
    [InlineData("page")]
    public async Task GivenUnusableContinuation_WhenDownloading_ThenRevalidateCurrentScopeAndReturnExplicitReset(string invalid)
    {
        var cursor = invalid == "invalid" ? "invalid" : new SyncCursor(s_options).Encode(new(invalid == "actor" ? Guid.NewGuid() : s_actor, s_profile, s_stream, 1, 0, 0, invalid == "page" ? 20 : 10));
        var store = new Mock<ISyncDownloadStore>(MockBehavior.Strict);
        store.Setup(x => x.DownloadAsync(It.Is<SyncDownloadRequest>(r => r.Actor == s_actor && r.InvalidCursor), It.IsAny<CancellationToken>())).ReturnsAsync(new SyncDownloadResult(Page(reset: true)));
        var output = await new DownloadSyncChangesHandler(store.Object, s_options, new SyncCursor(s_options)).HandleAsync(new(s_actor, Handle, cursor, 10));
        Assert.Contains("resynchronization_required", output.Errors);
        Assert.True(output.Data!.ResynchronizationRequired);
        Assert.True(output.Data.ClearScope);
        Assert.Null(output.Data.NextCursor);
        Assert.Empty(output.Data.Items);
    }
    [UnitTheory]
    [InlineData("empty")]
    [InlineData("policy")]
    [InlineData("profile")]
    [InlineData("counter")]
    [InlineData("resetContent")]
    public async Task GivenInvalidStoredPage_WhenDownloading_ThenFailClosed(string invalid)
    {
        var p = Page();
        p = invalid switch
        {
            "policy" => p with { PolicyRevision = 0 },
            "profile" => p with { ProfileId = Guid.Empty },
            "counter" => p with { Checkpoint = p.Checkpoint! with { Boundary = 9007199254740992 } },
            "resetContent" => p with { ResynchronizationRequired = true },
            _ => p
        };
        var output = await Handler(new(invalid == "empty" ? null : p)).HandleAsync(new(s_actor, Handle, null, 10));
        Assert.Contains("persistence_unavailable", output.Errors);
        Assert.Null(output.Data);
    }
    [UnitFact]
    public async Task GivenCancelledCaller_WhenDownloading_ThenPropagateCancellation()
    {
        using var ct = new CancellationTokenSource();
        ct.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(new()).HandleAsync(new(s_actor, Handle, null, 10), ct.Token));
    }
    [UnitFact]
    public async Task GivenNewWindowWithRegressedBoundary_WhenContinuing_ThenFailClosed()
    {
        var checkpoint = new SyncCheckpoint(s_profile, s_stream, 1, 100, 100);
        var cursor = new SyncCursor(s_options).Encode(new(s_actor, s_profile, s_stream, 1, 100, 100, 10));
        var p = Page() with
        {
            Checkpoint = checkpoint with { WindowVersion = 2, After = 2, Boundary = 2 },
            ClearScope = false,
            Items = [new(2, "record", Guid.NewGuid(), "removed", null, null)]
        };
        var result = await Handler(new(p)).HandleAsync(new(s_actor, Handle, cursor, 10));
        Assert.Contains("persistence_unavailable", result.Errors);
        Assert.Null(result.Data);
    }

    [UnitTheory]
    [InlineData("stream")]
    [InlineData("profile")]
    [InlineData("window")]
    [InlineData("boundary")]
    [InlineData("clear")]
    [InlineData("emptySkip")]
    [InlineData("duplicate")]
    [InlineData("order")]
    [InlineData("removalAfterUpsert")]
    [InlineData("type")]
    public async Task GivenMalformedContinuationPage_WhenContinuing_ThenRejectWithoutPublicData(string invalid)
    {
        var cursor = new SyncCursor(s_options).Encode(new(s_actor, s_profile, s_stream, 1, 100, 100, 10));
        var id = Guid.NewGuid();
        var removed = new SyncChangeItem(101, "record", id, "removed");
        var p = Page() with { ClearScope = false, Items = [removed], Checkpoint = new(s_profile, s_stream, 2, 101, 101) };
        p = invalid switch
        {
            "stream" => p with { Checkpoint = p.Checkpoint! with { StreamId = Guid.NewGuid() } },
            "profile" => p with { ProfileId = Guid.NewGuid() },
            "window" => p with { Checkpoint = p.Checkpoint! with { WindowVersion = 3 } },
            "boundary" => p with { Checkpoint = p.Checkpoint! with { WindowVersion = 1 } },
            "clear" => p with { ClearScope = true },
            "emptySkip" => p with { Items = [] },
            "type" => p with { Items = [removed with { ResourceKind = "account" }] },
            "duplicate" => p with { Items = [removed, removed with { Sequence = 102 }], Checkpoint = p.Checkpoint! with { After = 102, Boundary = 102 } },
            "order" => p with { Items = [removed with { Sequence = 102 }, removed with { ResourceId = Guid.NewGuid() }], Checkpoint = p.Checkpoint! with { After = 101, Boundary = 102 } },
            _ => p with
            {
                Items = [new(101, "record", id, "upsert", new SyncRecordData(id, 1, 1, DateTimeOffset.FromUnixTimeSeconds(1000),
                new("cerberus-content-v1", 1, new string('A', 43), new string('A', 16), "AQID", new string('A', 22)))),
                removed with { Sequence = 102, ResourceId = Guid.NewGuid() }],
                Checkpoint = p.Checkpoint! with { After = 102, Boundary = 102 }
            }
        };
        var output = await Handler(new(p)).HandleAsync(new(s_actor, Handle, cursor, 10));
        Assert.Contains("persistence_unavailable", output.Errors);
        Assert.Null(output.Data);
    }
    [UnitFact]
    public async Task GivenAdvancingNewWindow_WhenContinuing_ThenKeepStreamAndMonotonicCursor()
    {
        var cursors = new SyncCursor(s_options);
        var cursor = cursors.Encode(new(s_actor, s_profile, s_stream, 1, 100, 100, 10));
        var p = Page() with { ClearScope = false, Items = [new(101, "record", Guid.NewGuid(), "removed")], Checkpoint = new(s_profile, s_stream, 2, 101, 101) };
        var output = await Handler(new(p)).HandleAsync(new(s_actor, Handle, cursor, 10));
        Assert.True(output.Success);
        Assert.False(output.Data!.ClearScope);
        Assert.True(cursors.TryDecode(output.Data.NextCursor, out var next));
        Assert.Equal(s_stream, next!.StreamId);
        Assert.Equal(2, next.WindowVersion);
        Assert.Equal(101, next.After);
        Assert.Equal(101, next.Boundary);
    }

    private static DownloadSyncChangesHandler Handler(SyncDownloadResult result)
    {
        var store = new Mock<ISyncDownloadStore>(MockBehavior.Strict);
        store.Setup(x => x.DownloadAsync(It.Is<SyncDownloadRequest>(r => r.Actor == s_actor && r.PageSize == 10
            && r.AccessVerifier == "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925"), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return new(store.Object, s_options, new SyncCursor(s_options));
    }
}
