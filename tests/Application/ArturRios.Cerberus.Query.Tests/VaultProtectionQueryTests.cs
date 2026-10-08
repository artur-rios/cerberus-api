using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Query.Protection;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Query.Tests;

public class VaultProtectionQueryTests
{
    [UnitTheory]
    [InlineData("valid")][InlineData("actor")][InlineData("missing")][InlineData("invalid")][InlineData("not_found")][InlineData("persistence_unavailable")]
    public async Task GivenOwnProtectionQuery_WhenHandling_ThenReturnOnlyValidOwnMaterial(string state)
    {
        using var client = new ProtectionFixture();var actor = Guid.NewGuid();var id = Guid.NewGuid();
        var store = new Mock<IVaultProtectionStore>(MockBehavior.Strict);
        if (state != "actor") store.Setup(x => x.ReadAsync(actor,It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<VaultProtectionDetails>(
            state == "missing" ? null : new(id,1,1,1,state == "invalid" ? client.Material with { UnlockVerifier = null! } : client.Material),
            state is "not_found" or "persistence_unavailable" ? state : null));
        var result = await new GetVaultProtectionHandler(store.Object).HandleAsync(new GetVaultProtectionQuery(state == "actor" ? Guid.Empty : actor));
        if (state == "valid") { Assert.True(result.Success);Assert.Equal(client.Material,result.Data!.Material);Assert.Equal(id,result.Data.AccountId); }
        else { Assert.False(result.Success);Assert.Null(result.Data);Assert.Contains(state == "actor" ? "authentication_required" : state == "not_found" ? state : "persistence_unavailable",result.Errors); }
    }
}
