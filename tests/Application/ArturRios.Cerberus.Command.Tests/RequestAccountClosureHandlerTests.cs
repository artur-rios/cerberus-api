using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class RequestAccountClosureHandlerTests
{
    private static readonly Guid Actor = Guid.NewGuid(), Account = Guid.NewGuid();
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1800000000);
    private static RequestAccountClosureCommand Command()
    { var c = new RequestAccountClosureCommand { ExpectedRevision = 1 }; c.SetContext(Actor, Now.ToUnixTimeSeconds()); return c; }
    [UnitTheory]
    [InlineData("actor", "authentication_required")][InlineData("old", "fresh_authentication_required")]
    [InlineData("future", "fresh_authentication_required")][InlineData("revision", "validation_failed")]
    public async Task GivenInvalidClosureContext_WhenRequesting_ThenRejectBeforePersistence(string invalid, string error)
    {
        var c = Command(); c.SetContext(invalid == "actor" ? Guid.Empty : Actor,
            invalid == "old" ? Now.AddMinutes(-1).ToUnixTimeSeconds() : invalid == "future" ? Now.AddSeconds(1).ToUnixTimeSeconds() : Now.ToUnixTimeSeconds());
        if (invalid == "revision") c.ExpectedRevision = 0;
        var result = await new RequestAccountClosureHandler(new Mock<IAccountClosureStore>(MockBehavior.Strict).Object, new Clock()).HandleAsync(c);
        Assert.Contains(error, result.Errors); Assert.Null(result.Data);
    }
    [UnitTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenCommittedOrOriginalPendingDeadline_WhenHandling_ThenReturnOnlyValidatedMetadata(bool replay)
    {
        var details = Details(replay); var result = await Handler(new(details)).HandleAsync(Command());
        Assert.True(result.Success); Assert.Equal(Account, result.Data!.AccountId); Assert.Equal(details.ClosureDeadline, result.Data.ClosureDeadline);
        Assert.Equal(replay, result.Data.AlreadyPending); Assert.Equal(details.Revision, result.Data.Revision);
        Assert.DoesNotContain(result.Data.GetType().GetProperties(), p => p.Name.Contains("Envelope", StringComparison.Ordinal) || p.Name.Contains("Token", StringComparison.Ordinal));
    }
    [UnitTheory]
    [InlineData("not_found")][InlineData("revision_conflict")][InlineData("fresh_authentication_required")]
    [InlineData("persistence_unavailable")]
    public async Task GivenSafePersistenceDenial_WhenClosing_ThenReturnErrorWithoutDeadline(string error)
    { var result = await Handler(new(Error: error)).HandleAsync(Command()); Assert.Contains(error, result.Errors); Assert.Null(result.Data); }

    [UnitTheory]
    [InlineData("missing")][InlineData("identifier")][InlineData("revision")][InlineData("generation")]
    [InlineData("deadline")][InlineData("clock")][InlineData("unknown")][InlineData("error-data")]
    public async Task GivenInconsistentStoredClosure_WhenHandling_ThenFailClosed(string invalid)
    {
        var d = Details(false);
        d = invalid switch { "identifier" => d with { AccountId = Guid.Empty }, "revision" => d with { Revision = 3 },
            "generation" => d with { RevocationGeneration = 0 }, "deadline" => d with { ClosureDeadline = Now.AddDays(31) },
            "clock" => d with { ServerTime = Now.AddTicks(1) }, _ => d };
        var r = invalid == "missing" ? new AccountClosureResult() : invalid == "unknown" ? new(Error: "internal_failure")
            : new(d, invalid == "error-data" ? "not_found" : null);
        var result = await Handler(r).HandleAsync(Command()); Assert.Contains("persistence_unavailable", result.Errors); Assert.Null(result.Data);
    }
    [UnitFact]
    public async Task GivenCancelledCaller_WhenClosing_ThenPropagateWithoutWriting()
    { using var ct = new CancellationTokenSource(); ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(new()).HandleAsync(Command(), ct.Token)); }

    private static AccountClosureDetails Details(bool replay) => new(Account, replay ? 8 : 2, 2, Now, Now.AddDays(30), Now, replay);
    private static RequestAccountClosureHandler Handler(AccountClosureResult result)
    {
        var store = new Mock<IAccountClosureStore>(MockBehavior.Strict);
        store.Setup(x => x.RequestAsync(It.Is<AccountClosureRequest>(r => r.Actor == Actor && r.ExpectedRevision == 1
            && r.AuthenticationIssuedAt == Now.ToUnixTimeSeconds()), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return new(store.Object, new Clock());
    }
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
