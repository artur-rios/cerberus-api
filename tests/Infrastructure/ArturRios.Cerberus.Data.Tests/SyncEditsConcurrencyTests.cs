using System.Data.Common;
using ArturRios.Cerberus.Data.Sync;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class SyncEditsConcurrencyTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenRecordThenCollectionBatchAndOnlineRecipientWriter_WhenUpdatingSameSharedRecord_ThenSerializeCollectionsBeforeTargetsWithoutDeadlock()
    {
        using var ownerKey = new ProtectionFixture(); using var key = new ProtectionFixture();
        var owner = await ProfileSetup.Create(fixture, ownerKey); var recipient = await ProfileSetup.Create(fixture, key);
        var items = await AssociationSetup.Items(fixture, owner);
        await using (var db = fixture.CreateContext())
        {
            db.CollectionRecords.Add(new() { AccountId = owner.InternalId, CollectionId = items.Collection.Id, RecordId = items.Record.Id }); await db.SaveChangesAsync();
        }
        await AssociationSetup.Grant(fixture, owner, ownerKey, recipient, key, items.Collection, CollectionGrantAccess.ReadWrite);
        var time = DateTimeOffset.UtcNow.AddDays(1);
        var batch = new SyncEditsRequest(owner.Actor, owner.Verifier, new([
            new SyncRecordUpdate(Guid.NewGuid(), new(items.Record.PublicId, ProfileSetup.Envelope(), time)),
            new SyncCollectionUpdate(Guid.NewGuid(), new(items.Collection.PublicId, ProfileSetup.Envelope(), time))]));
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reached = Signal(); var release = Signal(); var started = Signal(); int holder = 0, waiter = 0;
        var paused = new ProfileSetup.Factory(fixture, new After(c => c.CommandText.StartsWith("SELECT FROM cerberus.record ", StringComparison.Ordinal),
            async c => { holder = ((NpgsqlConnection)c.Connection!).ProcessID; reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }));
        var observed = new ProfileSetup.Factory(fixture, new Before(c => c.CommandText.StartsWith("SELECT FROM cerberus.account ", StringComparison.Ordinal),
            c => { waiter = ((NpgsqlConnection)c.Connection!).ProcessID; started.TrySetResult(); return Task.CompletedTask; }));
        var upload = new SyncEditsStore(paused, new() { MaxPageSize = 100 }).UploadAsync(batch, ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var online = new RecordUpdateStore(observed).UpdateAsync(new(recipient.Actor, recipient.Verifier, items.Record.PublicId,
            new(1, ProfileSetup.Envelope(), time.AddSeconds(1))), ct.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10)); await using var observer = fixture.CreateContext();
            while (!await observer.Database.SqlQuery<bool>($"SELECT {holder}=ANY(pg_blocking_pids({waiter})) AS \"Value\"").SingleAsync(ct.Token)) await Task.Delay(20, ct.Token);
            Assert.False(online.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var result = await upload; Assert.Null(result.Error); Assert.All(result.Details!.Outcomes, o => Assert.Equal("applied", o.Status));
        Assert.Equal("revision_conflict", (await online).Error);
        await using var check = fixture.CreateContext(); Assert.Equal(2, await check.Records.Where(x => x.Id == items.Record.Id).Select(x => x.Revision).SingleAsync());
        Assert.Equal(2, await check.Collections.Where(x => x.Id == items.Collection.Id).Select(x => x.Revision).SingleAsync());
        Assert.Equal(2, await check.SyncOperations.CountAsync(x => x.AccountId == owner.InternalId));
    }
    [FunctionalFact]
    public async Task GivenConcurrentIdenticalOperation_WhenUploading_ThenReturnSameOutcomeAndAllocateOnlyOneWrite()
    {
        using var key = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, key);
        var edit = new SyncRecordCreate(Guid.NewGuid(), new(Guid.NewGuid(), ProfileSetup.Envelope(), DateTimeOffset.FromUnixTimeSeconds(1000), []));
        var request = new SyncEditsRequest(s.Actor, s.Verifier, new([edit])); var store = new SyncEditsStore(fixture, new() { MaxPageSize = 100 });
        var results = await Task.WhenAll(store.UploadAsync(request, default), store.UploadAsync(request, default));
        Assert.All(results, r => Assert.Null(r.Error)); Assert.Equal(results[0].Details!.Outcomes, results[1].Details!.Outcomes);
        await using var db = fixture.CreateContext(); Assert.Equal(1, await db.SyncOperations.CountAsync(x => x.AccountId == s.InternalId));
        Assert.Equal(1, await db.Records.Where(x => x.PublicId == edit.ResourceId).Select(x => x.Revision).SingleAsync());
    }
    [FunctionalTheory]
    [InlineData("closing", "not_found")][InlineData("policy", "vault_access_denied")][InlineData("expiry", "vault_access_denied")]
    public async Task GivenAuthorityLostDuringRealAccountLockWait_WhenUploading_ThenRecheckAndRollbackBeforeMutation(string change, string error)
    {
        using var key = new ProtectionFixture(); var s = await ProfileSetup.Create(fixture, key);
        var edit = new SyncRecordCreate(Guid.NewGuid(), new(Guid.NewGuid(), ProfileSetup.Envelope(), DateTimeOffset.FromUnixTimeSeconds(1000), []));
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        await using var blocker = fixture.CreateContext();
        if (change == "expiry") await blocker.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ExpiresAt, deadline));
        await using var tx = await blocker.Database.BeginTransactionAsync(); await blocker.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
        var holder = await blocker.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        var reached = Signal(); int waiter = 0; using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var observed = new ProfileSetup.Factory(fixture, new Before(c => c.CommandText.StartsWith("SELECT FROM cerberus.account ", StringComparison.Ordinal),
            c => { waiter = ((NpgsqlConnection)c.Connection!).ProcessID; reached.TrySetResult(); return Task.CompletedTask; }));
        var pending = new SyncEditsStore(observed, new() { MaxPageSize = 100 }).UploadAsync(new(s.Actor, s.Verifier, new([edit])), ct.Token);
        try
        {
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); await using var observer = fixture.CreateContext();
            while (!await observer.Database.SqlQuery<bool>($"SELECT {holder}=ANY(pg_blocking_pids({waiter})) AS \"Value\"").SingleAsync(ct.Token)) await Task.Delay(20, ct.Token);
            if (change == "closing") await blocker.Accounts.Where(x => x.Id == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
            if (change == "policy") await blocker.Accounts.Where(x => x.Id == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.PolicyRevision, 2));
            if (change == "expiry") { var delay = deadline - DateTimeOffset.UtcNow; if (delay > TimeSpan.Zero) await Task.Delay(delay + TimeSpan.FromMilliseconds(100), ct.Token); }
        }
        finally { await tx.CommitAsync(); }
        var result = await pending; Assert.Equal(error, result.Error); Assert.Null(result.Details);
        await using var db = fixture.CreateContext(); Assert.False(await db.Records.AnyAsync(x => x.PublicId == edit.ResourceId)); Assert.False(await db.SyncOperations.AnyAsync(x => x.AccountId == s.InternalId));
    }
    [FunctionalFact]
    public async Task GivenTwoRecipientsWithOpposingItemOrder_WhenUploadingEqualTimeEdits_ThenSerializeAndApplyBothBatchesWithoutDeadlock()
    {
        using var ownerKey = new ProtectionFixture(); using var key1 = new ProtectionFixture(); using var key2 = new ProtectionFixture();
        var owner = await ProfileSetup.Create(fixture, ownerKey); var first = await ProfileSetup.Create(fixture, key1); var second = await ProfileSetup.Create(fixture, key2);
        var a = await AssociationSetup.Items(fixture, owner); var b = await AssociationSetup.Items(fixture, owner);
        await using (var db = fixture.CreateContext())
        {
            db.CollectionRecords.AddRange(new CollectionRecord { AccountId = owner.InternalId, CollectionId = a.Collection.Id, RecordId = a.Record.Id },
                new CollectionRecord { AccountId = owner.InternalId, CollectionId = a.Collection.Id, RecordId = b.Record.Id });
            await db.SaveChangesAsync();
        }
        await AssociationSetup.Grant(fixture, owner, ownerKey, first, key1, a.Collection, CollectionGrantAccess.ReadWrite);
        await AssociationSetup.Grant(fixture, owner, ownerKey, second, key2, a.Collection, CollectionGrantAccess.ReadWrite);
        var time = DateTimeOffset.UtcNow.AddDays(1);
        SyncRecordUpdate Edit(Guid id) => new(Guid.NewGuid(), new(id, ProfileSetup.Envelope(), time));
        var one = new SyncEditsRequest(first.Actor, first.Verifier, new([Edit(a.Record.PublicId), Edit(b.Record.PublicId)]));
        var two = new SyncEditsRequest(second.Actor, second.Verifier, new([Edit(b.Record.PublicId), Edit(a.Record.PublicId)]));
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var reached = Signal(); var release = Signal(); var started = Signal(); int holder = 0, waiter = 0;
        var paused = new ProfileSetup.Factory(fixture, new After(c => c.CommandText.StartsWith("SELECT FROM cerberus.record ", StringComparison.Ordinal),
            async c => { holder = ((NpgsqlConnection)c.Connection!).ProcessID; reached.TrySetResult(); await release.Task.WaitAsync(ct.Token); }));
        var observed = new ProfileSetup.Factory(fixture, new Before(c => c.CommandText.StartsWith("SELECT FROM cerberus.account ", StringComparison.Ordinal),
            c => { waiter = ((NpgsqlConnection)c.Connection!).ProcessID; started.TrySetResult(); return Task.CompletedTask; }));
        var upload1 = new SyncEditsStore(paused, new() { MaxPageSize = 100 }).UploadAsync(one, ct.Token);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var upload2 = new SyncEditsStore(observed, new() { MaxPageSize = 100 }).UploadAsync(two, ct.Token);
        try
        {
            await started.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await using var observer = fixture.CreateContext();
            while (!await observer.Database.SqlQuery<bool>($"SELECT {holder}=ANY(pg_blocking_pids({waiter})) AS \"Value\"").SingleAsync(ct.Token))
                await Task.Delay(20, ct.Token);
            Assert.False(upload2.IsCompleted);
        }
        finally { release.TrySetResult(); }
        var results = await Task.WhenAll(upload1, upload2); Assert.All(results, r => Assert.Null(r.Error));
        Assert.All(results.SelectMany(r => r.Details!.Outcomes), r => Assert.Equal("applied", r.Status));
        Assert.True(results[1].Details!.Outcomes.Min(x => x.ServerSequence) > results[0].Details!.Outcomes.Max(x => x.ServerSequence));
        await using var check = fixture.CreateContext();
        Assert.All(await check.Records.Where(x => x.PublicId == a.Record.PublicId || x.PublicId == b.Record.PublicId).ToListAsync(), r => Assert.Equal(3, r.Revision));
        Assert.Equal(4, await check.SyncOperations.CountAsync(x => x.AccountId == first.InternalId || x.AccountId == second.InternalId));
    }
    private static TaskCompletionSource Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Before(Func<DbCommand, bool> match, Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        private bool used;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c, CommandEventData d, InterceptionResult<int> r, CancellationToken ct = default)
        { if (!used && match(c)) { used = true; await action(c); } return r; }
    }
    private sealed class After(Func<DbCommand, bool> match, Func<DbCommand, Task> action) : DbCommandInterceptor
    {
        private bool used;
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c, CommandExecutedEventData d, int r, CancellationToken ct = default)
        { if (!used && match(c)) { used = true; await action(c); } return r; }
    }
}
