using System.Data.Common;
using System.Security.Cryptography;
using ArturRios.Cerberus.Data.Offline;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Cerberus.Domain.Profiles;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class OfflinePolicyUpdateStoreConcurrencyTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenTwoCapturedPolicyUpdates_WhenCommitting_ThenOnlyOneVersionAndIntervalWin()
    {
        var s = await Setup();
        var captured = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var count = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var hook = new Before(c => Lock(c, "account"), async _ =>
        {
            if (Interlocked.Increment(ref count) == 2) captured.TrySetResult();
            await release.Task.WaitAsync(ct.Token);
        });
        var one = Store(hook).UpdateAsync(s.Request, ct.Token);
        var two = Store(hook).UpdateAsync(s.Request with { RenewalInterval = TimeSpan.FromSeconds(2) }, ct.Token);
        try { await captured.Task.WaitAsync(ct.Token); }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(one, two);
        Assert.Single(results, r => r.Error is null && r.Policy!.PolicyRevision == 2);
        Assert.Single(results, r => r.Error == "vault_access_denied" && r.Policy is null);
        var winner = results.Single(r => r.Error is null).Policy!;
        await using var db = fixture.CreateContext();
        var account = await db.Accounts.SingleAsync(x => x.Id == s.Owner);
        Assert.Equal(2, account.PolicyRevision);
        Assert.Equal(winner.RenewalInterval, account.RenewalInterval);
        Assert.Equal(7, account.Revision);
        Assert.Equal(new byte[] { 11, 22 }, account.DetailsEnvelope);
    }

    [FunctionalTheory]
    [InlineData("closure", "revision_conflict")]
    [InlineData("revision", "revision_conflict")]
    [InlineData("policy", "vault_access_denied")]
    [InlineData("generation", "vault_access_denied")]
    [InlineData("session", "vault_access_denied")]
    [InlineData("profile", "vault_access_denied")]
    public async Task GivenAuthorityChangesDuringOwnerLockWait_WhenUpdating_ThenPreserveTheWinningState(string change, string error)
    {
        var s = await Setup(selected: true);
        await using var held = fixture.CreateContext();
        await using var tx = await held.Database.BeginTransactionAsync();
        await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.Owner} FOR UPDATE");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = Store(new Before(c => Lock(c, "account"), _ => { ready.TrySetResult(); return Task.CompletedTask; }))
            .UpdateAsync(s.Request, default);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        if (change == "closure") await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET state=1 WHERE id={s.Owner}");
        if (change == "revision") await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET revision=revision+1 WHERE id={s.Owner}");
        if (change == "policy") await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET policy_revision=policy_revision+1 WHERE id={s.Owner}");
        if (change == "generation") await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET revocation_generation=revocation_generation+1 WHERE id={s.Owner}");
        if (change == "session") await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.vault_access_session SET revoked=true WHERE id={s.Session}");
        if (change == "profile") await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.profile SET deleted_at=statement_timestamp(),purge_at=statement_timestamp()+interval '30 days' WHERE id={s.Profile}");
        await tx.CommitAsync();
        var result = await pending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(error, result.Error);
        Assert.Null(result.Policy);
        await using var after = fixture.CreateContext();
        var row = await after.Accounts.SingleAsync(x => x.Id == s.Owner);
        Assert.Equal(change == "policy" ? 2 : 1, row.PolicyRevision);
        Assert.Equal(TimeSpan.FromHours(24), row.RenewalInterval);
        Assert.Equal(change == "revision" ? 8 : 7, row.Revision);
        Assert.Equal(change == "generation" ? 2 : 1, row.RevocationGeneration);
        Assert.Equal(new byte[] { 11, 22 }, row.DetailsEnvelope);
    }

    [FunctionalTheory]
    [InlineData("vault_access_session")]
    [InlineData("profile")]
    public async Task GivenAuthorityChangesDuringSessionOrProfileLockWait_WhenUpdating_ThenDenyWithoutMutation(string table)
    {
        var s = await Setup(selected: true);
        await using var held = fixture.CreateContext();
        await using var tx = await held.Database.BeginTransactionAsync();
        var id = table == "profile" ? s.Profile!.Value : s.Session;
        if (table == "profile")
            await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE id={id} FOR UPDATE");
        else
            await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE id={id} FOR UPDATE");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = Store(new Before(c => Lock(c, table), _ => { ready.TrySetResult(); return Task.CompletedTask; }))
            .UpdateAsync(s.Request, ct.Token);
        try
        {
            await ready.Task.WaitAsync(ct.Token);
            Assert.False(pending.IsCompleted);
            if (table == "profile") await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.profile SET deleted_at=statement_timestamp(),purge_at=statement_timestamp()+interval '30 days' WHERE id={id}");
            else await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.vault_access_session SET revoked=true WHERE id={id}");
        }
        finally { await tx.CommitAsync(); }
        var result = await pending.WaitAsync(ct.Token);
        Assert.Equal("vault_access_denied", result.Error);
        Assert.Null(result.Policy);
        await using var db = fixture.CreateContext();
        var account = await db.Accounts.SingleAsync(x => x.Id == s.Owner);
        Assert.Equal(1, account.PolicyRevision);
        Assert.Equal(TimeSpan.FromHours(24), account.RenewalInterval);
        Assert.Equal(new byte[] { 11, 22 }, account.DetailsEnvelope);
    }

    [FunctionalTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenExpiryAroundTheGuardedStatement_WhenUpdating_ThenRequireAuthorityOnlyAtTheWrite(bool afterGuard)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var s = await Setup();
            await using var db = fixture.CreateContext();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.vault_access_session SET expires_at=statement_timestamp()+interval '2 seconds' WHERE id={s.Session}");
            var expires = await db.VaultAccessSessions.Where(x => x.Id == s.Session).Select(x => x.ExpiresAt).SingleAsync();
            var observed = false;
            async Task Wait(DbCommand command)
            {
                observed = true;
                await using var wait = command.Connection!.CreateCommand();
                wait.Transaction = command.Transaction;
                wait.CommandText = "SELECT pg_sleep(GREATEST(0,EXTRACT(EPOCH FROM (@expires-clock_timestamp())))+0.06)";
                wait.Parameters.Add(new NpgsqlParameter("expires", expires!.Value));
                await wait.ExecuteNonQueryAsync();
            }
            DbCommandInterceptor hook = afterGuard ? new After(Cas, Wait) : new Before(Cas, Wait);
            var result = await Store(hook).UpdateAsync(s.Request, default);
            var revision = await db.Accounts.Where(x => x.Id == s.Owner).Select(x => x.PolicyRevision).SingleAsync();
            if (!observed && result.Error == "vault_access_denied")
            {
                // Retry only when scheduling consumed setup's window before
                // the required observation. Never retry an observed outcome.
                Assert.Equal(1, revision);
                continue;
            }
            Assert.True(observed);
            Assert.Equal(afterGuard ? null : "vault_access_denied", result.Error);
            Assert.Equal(afterGuard ? 2 : 1, revision);
            return;
        }
        Assert.Fail("Scheduling consumed the expiry setup window in all three attempts.");
    }

    [FunctionalTheory]
    [InlineData("revision")]
    [InlineData("session")]
    [InlineData("profileTerminal")]
    public async Task GivenStorageFaultAfterPolicyWrite_WhenCheckingPostconditions_ThenRollbackTheEntireOperation(string fault)
    {
        var s = await Setup(selected: true);
        var hook = new After(Cas, async command =>
        {
            await using var injected = command.Connection!.CreateCommand();
            injected.Transaction = command.Transaction;
            injected.CommandText = fault switch
            {
                "revision" => "UPDATE cerberus.account SET revision=revision+1 WHERE id=@owner",
                "session" => "UPDATE cerberus.vault_access_session SET revoked=true WHERE account_id=@owner",
                _ => "INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at) VALUES('profile',@profile,statement_timestamp())"
            };
            injected.Parameters.Add(new NpgsqlParameter("owner", s.Owner));
            injected.Parameters.Add(new NpgsqlParameter("profile", s.ProfilePublic));
            await injected.ExecuteNonQueryAsync();
        });
        var result = await Store(hook).UpdateAsync(s.Request, default);
        Assert.Equal("persistence_unavailable", result.Error);
        Assert.Null(result.Policy);
        await using var db = fixture.CreateContext();
        var row = await db.Accounts.SingleAsync(x => x.Id == s.Owner);
        Assert.Equal(1, row.PolicyRevision); Assert.Equal(7, row.Revision);
        Assert.Equal(TimeSpan.FromHours(24), row.RenewalInterval);
        Assert.False((await db.VaultAccessSessions.SingleAsync(x => x.Id == s.Session)).Revoked);
        Assert.False(await db.TerminalErasures.AnyAsync(x => x.ResourceKind == "profile" && x.ResourceId == s.ProfilePublic));
    }

    private async Task<SetupState> Setup(bool selected = false)
    {
        await using var db = fixture.CreateContext();
        var identity = Guid.NewGuid();
        var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = identity, DetailsEnvelope = [11, 22], Revision = 7 };
        db.Accounts.Add(account); await db.SaveChangesAsync();
        Profile? profile = null;
        if (selected) { profile = new Profile { PublicId = Guid.NewGuid(), AccountId = account.Id }; db.Profiles.Add(profile); await db.SaveChangesAsync(); }
        var verifier = Convert.ToHexStringLower(SHA256.HashData(Guid.NewGuid().ToByteArray()));
        var session = new VaultAccessSession { AccountId = account.Id, HandleVerifier = verifier, ProfileId = profile?.Id,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 };
        db.VaultAccessSessions.Add(session); await db.SaveChangesAsync();
        return new(account.Id, session.Id, profile?.Id, profile?.PublicId ?? Guid.Empty, new(identity, verifier, 1, true, TimeSpan.FromSeconds(1)));
    }
    private sealed record SetupState(long Owner, long Session, long? Profile, Guid ProfilePublic, OfflinePolicyUpdateRequest Request);
    private OfflinePolicyUpdateStore Store(DbCommandInterceptor hook) => new(new Factory(fixture, hook));
    private static bool Cas(DbCommand c) => c.CommandText.TrimStart().StartsWith("UPDATE cerberus.account a SET renewal_enabled", StringComparison.Ordinal);
    private static bool Lock(DbCommand c, string table) => c.CommandText.StartsWith("SELECT FROM cerberus." + table, StringComparison.Ordinal);
    private sealed class Factory(PostgresFixture fixture, DbCommandInterceptor hook) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            using var db = fixture.CreateContext();
            return new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(hook).Options);
        }
    }
    private sealed class Before(Func<DbCommand, bool> matches, Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d,
            InterceptionResult<int> result, CancellationToken ct = default)
        { if (matches(c)) await action(c); return result; }
    }
    private sealed class After(Func<DbCommand, bool> matches, Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int result, CancellationToken ct = default)
        { if (matches(c) && result == 1) await action(c); return result; }
    }
}
