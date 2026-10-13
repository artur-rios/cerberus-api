using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Cerberus.Query.Offline;
using Moq;

namespace ArturRios.Cerberus.Query.Tests;

public class GetOfflinePolicyHandlerTests
{
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid Identity = Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);

    [UnitTheory]
    [InlineData("identity", "authentication_required")]
    [InlineData("missing", "vault_access_required")]
    [InlineData("malformed", "validation_failed")]
    public async Task GivenInvalidContext_WhenReadingOfflinePolicy_ThenRejectBeforePersistence(string invalid, string error)
    {
        var query = new GetOfflinePolicyQuery(invalid == "identity" ? Guid.Empty : Identity,
            invalid == "missing" ? null : invalid == "malformed" ? "invalid" : Handle);
        var output = await new GetOfflinePolicyHandler(new Mock<IOfflinePolicyReadStore>(MockBehavior.Strict).Object, new Clock()).HandleAsync(query);
        Assert.Contains(error, output.Errors);
        Assert.Null(output.Data);
    }

    [UnitTheory]
    [InlineData("not_found")]
    [InlineData("vault_access_denied")]
    [InlineData("persistence_unavailable")]
    public async Task GivenRejectedStoreRead_WhenHandling_ThenReturnStableErrorWithoutPolicy(string error)
    {
        var output = await Handler(new(Error: error)).HandleAsync(new(Identity, Handle));
        Assert.Contains(error, output.Errors);
        Assert.Null(output.Data);
    }

    [UnitTheory]
    [InlineData(true, 864000000000L)]
    [InlineData(true, 10L)]
    [InlineData(false, null)]
    public async Task GivenValidSavedPolicy_WhenReading_ThenReturnItsExactDurationAndVersions(bool enabled, long? ticks)
    {
        var interval = ticks.HasValue ? TimeSpan.FromTicks(ticks.Value) : (TimeSpan?)null;
        var output = await Handler(new(new(enabled, interval, 7, 9))).HandleAsync(new(Identity, Handle));
        Assert.True(output.Success);
        Assert.Equal(enabled, output.Data!.RenewalEnabled);
        Assert.Equal(interval, output.Data.RenewalInterval);
        Assert.Equal(7, output.Data.PolicyRevision);
        Assert.Equal(9, output.Data.RevocationGeneration);
    }

    [UnitTheory]
    [InlineData(true, null, 1, 1)]
    [InlineData(true, 0L, 1, 1)]
    [InlineData(true, -10L, 1, 1)]
    [InlineData(false, 10L, 1, 1)]
    [InlineData(true, 10L, 0, 1)]
    [InlineData(true, 10L, 1, 0)]
    public async Task GivenCorruptPolicy_WhenReading_ThenFailClosed(bool enabled, long? ticks, long revision, long generation)
    {
        var output = await Handler(new(new(enabled, ticks.HasValue ? TimeSpan.FromTicks(ticks.Value) : null, revision, generation))).HandleAsync(new(Identity, Handle));
        Assert.Contains("persistence_unavailable", output.Errors);
        Assert.Null(output.Data);
    }

    [UnitFact]
    public async Task GivenIncompleteStoreResult_WhenReading_ThenFailClosed()
    {
        var output = await Handler(new()).HandleAsync(new(Identity, Handle));
        Assert.Contains("persistence_unavailable", output.Errors);
        Assert.Null(output.Data);
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenReading_ThenPropagateCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GetOfflinePolicyHandler(new Mock<IOfflinePolicyReadStore>(MockBehavior.Strict).Object, new Clock())
                .HandleAsync(new(Identity, Handle), cancellation.Token));
    }

    private static GetOfflinePolicyHandler Handler(OfflinePolicyReadResult result)
    {
        var store = new Mock<IOfflinePolicyReadStore>(MockBehavior.Strict);
        store.Setup(x => x.ReadAsync(Identity, "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", Now,
            It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return new(store.Object, new Clock());
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
