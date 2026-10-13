using ArturRios.Cerberus.Command.Offline;
using ArturRios.Cerberus.Domain.Offline;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class SetOfflinePolicyHandlerTests
{
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid Identity = Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");

    [UnitTheory]
    [InlineData("identity", "authentication_required")]
    [InlineData("missing", "vault_access_required")]
    [InlineData("malformed", "validation_failed")]
    [InlineData("revision", "validation_failed")]
    [InlineData("zero", "validation_failed")]
    [InlineData("negative", "validation_failed")]
    [InlineData("fraction", "validation_failed")]
    [InlineData("missingInterval", "validation_failed")]
    [InlineData("disabledInterval", "validation_failed")]
    [InlineData("overflow", "validation_failed")]
    public async Task GivenInvalidPolicyOrContext_WhenSetting_ThenRejectBeforePersistence(string invalid, string error)
    {
        var command = Command();
        command.SetContext(invalid == "identity" ? Guid.Empty : Identity,
            invalid == "missing" ? null : invalid == "malformed" ? "invalid" : Handle);
        if (invalid == "revision") command.ExpectedPolicyRevision = 0;
        if (invalid == "zero") command.RenewalInterval = TimeSpan.Zero;
        if (invalid == "negative") command.RenewalInterval = TimeSpan.FromSeconds(-1);
        if (invalid == "fraction") command.RenewalInterval = TimeSpan.FromMilliseconds(500);
        if (invalid == "missingInterval") command.RenewalInterval = null;
        if (invalid == "disabledInterval") command.RenewalEnabled = false;
        if (invalid == "overflow") command.RenewalInterval = TimeSpan.MaxValue;
        var output = await new SetOfflinePolicyHandler(new SetOfflinePolicyValidator(),
            new Mock<IOfflinePolicyUpdateStore>(MockBehavior.Strict).Object, new Clock()).HandleAsync(command);
        Assert.Contains(error, output.Errors);
        Assert.Null(output.Data);
    }

    [UnitTheory]
    [InlineData("not_found")]
    [InlineData("vault_access_denied")]
    [InlineData("revision_conflict")]
    [InlineData("persistence_unavailable")]
    public async Task GivenRejectedUpdate_WhenHandling_ThenReturnOnlyStableError(string error)
    {
        var output = await Handler(new(Error: error)).HandleAsync(Command());
        Assert.Contains(error, output.Errors);
        Assert.Null(output.Data);
    }

    [UnitTheory]
    [InlineData(true, 1)]
    [InlineData(true, 86400)]
    [InlineData(true, 123456)]
    [InlineData(false, 0)]
    public async Task GivenValidPolicy_WhenSetting_ThenReturnExactCommittedPolicy(bool enabled, long seconds)
    {
        var command = Command();
        command.RenewalEnabled = enabled;
        command.RenewalInterval = enabled ? TimeSpan.FromSeconds(seconds) : null;
        var store = new Mock<IOfflinePolicyUpdateStore>(MockBehavior.Strict);
        store.Setup(x => x.UpdateAsync(It.Is<OfflinePolicyUpdateRequest>(r => r.IdentityId == Identity
            && r.Verifier == "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925"
            && r.ExpectedPolicyRevision == 1 && r.RenewalEnabled == enabled && r.RenewalInterval == command.RenewalInterval),
            It.IsAny<CancellationToken>())).ReturnsAsync(new OfflinePolicyReadResult(new(enabled, command.RenewalInterval, 2, 3)));
        var output = await new SetOfflinePolicyHandler(new SetOfflinePolicyValidator(), store.Object, new Clock()).HandleAsync(command);
        Assert.True(output.Success);
        Assert.Equal(enabled, output.Data!.RenewalEnabled);
        Assert.Equal(command.RenewalInterval, output.Data.RenewalInterval);
        Assert.Equal(2, output.Data.PolicyRevision);
        Assert.Equal(3, output.Data.RevocationGeneration);
    }

    [UnitTheory]
    [InlineData("empty")]
    [InlineData("revision")]
    [InlineData("generation")]
    [InlineData("enabled")]
    [InlineData("interval")]
    public async Task GivenIncompleteOrMismatchedStoreOutcome_WhenHandling_ThenFailClosed(string invalid)
    {
        var result = new OfflinePolicyReadResult(invalid == "empty" ? null : new(
            invalid != "enabled", invalid == "interval" ? TimeSpan.FromHours(2) : TimeSpan.FromHours(24),
            invalid == "revision" ? 3 : 2, invalid == "generation" ? 0 : 3));
        var output = await Handler(result).HandleAsync(Command());
        Assert.Contains("persistence_unavailable", output.Errors);
        Assert.Null(output.Data);
    }

    [UnitFact]
    public async Task GivenUnsafeReturnedPolicyCounter_WhenSetting_ThenFailClosed()
    {
        var command = Command();
        command.ExpectedPolicyRevision = 9007199254740991;
        var output = await Handler(new(new(true, TimeSpan.FromHours(24), 9007199254740992, 3))).HandleAsync(command);
        Assert.Contains("persistence_unavailable", output.Errors);
        Assert.Null(output.Data);
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenSetting_ThenPropagateCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(new()).HandleAsync(Command(), cancellation.Token));
    }

    private static SetOfflinePolicyCommand Command()
    {
        var command = new SetOfflinePolicyCommand { ExpectedPolicyRevision = 1, RenewalEnabled = true, RenewalInterval = TimeSpan.FromHours(24) };
        command.SetContext(Identity, Handle);
        return command;
    }
    private static SetOfflinePolicyHandler Handler(OfflinePolicyReadResult result)
    {
        var store = new Mock<IOfflinePolicyUpdateStore>(MockBehavior.Strict);
        store.Setup(x => x.UpdateAsync(It.IsAny<OfflinePolicyUpdateRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return new(new SetOfflinePolicyValidator(), store.Object, new Clock());
    }
    private sealed class Clock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 13, 12, 0, 0, TimeSpan.Zero);
    }
}
