using System.Data.Common;
using ArturRios.Cerberus.Data.Offline;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Offline;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class OfflineAuthorizationStoreConcurrencyTests(PostgresFixture fixture)
{
    [FunctionalTheory]
    [InlineData("closure", "revision_conflict")][InlineData("revision", "revision_conflict")]
    [InlineData("profile", "revision_conflict")][InlineData("profileRevision", "revision_conflict")]
    [InlineData("policy", "vault_access_denied")][InlineData("generation", "vault_access_denied")]
    [InlineData("session", "vault_access_denied")][InlineData("grant", "revision_conflict")]
    [InlineData("grantRevision", "revision_conflict")][InlineData("foreignClosure", "revision_conflict")]
    public async Task GivenAuthorityChangedDuringAccountLockWait_WhenRenewing_ThenDenyWithoutSigning(string change, string error)
    {
        var s = await OfflineAuthorizationTestData.Setup(fixture, sharing: true);
        await using var held = fixture.CreateContext(); await using var tx = await held.Database.BeginTransactionAsync();
        var accounts = new[] { s.AccountPublicId, s.ForeignOwnerPublicId };
        await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE public_id=ANY({accounts}) ORDER BY public_id FOR UPDATE");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var signer = new OfflineAuthorizationTestData.Signer();
        var pending = Store(signer, new Before("account", () => { ready.TrySetResult(); return Task.CompletedTask; })).RenewAsync(s.Request, ct.Token);
        try
        {
            await ready.Task.WaitAsync(ct.Token); Assert.False(pending.IsCompleted);
            if (change == "closure") await held.Accounts.Where(x => x.Id == s.Owner).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
            if (change == "revision") await held.Accounts.Where(x => x.Id == s.Owner).ExecuteUpdateAsync(x => x.SetProperty(a => a.Revision, 8));
            if (change == "profile") await held.Profiles.Where(x => x.Id == s.Profile).ExecuteUpdateAsync(x => x.SetProperty(p => p.DeletedAt, DateTimeOffset.UtcNow));
            if (change == "profileRevision") await held.Profiles.Where(x => x.Id == s.Profile).ExecuteUpdateAsync(x => x.SetProperty(p => p.Revision, 4));
            if (change == "policy") await held.Accounts.Where(x => x.Id == s.Owner).ExecuteUpdateAsync(x => x.SetProperty(a => a.PolicyRevision, 2));
            if (change == "generation") await held.Accounts.Where(x => x.Id == s.Owner).ExecuteUpdateAsync(x => x.SetProperty(a => a.RevocationGeneration, 2));
            if (change == "session") await held.VaultAccessSessions.Where(x => x.Id == s.Session).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true));
            if (change == "grant") await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.collection_grant SET state=1 WHERE id={s.Grant}");
            if (change == "grantRevision") await held.CollectionGrants.Where(x => x.Id == s.Grant).ExecuteUpdateAsync(x => x.SetProperty(g => g.Revision, 5));
            if (change == "foreignClosure") await held.Accounts.Where(x => x.Id == s.ForeignOwner).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
        }
        finally { await tx.CommitAsync(); }
        var result = await pending.WaitAsync(ct.Token);
        Assert.Equal(error, result.Error); Assert.Null(result.Details); Assert.Equal(0, signer.Calls);
        await using var after = fixture.CreateContext();
        Assert.Equal(new byte[] { 11, 22 }, await after.Accounts.Where(x => x.Id == s.Owner).Select(x => x.DetailsEnvelope).SingleAsync());
        Assert.Equal(change == "policy" ? 2 : 1, await after.Accounts.Where(x => x.Id == s.Owner).Select(x => x.PolicyRevision).SingleAsync());
    }

    [FunctionalTheory]
    [InlineData("vault_access_session", "vault_access_denied")][InlineData("profile", "revision_conflict")]
    [InlineData("collection_grant", "revision_conflict")]
    public async Task GivenIndependentMetadataChangeDuringLaterLockWait_WhenRenewing_ThenRecheckCurrentAuthority(string table, string error)
    {
        var s = await OfflineAuthorizationTestData.Setup(fixture, sharing: true);
        await using var held = fixture.CreateContext(); await using var tx = await held.Database.BeginTransactionAsync();
        if (table == "profile") await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE id={s.Profile} FOR UPDATE");
        else if (table == "collection_grant") await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_grant WHERE id={s.Grant} FOR UPDATE");
        else await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE id={s.Session} FOR UPDATE");
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        using var signer = new OfflineAuthorizationTestData.Signer();
        var pending = Store(signer, new Before(table, () => { ready.TrySetResult(); return Task.CompletedTask; })).RenewAsync(s.Request, ct.Token);
        try
        {
            await ready.Task.WaitAsync(ct.Token); Assert.False(pending.IsCompleted);
            if (table == "profile") await held.Profiles.Where(x => x.Id == s.Profile).ExecuteUpdateAsync(x => x.SetProperty(p => p.Revision, 4));
            else if (table == "collection_grant") await held.CollectionGrants.Where(x => x.Id == s.Grant).ExecuteUpdateAsync(x => x.SetProperty(g => g.Revision, 5));
            else await held.VaultAccessSessions.Where(x => x.Id == s.Session).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true));
        }
        finally { await tx.CommitAsync(); }
        var result = await pending.WaitAsync(ct.Token);
        Assert.Equal(error, result.Error); Assert.Null(result.Details); Assert.Equal(0, signer.Calls);
    }

    [FunctionalFact]
    public async Task GivenSessionExpiresDuringAccountLockWait_WhenRenewing_ThenDenyWithoutSigning()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var s = await OfflineAuthorizationTestData.Setup(fixture);
            await using var held = fixture.CreateContext();
            await held.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.vault_access_session SET expires_at=statement_timestamp()+interval '2 seconds' WHERE id={s.Session}");
            var expiry = await held.VaultAccessSessions.Where(x => x.Id == s.Session).Select(x => x.ExpiresAt).SingleAsync();
            await using var tx = await held.Database.BeginTransactionAsync();
            await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.Owner} FOR UPDATE");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var signer = new OfflineAuthorizationTestData.Signer();
            var pending = Store(signer, new Before("account", () => { ready.TrySetResult(); return Task.CompletedTask; })).RenewAsync(s.Request, ct.Token);
            var observed = false;
            try
            {
                await Task.WhenAny(ready.Task, pending).WaitAsync(ct.Token);
                observed = ready.Task.IsCompleted;
                if (observed)
                {
                    Assert.False(pending.IsCompleted);
                    await held.Database.ExecuteSqlInterpolatedAsync($"SELECT pg_sleep(GREATEST(0,EXTRACT(EPOCH FROM ({expiry!.Value}-clock_timestamp())))+0.06)", ct.Token);
                }
            }
            finally { await tx.CommitAsync(); }
            var result = await pending.WaitAsync(ct.Token);
            Assert.Equal("vault_access_denied", result.Error); Assert.Null(result.Details); Assert.Equal(0, signer.Calls);
            Assert.Equal(1, await held.Accounts.Where(x => x.Id == s.Owner).Select(x => x.PolicyRevision).SingleAsync());
            if (observed) return;
        }
        Assert.Fail("Scheduling consumed every setup window before the account lock wait was observed.");
    }

    [FunctionalFact]
    public async Task GivenSigningDelayUntilOnlineSessionExpires_WhenRenewing_ThenDoNotDiscloseTheMintedLease()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var s = await OfflineAuthorizationTestData.Setup(fixture);
            await using var db = fixture.CreateContext();
            await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.vault_access_session SET expires_at=statement_timestamp()+interval '2 seconds' WHERE id={s.Session}");
            var expiry = await db.VaultAccessSessions.Where(x => x.Id == s.Session).Select(x => x.ExpiresAt).SingleAsync();
            using var signer = new OfflineAuthorizationTestData.Signer(snapshot =>
            {
                using var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256);
                var lease = OfflineLease.Sign(snapshot, "fixture-lease-issuer", key.ExportPkcs8PrivateKeyPem());
                db.Database.ExecuteSqlInterpolated($"SELECT pg_sleep(GREATEST(0,EXTRACT(EPOCH FROM ({expiry!.Value}-clock_timestamp())))+0.06)");
                return lease;
            });
            var result = await new OfflineAuthorizationStore(fixture, signer).RenewAsync(s.Request, default);
            Assert.Equal("vault_access_denied", result.Error); Assert.Null(result.Details);
            Assert.Equal(1, await db.Accounts.Where(x => x.Id == s.Owner).Select(x => x.PolicyRevision).SingleAsync());
            if (signer.Calls == 1) return;
        }
        Assert.Fail("Scheduling consumed every setup window before signing was observed.");
    }
    private OfflineAuthorizationStore Store(IOfflineLeaseSigner signer, DbCommandInterceptor hook) => new(new Factory(fixture, hook), signer);
    private sealed class Factory(PostgresFixture fixture, DbCommandInterceptor hook) : IDbContextFactory<AppDbContext>
    {
        public AppDbContext CreateDbContext()
        {
            using var db = fixture.CreateContext();
            return new(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(hook).Options);
        }
    }
    private sealed class Before(string table, Func<Task> action) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data,
            InterceptionResult<int> result, CancellationToken ct = default)
        {
            if (command.CommandText.StartsWith("SELECT FROM cerberus." + table + " WHERE", StringComparison.Ordinal)) await action();
            return result;
        }
    }
}
