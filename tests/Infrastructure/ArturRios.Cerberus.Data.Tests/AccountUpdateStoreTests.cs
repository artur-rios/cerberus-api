using System.Data.Common;
using System.Security.Cryptography;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class AccountUpdateStoreTests(PostgresFixture fixture)
{
    private static readonly DateTimeOffset Now = new(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);

    [FunctionalTheory]
    [InlineData("active", null)]
    [InlineData("noExpiry", null)]
    [InlineData("missing", "not_found")]
    [InlineData("closing", "not_found")]
    [InlineData("erased", "not_found")]
    [InlineData("missingSession", "vault_access_denied")]
    [InlineData("otherAccount", "vault_access_denied")]
    [InlineData("profile", "vault_access_denied")]
    [InlineData("expired", "vault_access_denied")]
    [InlineData("expiryEquality", "vault_access_denied")]
    [InlineData("futureIssue", "vault_access_denied")]
    [InlineData("missingExpiry", "vault_access_denied")]
    [InlineData("disabledWithExpiry", "vault_access_denied")]
    [InlineData("revoked", "vault_access_denied")]
    [InlineData("stalePolicy", "vault_access_denied")]
    [InlineData("staleGeneration", "vault_access_denied")]
    [InlineData("staleRevision", "revision_conflict")]
    [InlineData("exhausted", "revision_conflict")]
    public async Task GivenAccountAndCurrentAccess_WhenUpdating_ThenOnlyAuthorizedMatchingRevisionChanges(string state, string? error)
    {
        var input = await Setup(state);
        var result = await new AccountUpdateStore(fixture).UpdateAsync(input.Request, default);
        Assert.Equal(error, result.Error);
        if (error is null) { Assert.Equal(input.AccountId, result.Id); Assert.Equal(4, result.Revision); }
        else Assert.Null(result.Id);
        await using var after = fixture.CreateContext();
        var account = await after.Accounts.SingleAsync(x => x.PublicId == input.AccountId);
        Assert.Equal(error is null ? new byte[] { 7, 8, 9 } : new byte[] { 1, 2, 3 }, account.DetailsEnvelope);
        Assert.Equal(state == "exhausted" ? long.MaxValue : error is null ? 4 : 3, account.Revision);
        Assert.Equal(input.Identity, account.HeimdallPublicId);
        Assert.Equal(state == "closing" ? AccountState.ClosurePending : AccountState.Active, account.State);
        Assert.Equal(1, account.PolicyRevision);
        Assert.Equal(1, account.RevocationGeneration);
        Assert.Equal(state is not ("noExpiry" or "disabledWithExpiry"), account.RenewalEnabled);
        if (state != "missingSession")
        {
            var session = await after.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == input.Request.Verifier);
            Assert.Equal(state == "revoked", session.Revoked);
            Assert.Equal(state == "profile" ? 42 : (long?)null, session.ProfileId);
        }
    }

    [FunctionalFact]
    public async Task GivenTwoConcurrentEditsWithSameRevision_WhenUpdating_ThenOnlyOneEnvelopeWins()
    {
        var input = await Setup();
        var store = new AccountUpdateStore(fixture);
        var other = input.Request with { DetailsEnvelope = [9, 8, 7] };
        var results = await Task.WhenAll(store.UpdateAsync(input.Request, default), store.UpdateAsync(other, default));
        Assert.Single(results, x => x.Error is null);
        Assert.Single(results, x => x.Error == "revision_conflict");
        var winner = results[0].Error is null ? input.Request : other;
        await using var after = fixture.CreateContext();
        var account = await after.Accounts.SingleAsync(x => x.PublicId == input.AccountId);
        Assert.Equal(4, account.Revision);
        Assert.Equal(winner.DetailsEnvelope, account.DetailsEnvelope);
        Assert.Equal(input.Identity, account.HeimdallPublicId);
        var replay = await store.UpdateAsync(winner, default);
        Assert.Equal("revision_conflict", replay.Error);
    }

    [FunctionalTheory]
    [InlineData("closure")]
    [InlineData("revocation")]
    [InlineData("erasure")]
    [InlineData("policy")]
    [InlineData("expiry")]
    public async Task GivenAccessChangesBeforeConditionalWrite_WhenUpdating_ThenPreserveWinningState(string change)
    {
        var input = await Setup();
        var intercepted = false;
        var interceptor = new BeforeUpdate(async () =>
        {
            intercepted = true;
            await using var competing = fixture.CreateContext();
            var account = await competing.Accounts.SingleAsync(x => x.PublicId == input.AccountId);
            var session = await competing.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == input.Request.Verifier);
            if (change == "closure") { account.State = AccountState.ClosurePending; account.Revision++; }
            if (change == "revocation") session.Revoked = true;
            if (change == "policy") account.PolicyRevision++;
            if (change == "expiry") session.ExpiresAt = Now;
            if (change == "erasure") competing.TerminalErasures.Add(new TerminalErasure { ResourceId = input.AccountId, ResourceKind = "account", DeletedAt = Now });
            await competing.SaveChangesAsync();
        });
        var result = await new AccountUpdateStore(new InterceptingFactory(fixture, interceptor)).UpdateAsync(input.Request, default);
        Assert.True(intercepted);
        Assert.Equal("revision_conflict", result.Error);
        Assert.Null(result.Id);
        await using var after = fixture.CreateContext();
        var preserved = await after.Accounts.SingleAsync(x => x.PublicId == input.AccountId);
        Assert.Equal(new byte[] { 1, 2, 3 }, preserved.DetailsEnvelope);
        Assert.Equal(change == "closure" ? 4 : 3, preserved.Revision);
        Assert.Equal(change == "closure" ? AccountState.ClosurePending : AccountState.Active, preserved.State);
        Assert.Equal(change == "policy" ? 2 : 1, preserved.PolicyRevision);
        Assert.Equal(input.Identity, preserved.HeimdallPublicId);
        Assert.Equal(change == "revocation", (await after.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == input.Request.Verifier)).Revoked);
        Assert.Equal(change == "erasure", await after.TerminalErasures.AnyAsync(x => x.ResourceId == input.AccountId));
    }

    [FunctionalFact]
    public async Task GivenSessionExpiresAfterPreflight_WhenUpdating_ThenRejectAtStatementTime()
    {
        var input = await Setup();
        // The preflight uses a valid captured instant. Pause only the final
        // statement until the unchanged session expires, then execute the UPDATE.
        var captured = DateTimeOffset.UtcNow;
        var expires = new DateTimeOffset(captured.AddSeconds(1).Ticks / 10 * 10, TimeSpan.Zero);
        await using (var setup = fixture.CreateContext())
        {
            var session = await setup.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == input.Request.Verifier);
            session.IssuedAt = captured.AddMinutes(-10);
            session.ExpiresAt = expires;
            await setup.SaveChangesAsync();
        }
        var paused = false;
        var before = new BeforeUpdate(async () =>
        {
            paused = true;
            for (var remaining = expires - DateTimeOffset.UtcNow; remaining > TimeSpan.Zero; remaining = expires - DateTimeOffset.UtcNow)
                await Task.Delay(remaining + TimeSpan.FromMilliseconds(20));
        });
        var result = await new AccountUpdateStore(new InterceptingFactory(fixture, before))
            .UpdateAsync(input.Request with { Now = captured }, default);
        Assert.True(paused);
        Assert.Equal("revision_conflict", result.Error);
        Assert.Null(result.Id);
        await using var after = fixture.CreateContext();
        var account = await after.Accounts.SingleAsync(x => x.PublicId == input.AccountId);
        Assert.Equal(3, account.Revision);
        Assert.Equal(new byte[] { 1, 2, 3 }, account.DetailsEnvelope);
        Assert.Equal(expires, (await after.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == input.Request.Verifier)).ExpiresAt);
    }

    [FunctionalFact]
    public async Task GivenUnavailableDatabase_WhenUpdating_ThenReturnRetryableFailure()
    {
        var request = new AccountUpdateRequest(Guid.NewGuid(), new string('a', 64), 1, [7], Now);
        var result = await new AccountUpdateStore(new UnavailableFactory()).UpdateAsync(request, default);
        Assert.Equal("persistence_unavailable", result.Error);
        Assert.Null(result.Id);
    }

    [FunctionalFact]
    public async Task GivenCancelledCaller_WhenUpdating_ThenPropagateCancellationWithoutMutation()
    {
        var input = await Setup();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new AccountUpdateStore(fixture).UpdateAsync(input.Request, cancelled.Token));
        await using var after = fixture.CreateContext();
        Assert.Equal(3, (await after.Accounts.SingleAsync(x => x.PublicId == input.AccountId)).Revision);
    }

    private async Task<(Guid AccountId, Guid Identity, AccountUpdateRequest Request)> Setup(string state = "active")
    {
        var id = Guid.NewGuid();
        var identity = Guid.NewGuid();
        var verifier = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        await using var context = fixture.CreateContext();
        var account = new Account { PublicId = id, HeimdallPublicId = identity, Revision = state == "exhausted" ? long.MaxValue : 3,
            DetailsEnvelope = [1, 2, 3], State = state == "closing" ? AccountState.ClosurePending : AccountState.Active,
            RenewalEnabled = state is not ("noExpiry" or "disabledWithExpiry") };
        context.Accounts.Add(account);
        await context.SaveChangesAsync();
        var sessionOwner = account.Id;
        if (state == "otherAccount")
        {
            var other = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = Guid.NewGuid(), DetailsEnvelope = [4] };
            context.Accounts.Add(other);
            await context.SaveChangesAsync();
            sessionOwner = other.Id;
        }
        if (state != "missingSession") context.VaultAccessSessions.Add(new VaultAccessSession
        {
            AccountId = sessionOwner, HandleVerifier = verifier, ProfileId = state == "profile" ? 42 : null,
            IssuedAt = state == "futureIssue" ? Now.AddSeconds(1) : Now.AddMinutes(-5),
            ExpiresAt = state is "noExpiry" or "missingExpiry" ? null : state == "expiryEquality" ? Now : state == "expired" ? Now.AddSeconds(-1) : Now.AddMinutes(5),
            Revoked = state == "revoked", PolicyRevision = state == "stalePolicy" ? 0 : 1, RevocationGeneration = state == "staleGeneration" ? 0 : 1
        });
        if (state == "erased") context.TerminalErasures.Add(new TerminalErasure { ResourceId = id, ResourceKind = "account", DeletedAt = Now });
        await context.SaveChangesAsync();
        return (id, identity, new(state == "missing" ? Guid.NewGuid() : identity, verifier,
            state == "staleRevision" ? 2 : account.Revision, [7, 8, 9], Now));
    }

    private sealed class BeforeUpdate(Func<Task> action) : DbCommandInterceptor
    {
        private bool _ran;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!_ran && command.CommandText.TrimStart().StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase))
            { _ran = true; await action(); }
            return result;
        }
    }
    private sealed class InterceptingFactory(PostgresFixture fixture, DbCommandInterceptor interceptor) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            using var source = fixture.CreateContext();
            return new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(source.Database.GetConnectionString()).AddInterceptors(interceptor).Options);
        }
    }
    private sealed class UnavailableFactory : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext() => new(new DbContextOptionsBuilder<AppDbContext>()
            .UseNpgsql("Host=127.0.0.1;Port=1;Database=fixture;Username=fixture;Password=fixture;Timeout=1").Options);
    }
}
