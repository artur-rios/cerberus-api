using ArturRios.Cerberus.Command.Authentication;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Output;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class AuthenticationTests
{
    private static readonly Guid IdentityId = Guid.NewGuid();
    private static readonly HeimdallLogin Completed = new("signed-fixture-token", DateTimeOffset.UtcNow.AddHours(1), true, false, null, null);
    private static readonly HeimdallLogin Pending = new(null, null, null, true, "fixture-challenge", ["App"]);

    [UnitTheory]
    [InlineData("email")]
    [InlineData("password")]
    [InlineData("challenge")]
    [InlineData("noFactor")]
    [InlineData("bothFactors")]
    [InlineData("blankFactor")]
    public async Task GivenInvalidInput_WhenHandlingAuthentication_ThenRejectBeforeDependencyOrStore(string invalid)
    {
        var handler = Handler(new Mock<IHeimdallClient>(MockBehavior.Strict).Object, new Mock<IAccountAuthenticationStore>(MockBehavior.Strict).Object);
        DataOutput<AuthenticationOutput?> result;
        if (invalid is "email" or "password")
            result = await handler.HandleAsync(new LoginCommand { Email = invalid == "email" ? "bad" : "fixture@example.test", Password = invalid == "password" ? "" : "fixture-password" });
        else
            result = await handler.HandleAsync(new VerifyChallengeCommand
            {
                ChallengeToken = invalid == "challenge" ? "" : "fixture-challenge",
                Code = invalid == "noFactor" ? null : invalid == "blankFactor" ? " " : "123456",
                RecoveryCode = invalid == "bothFactors" ? "fixture-recovery" : null
            });
        Assert.False(result.Success);
        Assert.Contains("validation_failed", result.Errors);
        Assert.Null(result.Data);
    }

    [UnitTheory]
    [InlineData(false, "authentication_required")]
    [InlineData(true, "authentication_required")]
    [InlineData(false, "authentication_forbidden")]
    [InlineData(true, "authentication_forbidden")]
    [InlineData(false, "identity_unavailable")]
    [InlineData(true, "identity_unavailable")]
    public async Task GivenIdentityFailure_WhenHandling_ThenDoNotLookupAccount(bool challenge, string error)
    {
        var result = await Handle(challenge, new HeimdallAuthentication(Error: error), new Mock<IAccountAuthenticationStore>(MockBehavior.Strict));
        Assert.False(result.Success);
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
    }

    [UnitFact]
    public async Task GivenPendingMfa_WhenHandling_ThenReturnOnlyChallengeWithoutAccountLookup()
    {
        var result = await Handle(false, new HeimdallAuthentication(Pending), new Mock<IAccountAuthenticationStore>(MockBehavior.Strict));
        Assert.True(result.Success);
        Assert.Equal(Pending, result.Data!.Identity);
        Assert.Null(result.Data.Account);
        Assert.Contains("authentication_challenge_required", result.Messages);
    }

    [UnitTheory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task GivenCompletedAuthentication_WhenHandling_ThenReturnOnlyPermittedPublicAccountContext(bool challenge, bool bound)
    {
        var account = bound ? new AuthenticationAccount(Guid.NewGuid(), 3) : null;
        var store = Store(new AccountAuthenticationResult(account));
        var result = await Handle(challenge, new HeimdallAuthentication(Completed, IdentityId), store);
        Assert.True(result.Success);
        Assert.Equal(Completed, result.Data!.Identity);
        Assert.Equal(account, result.Data.Account);
        store.Verify(x => x.FindAsync(IdentityId, It.IsAny<CancellationToken>()), Times.Once);
    }

    [UnitTheory]
    [InlineData(false, "authentication_required")]
    [InlineData(true, "authentication_required")]
    [InlineData(false, "persistence_unavailable")]
    [InlineData(true, "persistence_unavailable")]
    public async Task GivenInactiveAccountOrPersistenceFailure_WhenHandling_ThenReturnNoIdentityBearer(bool challenge, string error)
    {
        var result = await Handle(challenge, new HeimdallAuthentication(Completed, IdentityId), Store(new AccountAuthenticationResult(Error: error)));
        Assert.False(result.Success);
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenIncompleteTypedAdapterResult_WhenHandling_ThenFailClosed(bool missingLogin)
    {
        var result = await Handle(false, new HeimdallAuthentication(missingLogin ? null : Completed), new Mock<IAccountAuthenticationStore>(MockBehavior.Strict));
        Assert.Contains("identity_unavailable", result.Errors);
        Assert.Null(result.Data);
    }

    private static Mock<IAccountAuthenticationStore> Store(AccountAuthenticationResult result)
    {
        var store = new Mock<IAccountAuthenticationStore>(MockBehavior.Strict);
        store.Setup(x => x.FindAsync(IdentityId, It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return store;
    }
    private static Task<DataOutput<AuthenticationOutput?>> Handle(bool challenge, HeimdallAuthentication result, Mock<IAccountAuthenticationStore> store)
    {
        var identity = new Mock<IHeimdallClient>(MockBehavior.Strict);
        if (challenge) identity.Setup(x => x.VerifyChallengeAsync("fixture-challenge", null, "fixture-recovery", It.IsAny<CancellationToken>())).ReturnsAsync(result);
        else identity.Setup(x => x.AuthenticateAsync("fixture@example.test", "fixture-password", It.IsAny<CancellationToken>())).ReturnsAsync(result);
        var handler = Handler(identity.Object, store.Object);
        return challenge ? handler.HandleAsync(new VerifyChallengeCommand { ChallengeToken = "fixture-challenge", RecoveryCode = "fixture-recovery" })
            : handler.HandleAsync(new LoginCommand { Email = "fixture@example.test", Password = "fixture-password" });
    }
    private static AuthenticationHandler Handler(IHeimdallClient identity, IAccountAuthenticationStore store) =>
        new(new LoginValidator(), new VerifyChallengeValidator(), identity, store);
}
