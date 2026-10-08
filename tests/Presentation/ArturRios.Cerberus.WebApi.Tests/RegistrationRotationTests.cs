using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Data;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using Microsoft.EntityFrameworkCore;
using Moq;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class RegistrationRotationTests(RegistrationApiFixture fixture)
{
    [FunctionalFact]
    public async Task GivenPendingAndCompletedRegistrations_WhenRestartingWithRotatedJwtKey_ThenReconcileAndReplay()
    {
        var command = new RegisterAccountCommand
        {
            AccountId = Guid.NewGuid(), IdempotencyKey = Guid.NewGuid(),
            Identity = new("Fixture Owner", Guid.NewGuid().ToString("N") + "@example.test", "fixture-password"),
            Details = new("cerberus-content-v1", 1, "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA", "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA")
        };
        var identityId = Guid.NewGuid();
        var identity = new Mock<IHeimdallClient>(MockBehavior.Strict);
        identity.SetupSequence(x => x.EstablishRegistrationIdentityAsync(command.Identity, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new RegistrationIdentity(RegistrationIdentityStatus.Unavailable))
            .ReturnsAsync(new RegistrationIdentity(RegistrationIdentityStatus.Verified, identityId))
            .ReturnsAsync(new RegistrationIdentity(RegistrationIdentityStatus.Verified, identityId));

        async Task<ArturRios.Output.DataOutput<RegisterAccountOutput?>> Attempt(string signingKey)
        {
            var factory = new Mock<IDbContextFactory<AppDbContext>>();
            factory.Setup(x => x.CreateDbContextAsync(It.IsAny<CancellationToken>())).ReturnsAsync(() => fixture.Context());
            var options = new CerberusOptions
            {
                AuthValidationSecret = signingKey, HeimdallScopeId = RegistrationApiFixture.Scope,
                RegistrationFingerprintKey = "independent-durable-fixture-key-32-bytes"
            };
            return await new RegisterAccountHandler(new RegisterAccountValidator(), new RegistrationStore(factory.Object), identity.Object, options)
                .HandleAsync(command);
        }

        var pending = await Attempt("initial-fixture-jwt-signing-secret-32-bytes");
        Assert.Contains("identity_unavailable", pending.Errors);
        var created = await Attempt("rotated-fixture-jwt-signing-secret-32-bytes");
        Assert.True(created.Success);
        Assert.False(created.Data!.Replayed);
        var replay = await Attempt("rotated-again-jwt-signing-secret-32-bytes");
        Assert.True(replay.Success);
        Assert.True(replay.Data!.Replayed);
        Assert.Equal(command.AccountId, replay.Data.Id);
        identity.Verify(x => x.EstablishRegistrationIdentityAsync(command.Identity, null, It.IsAny<CancellationToken>()), Times.Exactly(3));
        await using var after = fixture.Context();
        Assert.Equal(1, await after.Accounts.CountAsync(x => x.PublicId == command.AccountId));
    }
}
