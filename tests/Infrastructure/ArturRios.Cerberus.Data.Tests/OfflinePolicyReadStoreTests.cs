using System.Security.Cryptography;
using ArturRios.Cerberus.Data.Offline;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class OfflinePolicyReadStoreTests(PostgresFixture fixture)
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
    [InlineData("profileOnly", null)]
    [InlineData("foreignProfile", "vault_access_denied")]
    [InlineData("missingProfile", "vault_access_denied")]
    [InlineData("deletedProfile", "vault_access_denied")]
    [InlineData("erasedProfile", "vault_access_denied")]
    [InlineData("expired", "vault_access_denied")]
    [InlineData("expiryEquality", "vault_access_denied")]
    [InlineData("futureIssue", "vault_access_denied")]
    [InlineData("missingExpiry", "vault_access_denied")]
    [InlineData("disabledWithExpiry", "vault_access_denied")]
    [InlineData("revoked", "vault_access_denied")]
    [InlineData("stalePolicy", "vault_access_denied")]
    [InlineData("zeroPolicy", "vault_access_denied")]
    [InlineData("zeroGeneration", "vault_access_denied")]
    [InlineData("staleGeneration", "vault_access_denied")]
    public async Task GivenCurrentAccountAndSession_WhenReading_ThenReturnOnlyCurrentlyAuthorizedPolicy(string state, string? error)
    {
        var identity = Guid.NewGuid();
        var id = Guid.NewGuid();
        var verifier = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        await using (var setup = fixture.CreateContext())
        {
            var account = new Account { PublicId = id, HeimdallPublicId = identity, DetailsEnvelope = [1, 2, 3], Revision = 5,
                PolicyRevision = state == "stalePolicy" ? 2 : 1, RevocationGeneration = state == "staleGeneration" ? 2 : 1,
                State = state == "closing" ? AccountState.ClosurePending : AccountState.Active, RenewalEnabled = state is not ("noExpiry" or "disabledWithExpiry"),
                RenewalInterval = state is "noExpiry" or "disabledWithExpiry" ? null : TimeSpan.FromHours(24) };
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
            long? profileId = null;
            if (state is "profileOnly" or "foreignProfile" or "deletedProfile" or "erasedProfile")
            {
                var profileOwner = account.Id;
                if (state == "foreignProfile")
                {
                    var otherOwner = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid() };
                    setup.Accounts.Add(otherOwner);
                    await setup.SaveChangesAsync();
                    profileOwner = otherOwner.Id;
                }
                var profile = new ArturRios.Cerberus.Domain.Profiles.Profile { PublicId = Guid.NewGuid(), AccountId = profileOwner,
                    DeletedAt = state == "deletedProfile" ? Now : null, PurgeAt = state == "deletedProfile" ? Now.AddDays(30) : null };
                setup.Profiles.Add(profile);
                await setup.SaveChangesAsync();
                profileId = profile.Id;
                if (state == "erasedProfile") setup.TerminalErasures.Add(new TerminalErasure { ResourceId = profile.PublicId, ResourceKind = "profile", DeletedAt = Now });
            }
            if (state == "missingProfile") profileId = long.MaxValue;
            if (state != "missingSession") setup.VaultAccessSessions.Add(new VaultAccessSession
            {
                AccountId = sessionAccountId, HandleVerifier = verifier, ProfileId = profileId,
                IssuedAt = state == "futureIssue" ? Now.AddSeconds(1) : Now.AddMinutes(-5),
                ExpiresAt = state is "noExpiry" or "missingExpiry" ? null : state == "expiryEquality" ? Now : state == "expired" ? Now.AddSeconds(-1) : Now.AddMinutes(5),
                PolicyRevision = state == "zeroPolicy" ? 0 : 1, RevocationGeneration = state == "zeroGeneration" ? 0 : 1,
                Revoked = state == "revoked"
            });
            if (state == "erased") setup.TerminalErasures.Add(new TerminalErasure { ResourceId = id, ResourceKind = "account", DeletedAt = Now });
            await setup.SaveChangesAsync();
        }
        var result = await new OfflinePolicyReadStore(fixture).ReadAsync(state == "missingAccount" ? Guid.NewGuid() : identity, verifier, Now, default);
        Assert.Equal(error, result.Error);
        if (error is null)
        {
            Assert.Equal(state != "noExpiry", result.Policy!.RenewalEnabled);
            Assert.Equal(state == "noExpiry" ? null : (TimeSpan?)TimeSpan.FromHours(24), result.Policy.RenewalInterval);
            Assert.Equal(1, result.Policy.PolicyRevision);
            Assert.Equal(1, result.Policy.RevocationGeneration);
        }
        else Assert.Null(result.Policy);
        await using var after = fixture.CreateContext();
        var unchanged = await after.Accounts.SingleAsync(x => x.PublicId == id);
        Assert.Equal(5, unchanged.Revision);
        Assert.Equal(new byte[] { 1, 2, 3 }, unchanged.DetailsEnvelope);
    }

    [FunctionalFact]
    public async Task GivenUnavailableDatabase_WhenReading_ThenReturn503ErrorWithoutPolicy()
    {
        var result = await new OfflinePolicyReadStore(new UnavailableFactory()).ReadAsync(Guid.NewGuid(), new string('a', 64), Now, default);
        Assert.Equal("persistence_unavailable", result.Error);
        Assert.Null(result.Policy);
    }

    [FunctionalFact]
    public async Task GivenCancelledCaller_WhenReading_ThenPropagateCancellation()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OfflinePolicyReadStore(fixture).ReadAsync(Guid.NewGuid(), new string('a', 64), Now, cancelled.Token));
    }

    private sealed class UnavailableFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=fixture;Username=fixture;Password=fixture;Timeout=1").Options);
    }
}
