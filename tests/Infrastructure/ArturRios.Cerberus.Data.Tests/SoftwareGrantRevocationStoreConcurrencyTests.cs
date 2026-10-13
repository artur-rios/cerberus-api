using System.Data.Common;
using System.Security.Cryptography;
using ArturRios.Cerberus.Data.Software;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class SoftwareGrantRevocationStoreConcurrencyTests(PostgresFixture fixture)
{
    // Break: a captured loser reporting not_found, advancing twice, or leaving a mixed aggregate.
    // PostgreSQL's blocking graph, not elapsed time, proves that each contender really waited.
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenTwoCapturedRevokers_WhenGuardedWriteCommits_ThenOneWinnerAndLaterRequestIsNotFound(bool selectedProfile)
    {
        using var s = await Setup();
        if (selectedProfile) await SelectProfile(s);
        var original = await Grant(s); var vault = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
        var ready = Signal(); var release = Signal(); var waiting = Signal(); var holder = 0; var waiter = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var first = Store(new Before(Cas, async c =>
        { holder = Pid(c); ready.TrySetResult(); await release.Task.WaitAsync(ct.Token); })).RevokeAsync(Request(s), ct.Token);
        await ready.Task.WaitAsync(ct.Token);
        var second = Store(new Before(c => Lock(c, "account"), c =>
        { waiter = Pid(c); waiting.TrySetResult(); return Task.CompletedTask; })).RevokeAsync(Request(s), ct.Token);
        try { await waiting.Task.WaitAsync(ct.Token); await Blocked(holder, waiter, ct.Token); Assert.False(second.IsCompleted); }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, x => x.Data == new SoftwareGrantRevocationDetails(s.Grant, 2) && x.Error is null);
        Assert.Single(results, x => x.Error == "revision_conflict" && x.Data is null);
        await AssertRevoked(s, original, 2);
        var winner = await SoftwareGrantSetup.Aggregate(fixture, s.Grant);
        var late = await Store().RevokeAsync(Request(s), ct.Token);
        Assert.Equal("not_found", late.Error); Assert.Null(late.Data);
        Assert.Equal(winner, await SoftwareGrantSetup.Aggregate(fixture, s.Grant));
        Assert.Equal(vault, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
    }

    // Break: a stale revoker erasing UC-42's replacement key/targets or treating captured loss as absence.
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenReactivationFromRevisionTwo_WhenStaleRevokerRaces_ThenPreserveCompleteRevisionThree(bool capturedActive)
    {
        using var s = await Setup(); using var replacementKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var replacement = await AssociationSetup.Items(fixture, s.Owner);
        var original = await Grant(s); var vault = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
        var captured = Signal(); var resume = Signal(); var attempting = Signal(); var ready = Signal(); var release = Signal();
        var holder = 0; var waiter = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        Task<VaultResult<SoftwareGrantRevocationDetails>>? stale = null;
        if (capturedActive)
        {
            stale = Store(new Before(c => Lock(c, "account"), async c =>
            { waiter = Pid(c); captured.TrySetResult(); await resume.Task.WaitAsync(ct.Token); attempting.TrySetResult(); })).RevokeAsync(Request(s), ct.Token);
            await captured.Task.WaitAsync(ct.Token);
        }
        Assert.Null((await Store().RevokeAsync(Request(s), ct.Token)).Error);
        await AssertRevoked(s, original, 2);
        var request = s.Request(2, items: replacement, recipient: replacementKey);
        var reactivate = new SoftwareGrantStore(new ProfileSetup.Factory(fixture, new Before(Cas, async c =>
            { holder = Pid(c); ready.TrySetResult(); await release.Task.WaitAsync(ct.Token); }))).GrantAsync(request, ct.Token);
        try
        {
            await ready.Task.WaitAsync(ct.Token);
            if (capturedActive)
            {
                resume.TrySetResult(); await attempting.Task.WaitAsync(ct.Token);
                await Blocked(holder, waiter, ct.Token); Assert.False(stale!.IsCompleted);
            }
            else
            {
                stale = Store().RevokeAsync(Request(s), ct.Token);
                Assert.Equal("not_found", (await stale).Error);
            }
        }
        finally { resume.TrySetResult(); release.TrySetResult(); }
        var result = await reactivate;
        Assert.Null(result.Error); Assert.Equal(new SoftwareGrantDetails(s.Grant, 3), result.Data);
        var loser = await stale!;
        Assert.Equal(capturedActive ? "revision_conflict" : "not_found", loser.Error); Assert.Null(loser.Data);
        await using var db = fixture.CreateContext(); var saved = await Grant(s);
        Assert.Equal(original.Id, saved.Id); Assert.Equal(original.AccountId, saved.AccountId);
        Assert.Equal(original.PublicId, saved.PublicId); Assert.Equal(original.HeimdallSoftwarePublicId, saved.HeimdallSoftwarePublicId);
        Assert.Equal(SoftwareGrantState.Active, saved.State); Assert.Equal(3, saved.Revision);
        Assert.NotEqual(original.ConcurrencyStamp, saved.ConcurrencyStamp);
        Assert.Equal(SoftwareGrantSetup.Bytes(request.Input.RecipientKey), saved.RecipientKey);
        var record = Assert.Single(await db.SoftwareGrantRecords.Where(x => x.GrantId == saved.Id).ToArrayAsync());
        var collection = Assert.Single(await db.SoftwareGrantCollections.Where(x => x.GrantId == saved.Id).ToArrayAsync());
        Assert.Equal(s.Owner.InternalId, record.AccountId); Assert.Equal(s.Owner.InternalId, collection.AccountId);
        Assert.Equal(replacement.Record.Id, record.RecordId); Assert.Equal(replacement.Collection.Id, collection.CollectionId);
        Assert.Equal(SoftwareGrantSetup.Bytes(request.Input.Targets[0].KeyEnvelope), record.RecipientKeyEnvelope);
        Assert.Equal(SoftwareGrantSetup.Bytes(request.Input.Targets[1].KeyEnvelope), collection.RecipientKeyEnvelope);
        var winner = await SoftwareGrantSetup.Aggregate(fixture, s.Grant);
        Assert.Equal("revision_conflict", (await Store().RevokeAsync(Request(s), ct.Token)).Error);
        Assert.Equal(winner, await SoftwareGrantSetup.Aggregate(fixture, s.Grant));
        Assert.Equal(vault, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
    }

    // Break: authorizing at capture time or rolling back an already-authorized transition on natural expiry.
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenDatabaseTimeExpiryAroundGuardedWrite_WhenRevoking_ThenOnlyAlreadyAuthorizedTransitionCommits(bool after)
    {
        using var s = await Setup(); var original = await Grant(s);
        var before = await SoftwareGrantSetup.Aggregate(fixture, s.Grant);
        string? vault = null; var boundaryEntries = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        async Task Expire(DbCommand _)
        {
            Assert.Equal(1, ++boundaryEntries);
            await using var observer = fixture.CreateContext();
            Task<bool> Expired() => observer.Database.SqlQuery<bool>($"SELECT expires_at<=statement_timestamp() AS \"Value\" FROM cerberus.vault_access_session WHERE handle_verifier={s.Owner.Verifier}").SingleAsync(ct.Token);
            Assert.False(await Expired(), "The session must remain current until the guarded-write boundary is reached.");
            // A concurrent reader must never see the uncommitted revoked row with old children.
            Assert.Equal(before, await SoftwareGrantSetup.Aggregate(fixture, s.Grant));
            vault = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
            while (!await Expired()) await Task.Delay(20, ct.Token);
        }
        DbCommandInterceptor hook = after ? new After(Cas, Expire) : new Before(Cas, Expire);
        var store = Store(hook); var request = Request(s);
        // Arm the database deadline only after fixture/snapshot setup, immediately before
        // capture. Leave ample time for rechecks, then cross expiry inside the observed hook.
        await Sql("UPDATE cerberus.vault_access_session SET expires_at=statement_timestamp()+interval '10 seconds' WHERE handle_verifier={0}", s.Owner.Verifier);
        var result = await store.RevokeAsync(request, ct.Token);
        Assert.Equal(1, boundaryEntries); Assert.NotNull(vault);
        Assert.Equal(after ? null : "vault_access_denied", result.Error);
        if (after) { Assert.Equal(new SoftwareGrantRevocationDetails(s.Grant, 2), result.Data); await AssertRevoked(s, original, 2); }
        else { Assert.Null(result.Data); Assert.Equal(before, await SoftwareGrantSetup.Aggregate(fixture, s.Grant)); }
        Assert.Equal(vault, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
    }

    public static IEnumerable<object[]> WaitChanges()
    {
        foreach (var phase in new[] { "account", "vault_access_session", "profile", "software_grant" })
        {
            // Earlier authority rows are already held at later boundaries. Their row updates
            // are tested as blocked writers below; terminal markers remain independent rows.
            foreach (var change in new[] { "actorTerminal", "profileTerminal", "grantRevision", "grantRevoked", "grantTerminal", "recordDelete", "collectionDelete", "recordEnvelope", "collectionEnvelope" })
                yield return [phase, change, change == "profileTerminal" ? "vault_access_denied" : "revision_conflict"];
            if (phase != "software_grant") yield return [phase, "profileDeleted", "vault_access_denied"];
            if (phase is "account" or "vault_access_session")
            {
                foreach (var change in new[] { "sessionRevoked", "sessionPolicy", "sessionGeneration", "sessionExpiry", "sessionFuture", "sessionMissing" })
                    yield return [phase, change, "vault_access_denied"];
                yield return [phase, "selection", "revision_conflict"];
            }
            if (phase == "account")
            {
                yield return [phase, "actorInactive", "revision_conflict"];
                yield return [phase, "accountPolicy", "vault_access_denied"];
                yield return [phase, "accountGeneration", "vault_access_denied"];
            }
        }
    }

    // Break: stale captured evidence authorizing any write, or loss of session authority masked as conflict.
    [FunctionalTheory][MemberData(nameof(WaitChanges))]
    public async Task GivenCommittedChangeAtLockWait_WhenRevoking_ThenClassifyCurrentAuthorityAndPreserveExactWinner(string phase, string change, string error)
    {
        using var s = await Setup(); var profile = await SelectProfile(s);
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        await Hold(blocker, s, profile, phase);
        var holder = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        var waiting = Signal(); var acquired = Signal(); var release = Signal(); var waiter = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = Store(new AtLock(phase, c => { waiter = Pid(c); waiting.TrySetResult(); }, async () =>
        { acquired.TrySetResult(); await release.Task.WaitAsync(ct.Token); })).RevokeAsync(Request(s), ct.Token);
        string winner; string vault;
        try
        {
            await waiting.Task.WaitAsync(ct.Token); await Blocked(holder, waiter, ct.Token); Assert.False(pending.IsCompleted);
            await Change(blocker, s, profile, change, ct.Token);
            await tx.CommitAsync(ct.Token); await acquired.Task.WaitAsync(ct.Token);
            // Freeze the loser before its recheck so the expected snapshot belongs solely to the winner.
            winner = await SoftwareGrantSetup.Aggregate(fixture, s.Grant);
            vault = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
        }
        finally { release.TrySetResult(); }
        var result = await pending;
        Assert.Equal(error, result.Error); Assert.Null(result.Data);
        Assert.Equal(winner, await SoftwareGrantSetup.Aggregate(fixture, s.Grant));
        Assert.Equal(vault, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
        if (change is "actorInactive" or "actorTerminal" or "grantRevoked" or "grantTerminal")
        {
            Assert.Equal("not_found", (await Store().RevokeAsync(Request(s), ct.Token)).Error);
            Assert.Equal(winner, await SoftwareGrantSetup.Aggregate(fixture, s.Grant));
        }
    }

    // Break: a guarded write trusting the final recheck after an independent writer changed its inputs.
    [FunctionalTheory]
    [InlineData("actorTerminal", "revision_conflict")][InlineData("profileTerminal", "vault_access_denied")]
    [InlineData("grantTerminal", "revision_conflict")][InlineData("recordDelete", "revision_conflict")]
    [InlineData("collectionDelete", "revision_conflict")][InlineData("recordEnvelope", "revision_conflict")]
    [InlineData("collectionEnvelope", "revision_conflict")]
    public async Task GivenCommittedChangeAfterFinalRecheck_WhenGuardedWriteRuns_ThenPreserveWinnerWithoutRevoking(string change, string error)
    {
        using var s = await Setup(); var profile = await SelectProfile(s);
        string? winner = null; string? vault = null;
        var result = await Store(new Before(Cas, async _ =>
        {
            await using var writer = fixture.CreateContext();
            await Change(writer, s, profile, change, default);
            winner = await SoftwareGrantSetup.Aggregate(fixture, s.Grant);
            vault = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
        })).RevokeAsync(Request(s), default);
        Assert.Equal(error, result.Error); Assert.Null(result.Data); Assert.NotNull(winner); Assert.NotNull(vault);
        Assert.Equal(winner, await SoftwareGrantSetup.Aggregate(fixture, s.Grant));
        Assert.Equal(vault, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
    }

    public static IEnumerable<object[]> HeldAuthority()
    {
        yield return ["vault_access_session", "account"];
        yield return ["profile", "account"];
        yield return ["profile", "vault_access_session"];
        yield return ["software_grant", "account"];
        yield return ["software_grant", "vault_access_session"];
        yield return ["software_grant", "profile"];
    }

    // Break: reversing/skipping the account -> session -> optional profile -> grant prefix.
    [FunctionalTheory][MemberData(nameof(HeldAuthority))]
    public async Task GivenLaterLockWait_WhenEarlierAuthorityWriterCompetes_ThenKeepCanonicalPrefixLocked(string phase, string authority)
    {
        using var s = await Setup(); var profile = await SelectProfile(s); var original = await Grant(s);
        var vault = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        await Hold(blocker, s, profile, phase);
        var holder = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        var reached = Signal(); var waiter = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var pending = Store(new Before(c => Lock(c, phase), c =>
        { waiter = Pid(c); reached.TrySetResult(); return Task.CompletedTask; })).RevokeAsync(Request(s), ct.Token);
        try
        {
            await reached.Task.WaitAsync(ct.Token); await Blocked(holder, waiter, ct.Token);
            await using var writer = fixture.CreateContext(); await using var attempted = await writer.Database.BeginTransactionAsync(ct.Token);
            var exception = await Assert.ThrowsAsync<PostgresException>(() => Hold(writer, s, profile, authority, nowait: true));
            Assert.Equal(PostgresErrorCodes.LockNotAvailable, exception.SqlState);
            await attempted.RollbackAsync(ct.Token);
        }
        finally { await tx.CommitAsync(); }
        Assert.Null((await pending).Error); await AssertRevoked(s, original, 2);
        Assert.Equal(vault, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
    }

    // Break: treating successful CAS as permission to skip non-time authority checks or ownership.
    [FunctionalTheory]
    [InlineData("actorInactive")][InlineData("actorTerminal")][InlineData("accountPolicy")][InlineData("accountGeneration")]
    [InlineData("sessionRevoked")][InlineData("sessionPolicy")][InlineData("sessionGeneration")][InlineData("selection")]
    [InlineData("profileDeleted")][InlineData("profileTerminal")][InlineData("grantTerminal")]
    public async Task GivenNonTimeAuthorityChangeAfterGuardedWrite_WhenVerifying_ThenRollbackEntireTransition(string change)
    {
        using var s = await Setup(); var profile = await SelectProfile(s);
        var before = await SoftwareGrantSetup.Aggregate(fixture, s.Grant); var vault = await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId);
        var result = await Store(new After(Cas, c => Execute(c, ChangeSql(change), s, profile))).RevokeAsync(Request(s), default);
        Assert.Equal("persistence_unavailable", result.Error); Assert.Null(result.Data);
        Assert.Equal(before, await SoftwareGrantSetup.Aggregate(fixture, s.Grant));
        Assert.Equal(vault, await SoftwareGrantSetup.Vault(fixture, s.Owner.InternalId));
        Assert.Null((await Store().RevokeAsync(Request(s), default)).Error);
    }

    private static string ChangeSql(string change) => change switch
    {
        "actorInactive" => "UPDATE cerberus.account SET state=1 WHERE id=@owner",
        "actorTerminal" => "INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at) SELECT 'account',public_id,statement_timestamp() FROM cerberus.account WHERE id=@owner",
        "accountPolicy" => "UPDATE cerberus.account SET policy_revision=policy_revision+1 WHERE id=@owner",
        "accountGeneration" => "UPDATE cerberus.account SET revocation_generation=revocation_generation+1 WHERE id=@owner",
        "sessionRevoked" => "UPDATE cerberus.vault_access_session SET revoked=true WHERE handle_verifier=@verifier",
        "sessionPolicy" => "UPDATE cerberus.vault_access_session SET policy_revision=policy_revision+1 WHERE handle_verifier=@verifier",
        "sessionGeneration" => "UPDATE cerberus.vault_access_session SET revocation_generation=revocation_generation+1 WHERE handle_verifier=@verifier",
        "sessionExpiry" => "UPDATE cerberus.vault_access_session SET expires_at=statement_timestamp() WHERE handle_verifier=@verifier",
        "sessionFuture" => "UPDATE cerberus.vault_access_session SET issued_at=statement_timestamp()+interval '1 hour' WHERE handle_verifier=@verifier",
        "sessionMissing" => "DELETE FROM cerberus.vault_access_session WHERE handle_verifier=@verifier",
        "selection" => "UPDATE cerberus.vault_access_session SET profile_id=NULL WHERE handle_verifier=@verifier",
        "profileDeleted" => "UPDATE cerberus.profile SET deleted_at=statement_timestamp() WHERE id=@profile",
        "profileTerminal" => "INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at) SELECT 'profile',public_id,statement_timestamp() FROM cerberus.profile WHERE id=@profile",
        "grantRevision" => "UPDATE cerberus.software_grant SET revision=revision+1,concurrency_stamp=gen_random_uuid() WHERE public_id=@grant",
        "grantRevoked" => "UPDATE cerberus.software_grant SET state=1,revision=2,recipient_key=''::bytea,concurrency_stamp=gen_random_uuid() WHERE public_id=@grant; DELETE FROM cerberus.software_grant_record WHERE grant_id=(SELECT id FROM cerberus.software_grant WHERE public_id=@grant); DELETE FROM cerberus.software_grant_collection WHERE grant_id=(SELECT id FROM cerberus.software_grant WHERE public_id=@grant)",
        "grantTerminal" => "INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at) VALUES('grant',@grant,statement_timestamp())",
        "recordDelete" => "DELETE FROM cerberus.software_grant_record WHERE grant_id=(SELECT id FROM cerberus.software_grant WHERE public_id=@grant)",
        "collectionDelete" => "DELETE FROM cerberus.software_grant_collection WHERE grant_id=(SELECT id FROM cerberus.software_grant WHERE public_id=@grant)",
        "recordEnvelope" => "UPDATE cerberus.software_grant_record SET recipient_key_envelope='winner'::bytea WHERE grant_id=(SELECT id FROM cerberus.software_grant WHERE public_id=@grant)",
        "collectionEnvelope" => "UPDATE cerberus.software_grant_collection SET recipient_key_envelope='winner'::bytea WHERE grant_id=(SELECT id FROM cerberus.software_grant WHERE public_id=@grant)",
        _ => throw new ArgumentOutOfRangeException(nameof(change))
    };
    private static NpgsqlParameter[] ChangeParameters(SoftwareGrantSetup s, long profile) =>
        [new("owner", s.Owner.InternalId), new("verifier", s.Owner.Verifier), new("profile", profile), new("grant", s.Grant)];
    private static Task Change(AppDbContext db, SoftwareGrantSetup s, long profile, string change, CancellationToken ct) =>
        db.Database.ExecuteSqlRawAsync(ChangeSql(change), ChangeParameters(s, profile), ct);
    private static async Task Execute(DbCommand parent, string sql, SoftwareGrantSetup s, long profile)
    {
        await using var command = parent.Connection!.CreateCommand(); command.Transaction = parent.Transaction; command.CommandText = sql;
        foreach (var parameter in ChangeParameters(s, profile)) command.Parameters.Add(parameter);
        await command.ExecuteNonQueryAsync();
    }
    private static Task Hold(AppDbContext db, SoftwareGrantSetup s, long profile, string phase, bool nowait = false)
    {
        var where = phase switch
        {
            "account" => "id=@owner", "vault_access_session" => "handle_verifier=@verifier",
            "profile" => "id=@profile", "software_grant" => "public_id=@grant", _ => throw new ArgumentOutOfRangeException(nameof(phase))
        };
        var sql = "SELECT FROM cerberus." + phase + " WHERE " + where + " FOR UPDATE" + (nowait ? " NOWAIT" : "");
        return db.Database.ExecuteSqlRawAsync(sql, ChangeParameters(s, profile));
    }
    private sealed class AtLock(string table, Action<DbCommand> before, Func<Task> after) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> result, CancellationToken ct = default)
        { if (Lock(c, table)) before(c); return ValueTask.FromResult(result); }
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int result, CancellationToken ct = default)
        { if (Lock(c, table)) await after(); return result; }
    }

    private async Task<SoftwareGrantSetup> Setup()
    {
        var s = await SoftwareGrantSetup.Create(fixture);
        Assert.Null((await new SoftwareGrantStore(fixture).GrantAsync(s.Request(), default)).Error); return s;
    }
    private async Task<long> SelectProfile(SoftwareGrantSetup s)
    {
        var profile = await s.Profile(fixture);
        await Sql("UPDATE cerberus.vault_access_session SET profile_id={1} WHERE handle_verifier={0}", s.Owner.Verifier, profile);
        return profile;
    }
    private async Task<SoftwareGrant> Grant(SoftwareGrantSetup s)
    { await using var db = fixture.CreateContext(); return await db.SoftwareGrants.AsNoTracking().SingleAsync(x => x.PublicId == s.Grant); }
    private async Task AssertRevoked(SoftwareGrantSetup s, SoftwareGrant original, long revision)
    {
        var saved = await Grant(s); await using var db = fixture.CreateContext();
        Assert.Equal(original.Id, saved.Id); Assert.Equal(original.AccountId, saved.AccountId);
        Assert.Equal(original.PublicId, saved.PublicId); Assert.Equal(original.HeimdallSoftwarePublicId, saved.HeimdallSoftwarePublicId);
        Assert.Equal(SoftwareGrantState.Revoked, saved.State); Assert.Equal(revision, saved.Revision);
        Assert.NotEqual(Guid.Empty, saved.ConcurrencyStamp); Assert.NotEqual(original.ConcurrencyStamp, saved.ConcurrencyStamp);
        Assert.Empty(saved.RecipientKey);
        Assert.Empty(await db.SoftwareGrantRecords.Where(x => x.GrantId == saved.Id).ToArrayAsync());
        Assert.Empty(await db.SoftwareGrantCollections.Where(x => x.GrantId == saved.Id).ToArrayAsync());
    }
    private SoftwareGrantRevocationStore Store(DbCommandInterceptor? hook = null) => new(hook is null ? fixture : new ProfileSetup.Factory(fixture, hook));
    private static SoftwareGrantRevocationRequest Request(SoftwareGrantSetup s) => new(s.Owner.Actor, s.Owner.Verifier, s.Grant, new(1));
    private async Task Sql(string statement, params object[] parameters)
    { await using var db = fixture.CreateContext(); await db.Database.ExecuteSqlRawAsync(statement, parameters); }
    private static bool Cas(DbCommand c) => c.CommandText.Contains("UPDATE cerberus.software_grant AS g");
    private static bool Lock(DbCommand c, string table) => c.CommandText.StartsWith("SELECT FROM cerberus." + table + " ") && c.CommandText.Contains("FOR ");
    private static int Pid(DbCommand c) => ((NpgsqlConnection)c.Connection!).ProcessID;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private async Task Blocked(int holder, int waiter, CancellationToken ct)
    {
        await using var observer = fixture.CreateContext();
        while (!await observer.Database.SqlQuery<bool>($"SELECT {holder}=ANY(pg_blocking_pids({waiter})) AS \"Value\"").SingleAsync(ct)) await Task.Delay(20, ct);
        Assert.True(await observer.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT FROM pg_locks WHERE pid={waiter} AND NOT granted) AS \"Value\"").SingleAsync(ct));
    }
    private sealed class Before(Func<DbCommand, bool> matches, Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> result, CancellationToken ct = default)
        { if (matches(c)) await action(c); return result; }
    }
    private sealed class After(Func<DbCommand, bool> matches, Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int result, CancellationToken ct = default)
        { if (matches(c)) await action(c); return result; }
    }
}
