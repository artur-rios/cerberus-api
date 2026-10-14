using System.Data.Common;
using ArturRios.Cerberus.Data.Sync;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

public partial class SyncEditsStoreTests
{
    [FunctionalTheory]
    [InlineData("expired", "vault_access_denied")][InlineData("revoked", "vault_access_denied")]
    [InlineData("policy", "vault_access_denied")][InlineData("generation", "vault_access_denied")]
    [InlineData("closing", "not_found")][InlineData("terminal", "not_found")]
    [InlineData("future", "vault_access_denied")][InlineData("disabled", null)]
    public async Task GivenCurrentAccountAuthority_WhenUploading_ThenRecheckPersistedPolicyBeforeAnyWrite(string state, string? error)
    {
        using var keys = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys); var edit = Create();
        await using var db = fixture.CreateContext();
        switch (state)
        {
            case "expired": await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1))); break;
            case "future": await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.IssuedAt, DateTimeOffset.UtcNow.AddDays(1))); break;
            case "revoked": await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true)); break;
            case "policy": await db.Accounts.Where(x => x.Id == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(v => v.PolicyRevision, 2)); break;
            case "generation": await db.Accounts.Where(x => x.Id == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(v => v.RevocationGeneration, 2)); break;
            case "closing": await db.Accounts.Where(x => x.Id == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(v => v.State, AccountState.ClosurePending)); break;
            case "terminal": db.TerminalErasures.Add(new() { ResourceKind = "account", ResourceId = s.AccountId, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); break;
            case "disabled":
                await db.Accounts.Where(x => x.Id == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(v => v.RenewalEnabled, false).SetProperty(v => v.RenewalInterval, (TimeSpan?)null));
                await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ExpiresAt, (DateTimeOffset?)null)); break;
        }
        var result = await new SyncEditsStore(fixture, Options).UploadAsync(new(s.Actor, s.Verifier, new([edit])), default);
        Assert.Equal(error, result.Error); Assert.Equal(error is null, await db.Records.AnyAsync(x => x.PublicId == edit.ResourceId));
        Assert.Equal(error is null, await db.SyncOperations.AnyAsync(x => x.AccountId == s.InternalId));
    }
    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenLedgerFailureAfterNativeWrite_WhenUploading_ThenRollbackResourceAndOperationAndAllowCleanRetry(bool cancel)
    {
        using var keys = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys); var edit = Create();
        var request = new SyncEditsRequest(s.Actor, s.Verifier, new([edit])); using var ct = new CancellationTokenSource();
        var factory = new ProfileSetup.Factory(fixture, new Fault(c => c.CommandText.Contains("INSERT INTO cerberus.sync_operation", StringComparison.Ordinal),
            () => { if (cancel) { ct.Cancel(); ct.Token.ThrowIfCancellationRequested(); } throw new TimeoutException("fixture ledger failure"); }));
        var pending = new SyncEditsStore(factory, Options).UploadAsync(request, ct.Token);
        if (cancel) await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        else { var result = await pending; Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Details); }
        await using var db = fixture.CreateContext(); Assert.False(await db.Records.AnyAsync(x => x.PublicId == edit.ResourceId));
        Assert.False(await db.SyncOperations.AnyAsync(x => x.AccountId == s.InternalId));
        var retry = await new SyncEditsStore(fixture, Options).UploadAsync(request, default); Assert.Null(retry.Error);
        Assert.Equal("applied", Assert.Single(retry.Details!.Outcomes).Status);
    }
    [FunctionalFact]
    public async Task GivenAuthorityExpiresAfterNativeWrite_WhenPersistingOperation_ThenFinalRecheckRollsBackWholeBatch()
    {
        using var keys = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys); var edit = Create();
        var factory = new ProfileSetup.Factory(fixture, new AfterLedger(async c =>
        {
            await using var change = c.Connection!.CreateCommand(); change.Transaction = c.Transaction;
            change.CommandText = "UPDATE cerberus.vault_access_session SET expires_at=statement_timestamp()-interval '1 second' WHERE handle_verifier=@verifier";
            var parameter = change.CreateParameter(); parameter.ParameterName = "verifier"; parameter.Value = s.Verifier; change.Parameters.Add(parameter); await change.ExecuteNonQueryAsync();
        }));
        var result = await new SyncEditsStore(factory, Options).UploadAsync(new(s.Actor, s.Verifier, new([edit])), default);
        Assert.Equal("vault_access_denied", result.Error); Assert.Null(result.Details);
        await using var db = fixture.CreateContext(); Assert.False(await db.Records.AnyAsync(x => x.PublicId == edit.ResourceId)); Assert.False(await db.SyncOperations.AnyAsync(x => x.AccountId == s.InternalId));
        Assert.True(await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).Select(x => x.ExpiresAt > DateTimeOffset.UtcNow).SingleAsync());
    }
    [FunctionalFact]
    public async Task GivenInvalidNativeNonceAfterEarlierCreate_WhenUploading_ThenRollbackAllBatchWrites()
    {
        using var keys = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, keys); var store = new SyncEditsStore(fixture, Options); var existing = Create();
        Assert.Null((await store.UploadAsync(new(s.Actor, s.Verifier, new([existing])), default)).Error); var earlier = Create();
        var invalid = new SyncRecordUpdate(Guid.NewGuid(), new(existing.ResourceId, existing.Input.Envelope with { Ciphertext = "BAUG" }, existing.EditedAt.AddSeconds(1)));
        var result = await store.UploadAsync(new(s.Actor, s.Verifier, new([earlier, invalid])), default); Assert.Equal("validation_failed", result.Error);
        await using var db = fixture.CreateContext(); Assert.False(await db.Records.AnyAsync(x => x.PublicId == earlier.ResourceId));
        Assert.Equal(1, await db.Records.Where(x => x.PublicId == existing.ResourceId).Select(x => x.Revision).SingleAsync());
        Assert.Equal(1, await db.SyncOperations.CountAsync(x => x.AccountId == s.InternalId));
    }
    private sealed class Fault(Func<DbCommand, bool> match, Action action) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
        { if (match(c)) action(); return ValueTask.FromResult(r); }
    }
    private sealed class AfterLedger(Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        private bool persisted;
        public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c, CommandExecutedEventData d, DbDataReader r, CancellationToken ct = default)
        { if (c.CommandText.Contains("INSERT INTO cerberus.sync_operation", StringComparison.Ordinal)) persisted = true; return ValueTask.FromResult(r); }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> r, CancellationToken ct = default)
        { if (persisted && c.CommandText.Contains("foreign_owners", StringComparison.Ordinal)) { persisted = false; await action(c); } return r; }
    }
}
