using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Collections;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Data.Software;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class SoftwareGrantStoreConcurrencyTests(PostgresFixture fixture)
{
    // Explicit barriers and pg_blocking_pids prove a waiter actually encountered
    // the held PostgreSQL row; timing alone is never the concurrency oracle.
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenSameIdOrRevokedRevisionRace_WhenGranting_ThenOneWinnerAndExactLoserVisibleAggregate(bool revoked)
    {
        using var s = await SoftwareGrantSetup.Create(fixture);
        if (revoked) { Assert.Null((await Store().GrantAsync(s.Request(), default)).Error); await Sql("UPDATE cerberus.software_grant SET state=1 WHERE public_id={0}", s.Grant); }
        var request = s.Request(revoked ? 1 : null); var reversed = request with { Input = request.Input with { Targets = request.Input.Targets.Reverse().ToArray() } };
        var reached = Signal(); var release = Signal(); var waiting = Signal(); var holder = 0; var waiter = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var paused = new ProfileSetup.Factory(fixture, new After(c => Lock(c, "account"), async c => { holder = Pid(c); reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }));
        var observed = new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), c => { waiter = Pid(c); waiting.TrySetResult(); return Task.CompletedTask; }));
        var first = Store(paused).GrantAsync(request, ct.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var second = Store(observed).GrantAsync(reversed, ct.Token);
        try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Blocked(holder, waiter, ct.Token); Assert.False(second.IsCompleted); }
        finally { release.TrySetResult(); }
        Assert.Null((await first).Error); Assert.Equal("revision_conflict", (await second).Error);
        await using var db = fixture.CreateContext(); var saved = await db.SoftwareGrants.SingleAsync(x => x.PublicId == s.Grant);
        Assert.Equal(revoked ? 2 : 1, saved.Revision); Assert.Equal(SoftwareGrantSetup.Bytes(request.Input.RecipientKey), saved.RecipientKey);
        Assert.Equal(SoftwareGrantSetup.Bytes(request.Input.Targets[0].KeyEnvelope), (await db.SoftwareGrantRecords.SingleAsync(x => x.GrantId == saved.Id)).RecipientKeyEnvelope);
        Assert.Equal(SoftwareGrantSetup.Bytes(request.Input.Targets[1].KeyEnvelope), (await db.SoftwareGrantCollections.SingleAsync(x => x.GrantId == saved.Id)).RecipientKeyEnvelope);
        var winner = await SoftwareGrantSetup.Aggregate(fixture, s.Grant);
        Assert.Equal("revision_conflict", (await Store().GrantAsync(request, ct.Token)).Error);
        Assert.Equal(winner, await SoftwareGrantSetup.Aggregate(fixture, s.Grant));
    }

    public static IEnumerable<object[]> WaitChanges()
    {
        foreach (var phase in new[] { "account", "vault_access_session", "profile", "collection", "record", "software_grant" })
        {
            yield return [phase, "expiry", "vault_access_denied"];
            yield return [phase, "selection", "not_found"];
            // Raw competing transactions also prove target changes cannot hide
            // behind a stale EF entity or a previously captured authority graph.
            // Once the grant lock is reached all targets are already locked;
            // target changes at that phase are tested as blocked writers below.
            if (phase != "software_grant")
            {
                yield return [phase, "trash", "not_found"];
                yield return [phase, "update", "revision_conflict"];
            }
        }
    }

    [FunctionalTheory][MemberData(nameof(WaitChanges))]
    public async Task GivenAuthorityOrTargetChangeAtEveryLockWait_WhenGranting_ThenPreserveRevokedAggregate(string phase, string change, string error)
    {
        using var s = await SoftwareGrantSetup.Create(fixture);
        var profile = await s.Profile(fixture);
        await Sql("INSERT INTO cerberus.profile_record(account_id,profile_id,record_id) VALUES({0},{1},{2})", s.Owner.InternalId, profile, s.Items.Record.Id);
        await Sql("INSERT INTO cerberus.profile_collection(profile_id,collection_id) VALUES({0},{1})", profile, s.Items.Collection.Id);
        await Sql("UPDATE cerberus.vault_access_session SET profile_id={0} WHERE handle_verifier={1}", profile, s.Owner.Verifier);
        Assert.Null((await Store().GrantAsync(s.Request(), default)).Error); await Sql("UPDATE cerberus.software_grant SET state=1 WHERE public_id={0}", s.Grant);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(3);
        if (change == "expiry") await Sql("UPDATE cerberus.vault_access_session SET expires_at={0} WHERE handle_verifier={1}", deadline, s.Owner.Verifier);
        var before = await SoftwareGrantSetup.Aggregate(fixture, s.Grant);
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        var targetId = phase switch { "account" => s.Owner.InternalId, "profile" => profile, "collection" => s.Items.Collection.Id, "record" => s.Items.Record.Id, _ => 0 };
        if (phase == "vault_access_session") await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE handle_verifier={s.Owner.Verifier} FOR UPDATE");
        else if (phase == "software_grant") await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.software_grant WHERE public_id={s.Grant} FOR UPDATE");
        else { var statement = "SELECT FROM cerberus." + phase + " WHERE id={0} FOR UPDATE"; await blocker.Database.ExecuteSqlRawAsync(statement, targetId); }
        var holder = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(); var waiter = 0; var reached = Signal();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pending = Store(new ProfileSetup.Factory(fixture, new Before(c => Lock(c, phase), c => { waiter = Pid(c); reached.TrySetResult(); return Task.CompletedTask; }))).GrantAsync(s.Request(1), ct.Token);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Blocked(holder, waiter, ct.Token); Assert.False(pending.IsCompleted);
            if (change == "expiry") { var wait = deadline - DateTimeOffset.UtcNow; if (wait > TimeSpan.Zero) await Task.Delay(wait + TimeSpan.FromMilliseconds(100), ct.Token); }
            else if (change == "selection") await blocker.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cerberus.profile_record WHERE profile_id={profile} AND record_id={s.Items.Record.Id}", ct.Token);
            else if (phase == "record") await blocker.Database.ExecuteSqlRawAsync(change == "trash"
                ? "UPDATE cerberus.record SET deleted_at=statement_timestamp() WHERE id={0}"
                : "UPDATE cerberus.record SET revision=revision+1 WHERE id={0}", [s.Items.Record.Id], ct.Token);
            else await blocker.Database.ExecuteSqlRawAsync(change == "trash"
                ? "UPDATE cerberus.collection SET deleted_at=statement_timestamp() WHERE id={0}"
                : "UPDATE cerberus.collection SET revision=revision+1 WHERE id={0}", [s.Items.Collection.Id], ct.Token);
        }
        finally { await tx.CommitAsync(); }
        Assert.Equal(error, (await pending).Error); Assert.Equal(before, await SoftwareGrantSetup.Aggregate(fixture, s.Grant));
    }

    [FunctionalTheory][InlineData("collection")][InlineData("record")]
    public async Task GivenLowerTargetHeldAndReversedInput_WhenGranting_ThenHigherTargetsRemainUnlockedAndCancellationRollsBack(string kind)
    {
        using var s = await SoftwareGrantSetup.Create(fixture); var other = await AssociationSetup.Items(fixture, s.Owner);
        var collections = new[] { s.Items.Collection, other.Collection }.OrderBy(x => x.PublicId).ToArray();
        var records = new[] { s.Items.Record, other.Record }.OrderBy(x => x.PublicId).ToArray();
        var request = s.Request(); request = request with { Input = request.Input with { Targets =
            [s.Target("record", other.Record.PublicId), s.Target("collection", collections[1].PublicId), s.Target("record", s.Items.Record.PublicId), s.Target("collection", collections[0].PublicId)] } };
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        var lockSql = "SELECT FROM cerberus." + kind + " WHERE id={0} FOR UPDATE";
        await blocker.Database.ExecuteSqlRawAsync(lockSql, kind == "record" ? records[0].Id : collections[0].Id);
        var holder = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(); var waiter = 0; var reached = Signal();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = Store(new ProfileSetup.Factory(fixture, new Before(c => Lock(c, kind), c => { waiter = Pid(c); reached.TrySetResult(); return Task.CompletedTask; }))).GrantAsync(request, ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Blocked(holder, waiter, ct.Token);
        await using var observer = fixture.CreateContext(); await using var held = await observer.Database.BeginTransactionAsync();
        if (kind == "collection")
        {
            await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE id={collections[1].Id} FOR UPDATE NOWAIT");
            await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE id={s.Items.Record.Id} OR id={other.Record.Id} FOR UPDATE NOWAIT");
        }
        else await observer.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE id={records[1].Id} FOR UPDATE NOWAIT");
        ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        await held.RollbackAsync(); await tx.RollbackAsync();
        await using var check = fixture.CreateContext(); Assert.False(await check.SoftwareGrants.AnyAsync(x => x.PublicId == s.Grant));
    }

    [FunctionalFact]
    public async Task GivenDifferentOwnersRacingSamePublicId_WhenInitialInsertsMeet_ThenUniqueConstraintPreservesOnlyWinningAggregate()
    {
        using var firstOwner = await SoftwareGrantSetup.Create(fixture); using var secondOwner = await SoftwareGrantSetup.Create(fixture);
        var firstRequest = firstOwner.Request(); var secondRequest = secondOwner.Request(grant: firstOwner.Grant);
        var firstReady = Signal(); var secondReady = Signal(); var release = Signal();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var first = Store(new ProfileSetup.Factory(fixture, new Before(c => c.CommandText.Contains("INSERT INTO cerberus.software_grant("), async _ =>
            { firstReady.TrySetResult(); await release.Task.WaitAsync(ct.Token); }))).GrantAsync(firstRequest, ct.Token);
        var second = Store(new ProfileSetup.Factory(fixture, new Before(c => c.CommandText.Contains("INSERT INTO cerberus.software_grant("), async _ =>
            { secondReady.TrySetResult(); await release.Task.WaitAsync(ct.Token); }))).GrantAsync(secondRequest, ct.Token);
        try { await Task.WhenAll(firstReady.Task, secondReady.Task).WaitAsync(TimeSpan.FromSeconds(10)); }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(first, second);
        Assert.Single(results, x => x.Error is null); Assert.Equal("revision_conflict", Assert.Single(results, x => x.Error is not null).Error);
        var winner = results[0].Error is null ? firstOwner : secondOwner; var winningRequest = results[0].Error is null ? firstRequest : secondRequest;
        await using var db = fixture.CreateContext(); var saved = await db.SoftwareGrants.SingleAsync(x => x.PublicId == firstOwner.Grant);
        Assert.Equal(winner.Owner.InternalId, saved.AccountId); Assert.Equal(winningRequest.Input.SoftwareIdentityId, saved.HeimdallSoftwarePublicId);
        Assert.Equal(winner.Items.Record.Id, (await db.SoftwareGrantRecords.SingleAsync(x => x.GrantId == saved.Id)).RecordId);
        Assert.Equal(winner.Items.Collection.Id, (await db.SoftwareGrantCollections.SingleAsync(x => x.GrantId == saved.Id)).CollectionId);
        Assert.Equal(SoftwareGrantSetup.Bytes(winningRequest.Input.RecipientKey), saved.RecipientKey);
    }

    [FunctionalTheory][InlineData("recordUpdate", false)][InlineData("recordUpdate", true)]
    [InlineData("collectionUpdate", false)][InlineData("collectionUpdate", true)]
    [InlineData("membership", false)][InlineData("membership", true)]
    [InlineData("recordTrash", false)][InlineData("recordTrash", true)]
    [InlineData("collectionTrash", false)][InlineData("collectionTrash", true)]
    [InlineData("protection", false)][InlineData("protection", true)]
    public async Task GivenNativeWriterInEitherAcquisitionOrder_WhenGranting_ThenSerializeWithoutDeadlock(string writer, bool grantFirst)
    {
        using var s = await SoftwareGrantSetup.Create(fixture); var request = s.Request();
        ProtectionChangeRequest? protection = null;
        if (writer == "protection")
        {
            var input = new ProtectionChange(s.Owner.AccountId, 1, 1, "rewrap", s.Protection.Rewrap() with { AuthorKey = ProtectionFixture.Public(s.Author) }, []);
            var raw = SoftwareGrantSetup.Bytes(input);
            var challenge = (await new VaultProtectionStore(fixture).ChallengeAsync(s.Owner.Actor, "change-protection", ProtocolBinary.Encode(SHA256.HashData(raw)), default)).Data!;
            protection = new(s.Owner.Actor, s.Owner.Verifier, challenge.ChallengeId, s.Protection.Sign(challenge), raw, input);
        }
        async Task<string?> Write(IDbContextFactory<AppDbContext> factory, CancellationToken ct)
        {
            var time = DateTimeOffset.UtcNow;
            return writer switch
            {
                "recordUpdate" => (await new RecordUpdateStore(factory).UpdateAsync(new(s.Owner.Actor, s.Owner.Verifier, s.Items.Record.PublicId, new(1, ProfileSetup.Envelope(), time)), ct)).Error,
                "collectionUpdate" => (await new CollectionUpdateStore(factory).UpdateAsync(new(s.Owner.Actor, s.Owner.Verifier, s.Items.Collection.PublicId, new(1, ProfileSetup.Envelope(), time)), ct)).Error,
                "membership" => (await new CollectionMembershipStore(factory).SetAsync(new(s.Owner.Actor, s.Owner.Verifier, s.Items.Collection.PublicId, new(1, ProfileSetup.Envelope(), time, [s.Items.Record.PublicId], [])), ct)).Error,
                "recordTrash" => (await new RecordTrashStore(factory).TrashAsync(new(s.Owner.Actor, s.Owner.Verifier, s.Items.Record.PublicId, 1), ct)).Error,
                "collectionTrash" => (await new CollectionTrashStore(factory).TrashAsync(new(s.Owner.Actor, s.Owner.Verifier, s.Items.Collection.PublicId, 1), ct)).Error,
                _ => (await new VaultProtectionChangeStore(factory).ChangeAsync(protection!, ct)).Error
            };
        }
        var reached = Signal(); var release = Signal(); var waiting = Signal(); var holder = 0; var waiter = 0;
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var paused = new ProfileSetup.Factory(fixture, new After(c => Lock(c, "account"), async c => { holder = Pid(c); reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }));
        var observed = new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), c => { waiter = Pid(c); waiting.TrySetResult(); return Task.CompletedTask; }));
        Task<VaultResult<SoftwareGrantDetails>> grant; Task<string?> write;
        if (grantFirst) { grant = Store(paused).GrantAsync(request, ct.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); write = Write(observed, ct.Token); }
        else { write = Write(paused, ct.Token); await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); grant = Store(observed).GrantAsync(request, ct.Token); }
        try { await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Blocked(holder, waiter, ct.Token); Assert.False(grantFirst ? write.IsCompleted : grant.IsCompleted); }
        finally { release.TrySetResult(); }
        Assert.Null(await write);
        Assert.Equal(grantFirst ? null : writer == "protection" ? "vault_access_denied" : writer.EndsWith("Trash", StringComparison.Ordinal) ? "not_found" : "revision_conflict", (await grant).Error);
        await using var db = fixture.CreateContext(); Assert.Equal(grantFirst ? 1 : 0, await db.SoftwareGrants.CountAsync(x => x.PublicId == s.Grant));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenExpiryAroundGuardedWrite_WhenGranting_ThenOnlyPreviouslyAuthorizedAggregateCommits(bool after)
    {
        using var s = await SoftwareGrantSetup.Create(fixture); var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        await Sql("UPDATE cerberus.vault_access_session SET expires_at={0} WHERE handle_verifier={1}", deadline, s.Owner.Verifier);
        async Task Past(DbCommand _) { var remaining = deadline - DateTimeOffset.UtcNow; if (remaining > TimeSpan.Zero) await Task.Delay(remaining + TimeSpan.FromMilliseconds(100)); }
        DbCommandInterceptor interceptor = after ? new After(c => c.CommandText.Contains("INSERT INTO cerberus.software_grant("), Past)
            : new Before(c => c.CommandText.Contains("INSERT INTO cerberus.software_grant("), Past);
        var factory = new ProfileSetup.Factory(fixture, interceptor);
        Assert.Equal(after ? null : "vault_access_denied", (await Store(factory).GrantAsync(s.Request(), default)).Error);
        await using var db = fixture.CreateContext(); Assert.Equal(after ? 1 : 0, await db.SoftwareGrants.CountAsync(x => x.PublicId == s.Grant));
    }

    [FunctionalTheory][InlineData("record", false)][InlineData("record", true)][InlineData("collection", false)][InlineData("collection", true)]
    public async Task GivenGrantLockWait_WhenTargetUpdaterOrTrashWriterRuns_ThenTargetRemainsLockedUntilAggregateCommit(string kind, bool trash)
    {
        using var s = await SoftwareGrantSetup.Create(fixture);
        Assert.Null((await Store().GrantAsync(s.Request(), default)).Error); await Sql("UPDATE cerberus.software_grant SET state=1 WHERE public_id={0}", s.Grant);
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.software_grant WHERE public_id={s.Grant} FOR UPDATE");
        var holder = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(); var waiter = 0; var reached = Signal();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var grant = Store(new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "software_grant"), c => { waiter = Pid(c); reached.TrySetResult(); return Task.CompletedTask; }))).GrantAsync(s.Request(1), ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Blocked(holder, waiter, ct.Token);
        await using var writer = fixture.CreateContext(); await writer.Database.OpenConnectionAsync(ct.Token);
        var writerPid = await writer.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(ct.Token);
        var updateSql = "UPDATE cerberus." + kind + " SET " + (trash ? "deleted_at=statement_timestamp()" : "revision=revision+1") + " WHERE id={0}";
        var update = writer.Database.ExecuteSqlRawAsync(updateSql, [kind == "record" ? s.Items.Record.Id : s.Items.Collection.Id], ct.Token);
        try { await Blocked(waiter, writerPid, ct.Token); Assert.False(update.IsCompleted); }
        finally { await tx.CommitAsync(ct.Token); }
        Assert.Null((await grant).Error); Assert.Equal(1, await update);
        await using var db = fixture.CreateContext(); Assert.Equal(2, (await db.SoftwareGrants.SingleAsync(x => x.PublicId == s.Grant)).Revision);
    }

    [FunctionalFact]
    public async Task GivenDifferentGrantIdsAndReversedMixedTargets_WhenGrantingConcurrently_ThenBothCompleteAndUseCanonicalLocks()
    {
        using var s = await SoftwareGrantSetup.Create(fixture); var extra = await AssociationSetup.Items(fixture, s.Owner); var otherId = Guid.NewGuid();
        var first = s.Request(); var second = s.Request(grant: otherId);
        first = first with { Input = first.Input with { Targets = [..first.Input.Targets, s.Target("collection", extra.Collection.PublicId), s.Target("record", extra.Record.PublicId)] } };
        second = second with { Input = second.Input with { Targets = [s.Target("record", extra.Record.PublicId, grant: otherId), s.Target("collection", extra.Collection.PublicId, grant: otherId), ..second.Input.Targets.Reverse()] } };
        var capture1 = new Capture(); var capture2 = new Capture();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var results = await Task.WhenAll(Store(new ProfileSetup.Factory(fixture, capture1)).GrantAsync(first, ct.Token), Store(new ProfileSetup.Factory(fixture, capture2)).GrantAsync(second, ct.Token));
        Assert.All(results, result => Assert.Null(result.Error));
        foreach (var capture in new[] { capture1, capture2 })
        {
            Assert.Equal(new[] { "account", "vault_access_session", "collection", "record", "software_grant" }, capture.Tables);
            Assert.All(capture.TargetLocks, command => Assert.Contains("ORDER BY public_id FOR UPDATE", command));
        }
        Assert.Equal(capture1.TargetLocks, capture2.TargetLocks);
        await using var db = fixture.CreateContext(); Assert.Equal(2, await db.SoftwareGrants.CountAsync(x => x.AccountId == s.Owner.InternalId));
        Assert.Equal(4, await db.SoftwareGrantRecords.CountAsync(x => x.AccountId == s.Owner.InternalId));
        Assert.Equal(4, await db.SoftwareGrantCollections.CountAsync(x => x.AccountId == s.Owner.InternalId));
    }

    [FunctionalFact]
    public async Task GivenOwnerPinsCorruptedDuringAccountLockWait_WhenGranting_ThenClassifyRequiredCorruptionAndWriteNothing()
    {
        using var s = await SoftwareGrantSetup.Create(fixture);
        await using var blocker = fixture.CreateContext(); await using var tx = await blocker.Database.BeginTransactionAsync();
        await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.Owner.InternalId} FOR UPDATE");
        var holder = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync(); var waiter = 0; var reached = Signal();
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pending = Store(new ProfileSetup.Factory(fixture, new Before(c => Lock(c, "account"), c => { waiter = Pid(c); reached.TrySetResult(); return Task.CompletedTask; }))).GrantAsync(s.Request(), ct.Token);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); await Blocked(holder, waiter, ct.Token);
            await blocker.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.vault_protection SET material='bad'::bytea WHERE account_id={s.Owner.InternalId}", ct.Token);
        }
        finally { await tx.CommitAsync(ct.Token); }
        Assert.Equal("persistence_unavailable", (await pending).Error);
        await using var db = fixture.CreateContext(); Assert.False(await db.SoftwareGrants.AnyAsync(x => x.PublicId == s.Grant));
    }

    private static bool Lock(DbCommand c, string table) => c.CommandText.Contains("FROM cerberus." + table + " WHERE") && c.CommandText.Contains("FOR ");
    private static int Pid(DbCommand c) => ((NpgsqlConnection)c.Connection!).ProcessID;
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private SoftwareGrantStore Store(IDbContextFactory<AppDbContext>? factory = null) => new(factory ?? fixture);
    private async Task Sql(string statement, params object[] parameters) { await using var db = fixture.CreateContext(); await db.Database.ExecuteSqlRawAsync(statement, parameters); }
    private async Task Blocked(int holder, int waiter, CancellationToken ct)
    {
        await using var observer = fixture.CreateContext();
        while (!await observer.Database.SqlQuery<bool>($"SELECT {holder}=ANY(pg_blocking_pids({waiter})) AS \"Value\"").SingleAsync(ct)) await Task.Delay(20, ct);
        Assert.True(await observer.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT FROM pg_locks WHERE pid={waiter} AND NOT granted) AS \"Value\"").SingleAsync(ct));
    }
    private sealed class Before(Func<DbCommand, bool> matches, Func<DbCommand, Task> act) : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> result, CancellationToken ct = default)
        { if (matches(c)) await act(c); return result; }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (matches(c)) await act(c); return result; }
    }
    private sealed class After(Func<DbCommand, bool> matches, Func<DbCommand, Task> act) : DbCommandInterceptor
    {
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int result, CancellationToken ct = default)
        { if (matches(c)) await act(c); return result; }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c, CommandExecutedEventData d, DbDataReader result, CancellationToken ct = default)
        { if (matches(c)) await act(c); return result; }
    }
    private sealed class Capture : DbCommandInterceptor
    {
        internal List<string> Tables { get; } = [];
        internal List<string> TargetLocks { get; } = [];
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> result, CancellationToken ct = default)
        {
            foreach (var table in new[] { "account", "vault_access_session", "profile", "collection", "record", "software_grant" })
                if (Lock(c, table)) { Tables.Add(table); if (table is "collection" or "record") TargetLocks.Add(c.CommandText); }
            return ValueTask.FromResult(result);
        }
    }
}
