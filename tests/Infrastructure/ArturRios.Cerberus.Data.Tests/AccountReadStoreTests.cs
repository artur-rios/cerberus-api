using System.Security.Cryptography;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class AccountReadStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [FunctionalTheory]
    [InlineData("active", null)]
    [InlineData("noExpiry", null)]
    [InlineData("missingAccount", "not_found")]
    [InlineData("closing", "not_found")]
    [InlineData("erased", "not_found")]
    [InlineData("missingSession", "vault_access_denied")]
    [InlineData("otherAccount", "vault_access_denied")]
    [InlineData("profileOnly", "vault_access_denied")]
    [InlineData("expired", "vault_access_denied")]
    [InlineData("expiryEquality", "vault_access_denied")]
    [InlineData("futureIssue", "vault_access_denied")]
    [InlineData("missingExpiry", "vault_access_denied")]
    [InlineData("disabledWithExpiry", "vault_access_denied")]
    [InlineData("revoked", "vault_access_denied")]
    [InlineData("stalePolicy", "vault_access_denied")]
    [InlineData("staleGeneration", "vault_access_denied")]
    public async Task GivenCurrentAccountAndSession_WhenReading_ThenReturnOnlyCurrentlyAuthorizedCiphertext(string state, string? error)
    {
        var identity = Guid.NewGuid();
        var id = Guid.NewGuid();
        var verifier = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        await using (var setup = fixture.CreateContext())
        {
            var account = new Account { PublicId = id, HeimdallPublicId = identity, DetailsEnvelope = [1, 2, 3], Revision = 5,
                State = state == "closing" ? AccountState.ClosurePending : AccountState.Active, RenewalEnabled = state is not ("noExpiry" or "disabledWithExpiry") };
            setup.Accounts.Add(account);
            await setup.SaveChangesAsync();
            var sessionAccountId = account.Id;
            if (state == "otherAccount")
            {
                var other = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid(), DetailsEnvelope = [9, 9] };
                setup.Accounts.Add(other);
                await setup.SaveChangesAsync();
                sessionAccountId = other.Id;
            }
            if (state != "missingSession") setup.VaultAccessSessions.Add(new VaultAccessSession
            {
                AccountId = sessionAccountId, HandleVerifier = verifier, ProfileId = state == "profileOnly" ? 42 : null,
                IssuedAt = state == "futureIssue" ? Now.AddSeconds(1) : Now.AddMinutes(-5),
                ExpiresAt = state is "noExpiry" or "missingExpiry" ? null : state == "expiryEquality" ? Now : state == "expired" ? Now.AddSeconds(-1) : Now.AddMinutes(5),
                PolicyRevision = state == "stalePolicy" ? 0 : 1, RevocationGeneration = state == "staleGeneration" ? 0 : 1,
                Revoked = state == "revoked"
            });
            if (state == "erased") setup.TerminalErasures.Add(new TerminalErasure { ResourceId = id, ResourceKind = "account", DeletedAt = Now });
            await setup.SaveChangesAsync();
        }
        var result = await new AccountReadStore(fixture).ReadAsync(state == "missingAccount" ? Guid.NewGuid() : identity, verifier, Now, default);
        Assert.Equal(error, result.Error);
        if (error is null)
        {
            Assert.Equal(id, result.Account!.Id);
            Assert.Equal(5, result.Account.Revision);
            Assert.Equal(new byte[] { 1, 2, 3 }, result.Account.DetailsEnvelope);
        }
        else Assert.Null(result.Account);
        await using var after = fixture.CreateContext();
        var unchanged = await after.Accounts.SingleAsync(x => x.PublicId == id);
        Assert.Equal(5, unchanged.Revision);
        Assert.Equal(new byte[] { 1, 2, 3 }, unchanged.DetailsEnvelope);
    }

    [FunctionalFact]
    public async Task GivenUnavailableDatabase_WhenReading_ThenReturn503ErrorWithoutCiphertext()
    {
        var result = await new AccountReadStore(new UnavailableFactory()).ReadAsync(Guid.NewGuid(), new string('a', 64), Now, default);
        Assert.Equal("persistence_unavailable", result.Error);
        Assert.Null(result.Account);
    }

    [FunctionalFact]
    public async Task GivenCancelledCaller_WhenReading_ThenPropagateCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AccountReadStore(fixture).ReadAsync(Guid.NewGuid(), new string('a', 64), Now, cancelled.Token));
    }

    private sealed class UnavailableFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=fixture;Username=fixture;Password=fixture;Timeout=1").Options);
    }
}
