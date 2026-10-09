using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class RegisterAccountHandlerTests
{
    [UnitFact]
    public async Task GivenValidRegistration_WhenHandling_ThenStoreOpaqueInputWithKeyedFingerprints()
    {
        var command = RegisterAccountValidatorTests.Valid();
        RegistrationRequest? captured = null;
        var store = new Mock<IRegistrationStore>(MockBehavior.Strict);
        store.Setup(x => x.RegisterAsync(It.IsAny<RegistrationRequest>(), It.IsAny<Func<CancellationToken, Task<RegistrationIdentity>>>(), It.IsAny<CancellationToken>()))
            .Returns<RegistrationRequest, Func<CancellationToken, Task<RegistrationIdentity>>, CancellationToken>((request, _, _) =>
            {
                captured = request;
                return Task.FromResult(new RegistrationResult(request.AccountId, 1));
            });
        var result = await Handler(store.Object).HandleAsync(command);
        Assert.True(result.Success);
        Assert.Equal(command.AccountId, result.Data!.Id);
        Assert.Equal("protection_required", result.Data.OnboardingState);
        Assert.Equal(1, result.Data.Revision);
        Assert.NotNull(captured);
        Assert.Equal(command.IdempotencyKey, captured.OperationId);
        Assert.DoesNotContain(command.Identity.Password, System.Text.Encoding.UTF8.GetString(captured.DetailsEnvelope));
        Assert.DoesNotContain(command.Identity.Email, captured.RequestFingerprint);
        Assert.Equal(64, captured.RequestFingerprint.Length);
    }

    [UnitFact]
    public async Task GivenInvalidInput_WhenHandling_ThenRejectBeforePersistence()
    {
        var command = RegisterAccountValidatorTests.Valid();
        command.AccountId = Guid.Empty;
        var result = await Handler(new Mock<IRegistrationStore>(MockBehavior.Strict).Object).HandleAsync(command);
        Assert.False(result.Success);
        Assert.Contains("validation_failed", result.Errors);
        Assert.Null(result.Data);
    }

    [UnitTheory]
    [InlineData("identity_proof_required")]
    [InlineData("registration_forbidden")]
    [InlineData("not_found")]
    [InlineData("registration_conflict")]
    [InlineData("identity_unavailable")]
    [InlineData("persistence_unavailable")]
    public async Task GivenRegistrationFailure_WhenHandling_ThenReturnStableErrorWithoutAccountData(string error)
    {
        var store = new Mock<IRegistrationStore>();
        store.Setup(x => x.RegisterAsync(It.IsAny<RegistrationRequest>(), It.IsAny<Func<CancellationToken, Task<RegistrationIdentity>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistrationResult(Error: error));
        var result = await Handler(store.Object).HandleAsync(RegisterAccountValidatorTests.Valid());
        Assert.False(result.Success);
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
    }

    [UnitFact]
    public async Task GivenCompletedRetry_WhenHandling_ThenReturnOriginalOutcomeAsReplay()
    {
        var command = RegisterAccountValidatorTests.Valid();
        var store = new Mock<IRegistrationStore>();
        store.Setup(x => x.RegisterAsync(It.IsAny<RegistrationRequest>(), It.IsAny<Func<CancellationToken, Task<RegistrationIdentity>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistrationResult(command.AccountId, 1, Replayed: true));
        var result = await Handler(store.Object).HandleAsync(command);
        Assert.True(result.Success);
        Assert.True(result.Data!.Replayed);
        Assert.Contains("account_registration_replayed", result.Messages);
    }

    [UnitFact]
    public async Task GivenChangedCredentialsOrServerKey_WhenFingerprinting_ThenRejectReplayEquivalence()
    {
        var command = RegisterAccountValidatorTests.Valid();
        var requests = new List<RegistrationRequest>();
        var store = new Mock<IRegistrationStore>();
        store.Setup(x => x.RegisterAsync(It.IsAny<RegistrationRequest>(), It.IsAny<Func<CancellationToken, Task<RegistrationIdentity>>>(), It.IsAny<CancellationToken>()))
            .Returns<RegistrationRequest, Func<CancellationToken, Task<RegistrationIdentity>>, CancellationToken>((request, _, _) =>
            {
                requests.Add(request);
                return Task.FromResult(new RegistrationResult(request.AccountId, 1));
            });
        await Handler(store.Object).HandleAsync(command);
        await Handler(store.Object).HandleAsync(command);
        Assert.Equal(requests[0].RequestFingerprint, requests[1].RequestFingerprint);
        command.Identity = command.Identity with { Password = "different-fixture-password" };
        await Handler(store.Object).HandleAsync(command);
        await Handler(store.Object, "another-server-secret-at-least-32-characters").HandleAsync(command);
        Assert.NotEqual(requests[0].RequestFingerprint, requests[2].RequestFingerprint);
        Assert.NotEqual(requests[2].RequestFingerprint, requests[3].RequestFingerprint);
    }

    private static RegisterAccountHandler Handler(IRegistrationStore store, string secret = "fixture-server-secret-at-least-32-characters") =>
        new(new RegisterAccountValidator(), store, new Mock<IHeimdallClient>(MockBehavior.Strict).Object,
            new CerberusOptions { RegistrationFingerprintKey = secret, HeimdallScopeId = Guid.Parse("527a1001-8ef5-4c9b-a565-111111111111") });
}
