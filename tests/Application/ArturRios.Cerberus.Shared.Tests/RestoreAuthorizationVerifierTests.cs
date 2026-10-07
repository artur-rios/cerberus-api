using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.Shared.Operations;
using Moq;

namespace ArturRios.Cerberus.Shared.Tests;

public sealed class RestoreAuthorizationVerifierTests
{
    [UnitTheory]
    [InlineData(false, true, false)]
    [InlineData(true, false, false)]
    [InlineData(true, true, true)]
    public async Task GivenScopeAndModuleVerification_WhenReconciling_ThenRequireBoth(bool scope, bool module, bool accepted)
    {
        var upstream = new Mock<IHeimdallClient>(MockBehavior.Strict);
        upstream.Setup(x => x.VerifyScopeAsync(It.IsAny<CancellationToken>())).ReturnsAsync(scope);
        var verifier = new RestoreAuthorizationVerifier(upstream.Object, [new ModuleFixture(module)]);
        Assert.Equal(accepted, await verifier.VerifyAsync(default));
    }

    private sealed class ModuleFixture(bool valid) : IRestoreVerificationStep
    {
        public Task<bool> VerifyAsync(CancellationToken cancellationToken) => Task.FromResult(valid);
    }
}
