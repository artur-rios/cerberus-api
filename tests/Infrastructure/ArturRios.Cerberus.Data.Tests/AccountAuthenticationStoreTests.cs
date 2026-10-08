using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class AccountAuthenticationStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory]
    [InlineData("active", null)]
    [InlineData("closing", "authentication_required")]
    [InlineData("erased", "authentication_required")]
    public async Task GivenCurrentAccount_WhenResolvingAuthentication_ThenReturnOnlyPermittedPublicContext(string state, string? expected)
    {
        var identity = Guid.NewGuid();
        var accountId = Guid.NewGuid();
        await using (var setup = fixture.CreateContext())
        {
            setup.Accounts.Add(new Account { PublicId = accountId, HeimdallPublicId = identity, DetailsEnvelope = [1, 2, 3], Revision = 7,
                State = state == "closing" ? AccountState.ClosurePending : AccountState.Active });
            if (state == "erased") setup.TerminalErasures.Add(new TerminalErasure { ResourceId = accountId, ResourceKind = "account", DeletedAt = DateTimeOffset.UtcNow });
            await setup.SaveChangesAsync();
        }
        var result = await new AccountAuthenticationStore(fixture).FindAsync(identity, default);
        Assert.Equal(expected, result.Error);
        if (expected is null)
        {
            Assert.Equal(accountId, result.Account!.Id);
            Assert.Equal(7, result.Account.Revision);
        }
        else Assert.Null(result.Account);
        await using var after = fixture.CreateContext();
        Assert.Equal(7, (await after.Accounts.SingleAsync(x => x.PublicId == accountId)).Revision);
    }

    [FunctionalFact]
    public async Task GivenNoAccountForScopedIdentity_WhenResolvingAuthentication_ThenReturnNoAccountRightsOrWrites()
    {
        var identity = Guid.NewGuid();
        var result = await new AccountAuthenticationStore(fixture).FindAsync(identity, default);
        Assert.Null(result.Error);
        Assert.Null(result.Account);
        await using var after = fixture.CreateContext();
        Assert.False(await after.Accounts.AnyAsync(x => x.HeimdallPublicId == identity));
    }

    [FunctionalFact]
    public async Task GivenUnavailablePostgreSql_WhenResolvingAuthentication_ThenFailClosedWithoutContext()
    {
        var result = await new AccountAuthenticationStore(new UnavailableFactory()).FindAsync(Guid.NewGuid(), default);
        Assert.Equal("persistence_unavailable", result.Error);
        Assert.Null(result.Account);
    }

    [FunctionalFact]
    public async Task GivenCancelledCaller_WhenResolvingAuthentication_ThenPropagateCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AccountAuthenticationStore(fixture).FindAsync(Guid.NewGuid(), cancelled.Token));
    }

    private sealed class UnavailableFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql(new NpgsqlConnectionStringBuilder { Host = "127.0.0.1", Port = 1, Database = "fixture", Username = "fixture", Password = "fixture", Timeout = 1 }.ConnectionString).Options);
    }
}
