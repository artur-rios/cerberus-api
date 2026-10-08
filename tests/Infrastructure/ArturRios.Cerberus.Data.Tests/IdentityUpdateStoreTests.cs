using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Identity;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Identity;
using ArturRios.Cerberus.Domain.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class IdentityUpdateStoreTests(PostgresFixture fixture)
{
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
    [InlineData("future", "vault_access_denied")]
    [InlineData("missingExpiry", "vault_access_denied")]
    [InlineData("disabledWithExpiry", "vault_access_denied")]
    [InlineData("revoked", "vault_access_denied")]
    [InlineData("stalePolicy", "vault_access_denied")]
    [InlineData("staleGeneration", "vault_access_denied")]
    public async Task GivenAccountAndAccess_WhenUpdatingIdentity_ThenOnlyCurrentAccountWideAccessInvokesCallback(string state, string? error)
    {
        var input = await Setup(state);
        var before = await Snapshot(input.Id, input.Verifier);
        var called = false;
        var result = await new IdentityUpdateStore(fixture).UpdateAsync(state == "missing" ? Guid.NewGuid() : input.Identity, input.Verifier,
            _ => { called = true; return Task.FromResult(Success(input.Identity)); }, default);
        Assert.Equal(error, result.Error);
        Assert.Equal(error is null, called);
        if (error is null) Assert.Equal(input.Identity, result.Identity!.Id);
        else Assert.Null(result.Identity);
        Assert.Equal(before, await Snapshot(input.Id, input.Verifier));
    }

    [FunctionalTheory]
    [InlineData("identity_conflict")]
    [InlineData("identity_unavailable")]
    [InlineData("authentication_required")]
    [InlineData("identity_update_forbidden")]
    [InlineData("not_found")]
    [InlineData("validation_failed")]
    public async Task GivenProviderFailure_WhenUpdatingIdentity_ThenPreserveAllLocalVaultState(string error)
    {
        var input = await Setup();
        var before = await Snapshot(input.Id, input.Verifier);
        var result = await new IdentityUpdateStore(fixture).UpdateAsync(input.Identity, input.Verifier,
            _ => Task.FromResult(new IdentityUpdateResult(Error: error)), default);
        Assert.Equal(error, result.Error);
        Assert.Null(result.Identity);
        Assert.Equal(before, await Snapshot(input.Id, input.Verifier));
    }

    [FunctionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenExternalCallbackInFlight_WhenClosingAccountOrRevokingSession_ThenLocksPreventInterleaving(bool session)
    {
        var input = await Setup();
        var before = await Snapshot(input.Id, input.Verifier);
        var result = await new IdentityUpdateStore(fixture).UpdateAsync(input.Identity, input.Verifier, async _ =>
        {
            await using var competing = fixture.CreateContext();
            await competing.Database.OpenConnectionAsync();
            await competing.Database.ExecuteSqlRawAsync("SET lock_timeout = '100ms'");
            var error = await Assert.ThrowsAsync<PostgresException>(() => session
                ? competing.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.vault_access_session SET revoked = TRUE WHERE handle_verifier = {input.Verifier}")
                : competing.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET state = 1 WHERE public_id = {input.Id}"));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, error.SqlState);
            return Success(input.Identity);
        }, default);
        Assert.Null(result.Error);
        Assert.Equal(before, await Snapshot(input.Id, input.Verifier));
        // Completion released the locks; the later lifecycle operation can now win.
        await using var after = fixture.CreateContext();
        Assert.Equal(1, await (session
            ? after.VaultAccessSessions.Where(x => x.HandleVerifier == input.Verifier).ExecuteUpdateAsync(s => s.SetProperty(x => x.Revoked, true))
            : after.Accounts.Where(x => x.PublicId == input.Id).ExecuteUpdateAsync(s => s.SetProperty(x => x.State, AccountState.ClosurePending))));
    }

    [FunctionalTheory]
    [InlineData("closure", "not_found")]
    [InlineData("erasure", "not_found")]
    [InlineData("revocation", "vault_access_denied")]
    [InlineData("policy", "vault_access_denied")]
    public async Task GivenStateChangesBeforeLocks_WhenUpdatingIdentity_ThenCurrentGuardRejectsWithoutCallback(string change, string error)
    {
        var input = await Setup();
        var interceptor = new BeforeLock(sessionLock: false, async () =>
        {
            await using var competing = fixture.CreateContext();
            var account = await competing.Accounts.SingleAsync(x => x.PublicId == input.Id);
            if (change == "closure") account.State = AccountState.ClosurePending;
            if (change == "policy") account.PolicyRevision++;
            if (change == "revocation") (await competing.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == input.Verifier)).Revoked = true;
            if (change == "erasure") competing.TerminalErasures.Add(new TerminalErasure { ResourceId = input.Id, ResourceKind = "account", DeletedAt = DateTimeOffset.UtcNow });
            await competing.SaveChangesAsync();
        });
        var called = false;
        var result = await new IdentityUpdateStore(new InterceptingFactory(fixture, interceptor)).UpdateAsync(input.Identity, input.Verifier,
            _ => { called = true; return Task.FromResult(Success(input.Identity)); }, default);
        Assert.True(interceptor.Ran);
        Assert.Equal(error, result.Error);
        Assert.False(called);
        await using var verify = fixture.CreateContext();
        var unchanged = await verify.Accounts.SingleAsync(x => x.PublicId == input.Id);
        Assert.Equal(3, unchanged.Revision);
        Assert.Equal(new byte[] { 1, 2, 3 }, unchanged.DetailsEnvelope);
        Assert.Equal(input.Identity, unchanged.HeimdallPublicId);
    }

    [FunctionalFact]
    public async Task GivenSessionExpiresWhileWaitingForSessionLock_WhenUpdatingIdentity_ThenUseFreshStatementTime()
    {
        var input = await Setup();
        var expires = new DateTimeOffset(DateTimeOffset.UtcNow.AddSeconds(1).Ticks / 10 * 10, TimeSpan.Zero);
        await using (var setup = fixture.CreateContext())
        {
            var session = await setup.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == input.Verifier);
            session.ExpiresAt = expires;
            await setup.SaveChangesAsync();
        }
        // Account lock already started the transaction. Delay its session-lock
        // command until expiry; transaction-begin now() would be stale here.
        var interceptor = new BeforeLock(sessionLock: true, async () =>
        {
            for (var remaining = expires - DateTimeOffset.UtcNow; remaining > TimeSpan.Zero; remaining = expires - DateTimeOffset.UtcNow)
                await Task.Delay(remaining + TimeSpan.FromMilliseconds(20));
        });
        var called = false;
        var before = await Snapshot(input.Id, input.Verifier);
        var result = await new IdentityUpdateStore(new InterceptingFactory(fixture, interceptor)).UpdateAsync(input.Identity, input.Verifier,
            _ => { called = true; return Task.FromResult(Success(input.Identity)); }, default);
        Assert.True(interceptor.Ran);
        Assert.Equal("vault_access_denied", result.Error);
        Assert.False(called);
        Assert.Equal(before, await Snapshot(input.Id, input.Verifier));
    }

    [FunctionalFact]
    public async Task GivenUnavailableDatabase_WhenUpdatingIdentity_ThenFailWithoutInvokingProvider()
    {
        var result = await new IdentityUpdateStore(new UnavailableFactory()).UpdateAsync(Guid.NewGuid(), new string('a', 64),
            _ => throw new InvalidOperationException("Provider must not be invoked."), default);
        Assert.Equal("persistence_unavailable", result.Error);
        Assert.Null(result.Identity);
    }

    [FunctionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenCancelledCallerBeforeOrDuringCallback_WhenUpdatingIdentity_ThenPropagateAndReleaseLocks(bool during)
    {
        var input = await Setup();
        var before = await Snapshot(input.Id, input.Verifier);
        using var cancelled = new CancellationTokenSource();
        if (!during) cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IdentityUpdateStore(fixture).UpdateAsync(input.Identity, input.Verifier,
            ct => { cancelled.Cancel(); ct.ThrowIfCancellationRequested(); return Task.FromResult(Success(input.Identity)); }, cancelled.Token));
        Assert.Equal(before, await Snapshot(input.Id, input.Verifier));
    }

    private async Task<(Guid Id, Guid Identity, string Verifier)> Setup(string state = "active")
    {
        var id = Guid.NewGuid();
        var identity = Guid.NewGuid();
        var verifier = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        var now = new DateTimeOffset(DateTimeOffset.UtcNow.Ticks / 10 * 10, TimeSpan.Zero);
        await using var context = fixture.CreateContext();
        var account = new Account { PublicId = id, HeimdallPublicId = identity, Revision = 3, DetailsEnvelope = [1, 2, 3],
            State = state == "closing" ? AccountState.ClosurePending : AccountState.Active, RenewalEnabled = state is not ("noExpiry" or "disabledWithExpiry") };
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
            IssuedAt = state == "future" ? now.AddHours(1) : now.AddMinutes(-5),
            ExpiresAt = state is "noExpiry" or "missingExpiry" ? null : state == "expired" ? now.AddMinutes(-1) : now.AddMinutes(5),
            Revoked = state == "revoked", PolicyRevision = state == "stalePolicy" ? 0 : 1, RevocationGeneration = state == "staleGeneration" ? 0 : 1
        });
        if (state == "erased") context.TerminalErasures.Add(new TerminalErasure { ResourceId = id, ResourceKind = "account", DeletedAt = now });
        await context.SaveChangesAsync();
        return (id, identity, verifier);
    }
    private async Task<string> Snapshot(Guid id, string verifier)
    {
        await using var context = fixture.CreateContext();
        return JsonSerializer.Serialize(new { Account = await context.Accounts.AsNoTracking().SingleAsync(x => x.PublicId == id),
            Session = await context.VaultAccessSessions.AsNoTracking().SingleOrDefaultAsync(x => x.HandleVerifier == verifier) });
    }
    private static IdentityUpdateResult Success(Guid identity) => new(new IdentityDetails(identity, "Updated Owner", "updated@example.test", false));
    private sealed class BeforeLock(bool sessionLock, Func<Task> action) : DbCommandInterceptor
    {
        public bool Ran { get; private set; }
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            if (!Ran && command.CommandText.Contains("FOR UPDATE", StringComparison.OrdinalIgnoreCase)
                && command.CommandText.Contains(sessionLock ? "vault_access_session" : "cerberus.account", StringComparison.Ordinal))
            { Ran = true; await action(); }
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
