using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;

namespace ArturRios.Cerberus.Data.Tests;

public sealed partial class RecordPermanentDeleteStoreTests
{
    [FunctionalTheory]
    [InlineData("create")][InlineData("association")]
    public async Task GivenPermanentIntentHoldsOwnedParents_WhenAnotherActualWriterStarts_ThenSerializeWithoutDeadlock(string writer)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "direct", false);
        await using var db = fixture.CreateContext();
        var verifier = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        db.VaultAccessSessions.Add(new() { AccountId = w.State.InternalId, HandleVerifier = verifier,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            PolicyRevision = 1, RevocationGeneration = 1 });
        await db.SaveChangesAsync();
        w.State = w.State with { Verifier = verifier };
        await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.FolderId, w.Items.Folder.Id));
        var profile = await db.Profiles.SingleAsync(x => x.Id == w.Selection);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var deletion = Store(factory: new ProfileSetup.Factory(fixture, new BeforeTarget(() => { entered.TrySetResult(); return release.Task.WaitAsync(ct.Token); }))).DeleteAsync(w.Request(), ct.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var blocked = new PermanentDeleteSetup.LockSignal("account");
        var factory = new ProfileSetup.Factory(fixture, blocked);
        var other = writer == "create"
            ? new RecordCreateStore(factory).CreateAsync(new(w.State.Actor, w.State.Verifier, new(Guid.NewGuid(), ProfileSetup.Envelope(), DateTimeOffset.UtcNow, [], w.Items.Folder.PublicId)), ct.Token).ContinueWith(t => t.Result.Error)
            : new ProfileAssociationStore(factory).SetAsync(new(w.State.Actor, w.State.Verifier, profile.PublicId, new(profile.Revision, [], [], [])), ct.Token).ContinueWith(t => t.Result.Error);
        try { await blocked.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(other.IsCompleted); }
        finally { release.TrySetResult(); }
        Assert.Null((await deletion).Error);
        Assert.Equal(writer == "create" ? null : "revision_conflict", await other);
    }

    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenControlledRecipientEditAndOwnerPermanentDelete_WhenWaitingOnCollections_ThenOneWinnerWithoutDeadlock(bool deleteFirst)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        using var client = new ProtectionFixture();
        var recipient = await ProfileSetup.Create(fixture, client);
        await using var db = fixture.CreateContext();
        db.CollectionRecords.Add(new() { AccountId = w.State.InternalId, CollectionId = w.Items.Collection.Id, RecordId = w.Record.Id });
        await db.SaveChangesAsync();
        await AssociationSetup.Grant(fixture, w.State, w.Owner, recipient, client, w.Items.Collection, CollectionGrantAccess.ReadWrite);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blocked = new PermanentDeleteSetup.LockSignal("collection");
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var firstFactory = new ProfileSetup.Factory(fixture, new BeforeTarget(() => { entered.TrySetResult(); return release.Task.WaitAsync(ct.Token); }));
        var secondFactory = new ProfileSetup.Factory(fixture, blocked);
        Task<string?> first;
        Task<string?> second;
        if (deleteFirst)
        {
            first = Store(factory: firstFactory).DeleteAsync(w.Request(), ct.Token).ContinueWith(t => t.Result.Error);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = new RecordUpdateStore(secondFactory).UpdateAsync(new(recipient.Actor, recipient.Verifier, w.Record.PublicId, new(1, ProfileSetup.Envelope(), DateTimeOffset.UtcNow)), ct.Token).ContinueWith(t => t.Result.Error);
        }
        else
        {
            first = new RecordUpdateStore(firstFactory).UpdateAsync(new(recipient.Actor, recipient.Verifier, w.Record.PublicId, new(1, ProfileSetup.Envelope(), DateTimeOffset.UtcNow)), ct.Token).ContinueWith(t => t.Result.Error);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            second = Store(factory: secondFactory).DeleteAsync(w.Request(), ct.Token).ContinueWith(t => t.Result.Error);
        }
        try { await blocked.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(second.IsCompleted); }
        finally { release.TrySetResult(); }
        Assert.Null(await first);
        Assert.Equal(deleteFirst ? "not_found" : "revision_conflict", await second);
        Assert.Equal(!deleteFirst, await db.Records.AnyAsync(x => x.Id == w.Record.Id));
    }

    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenTerminalRecordAwaitingLedger_WhenRotatingSurvivingInventory_ThenTrashIsRequiredButTerminalCipherIsExcluded(bool includeTerminal)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        var unavailable = new PermanentDeleteSetup.LedgerBoundary(Ledger, () => throw new IOException("fixture ledger outage"));
        Assert.Equal("persistence_unavailable", (await Store(unavailable).DeleteAsync(w.Request(), default)).Error);
        var content = new List<ContentReplacement> {
            new("account", w.State.AccountId, 1, ProfileSetup.Envelope(2)),
            new("record", w.Items.Record.PublicId, 1, ProfileSetup.Envelope(2)),
            new("folder", w.Items.Folder.PublicId, 1, ProfileSetup.Envelope(2)),
            new("collection", w.Items.Collection.PublicId, 1, ProfileSetup.Envelope(2)) };
        if (includeTerminal) content.Add(new("record", w.Record.PublicId, 1, ProfileSetup.Envelope(2)));
        var change = new ProtectionChange(w.State.AccountId, 1, 1, "rotate-content", w.Owner.Rewrap(), content.ToArray());
        var raw = JsonSerializer.SerializeToUtf8Bytes(change, ProtectionFixture.Json);
        var challenge = (await new VaultProtectionStore(fixture).ChallengeAsync(w.State.Actor, "change-protection", ProtocolBinary.Encode(SHA256.HashData(raw)), default)).Data!;
        var result = await new VaultProtectionChangeStore(fixture).ChangeAsync(new(w.State.Actor, w.State.Verifier, challenge.ChallengeId, w.Owner.Sign(challenge), raw, change), default);
        Assert.Equal(includeTerminal ? "validation_failed" : null, result.Error);
        await using var db = fixture.CreateContext();
        var pending = await db.Records.AsNoTracking().SingleAsync(x => x.Id == w.Record.Id);
        Assert.Equal(w.Record.Envelope, pending.Envelope);
        Assert.Equal(1, pending.Revision);
        Assert.Equal(!includeTerminal, (await db.VaultUnlockChallenges.SingleAsync(x => x.PublicId == challenge.ChallengeId)).Consumed);
        Assert.Equal(includeTerminal ? 1 : 2, (await db.Records.SingleAsync(x => x.Id == w.Items.Record.Id)).Revision);
    }

    [FunctionalFact]
    public async Task GivenUnrelatedDeepInventory_WhenPermanentlyDeleting_ThenAnalyzeOneTargetAndOwnOrderedLocks()
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "direct", false);
        await using var db = fixture.CreateContext();
        await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.FolderId, w.Items.Folder.Id));
        db.CollectionRecords.Add(new() { AccountId = w.State.InternalId, CollectionId = w.Items.Collection.Id, RecordId = w.Record.Id });
        long? parent = null;
        for (var n = 0; n < 40; n++)
        {
            var folder = new VaultFolder { AccountId = w.State.InternalId, PublicId = Guid.NewGuid(), Envelope = w.Items.Folder.Envelope, EditedAt = w.Items.Folder.EditedAt, ParentFolderId = parent };
            db.Folders.Add(folder); await db.SaveChangesAsync(); parent = folder.Id;
            db.Records.Add(new() { AccountId = w.State.InternalId, PublicId = Guid.NewGuid(), Envelope = w.Record.Envelope, EditedAt = w.Record.EditedAt, FolderId = folder.Id });
        }
        await db.SaveChangesAsync();
        var capture = new CaptureTarget(fixture);
        Assert.Null((await Store(factory: new ProfileSetup.Factory(fixture, capture)).DeleteAsync(w.Request(), default)).Error);
        Assert.NotNull(capture.Plan);
        using var plan = JsonDocument.Parse(capture.Plan);
        var scans = Nodes(plan.RootElement).Where(x => x.TryGetProperty("CTE Name", out var name) && name.GetString() == "candidates").ToArray();
        Assert.NotEmpty(scans);
        Assert.All(scans, x => Assert.InRange(x.GetProperty("Actual Rows").GetDecimal(), 0, 1));
        Assert.Contains(scans, x => x.GetProperty("Actual Rows").GetDecimal() == 1);
        var tables = new[] { "account", "vault_access_session", "profile", "collection", "folder", "record" };
        var positions = tables.Select(t => capture.Locks.FindIndex(x => x.Contains("cerberus." + t + " "))).ToArray();
        Assert.All(positions, p => Assert.True(p >= 0));
        for (var i = 1; i < positions.Length; i++) Assert.True(positions[i - 1] < positions[i]);
        Assert.Contains("ORDER BY p.public_id", capture.Locks[positions[2]]);
        Assert.Contains("ORDER BY c.public_id", capture.Locks[positions[3]]);
    }

    private static IEnumerable<JsonElement> Nodes(JsonElement node)
    {
        if (node.ValueKind == JsonValueKind.Object)
        { yield return node; foreach (var property in node.EnumerateObject()) foreach (var child in Nodes(property.Value)) yield return child; }
        if (node.ValueKind == JsonValueKind.Array) foreach (var value in node.EnumerateArray()) foreach (var child in Nodes(value)) yield return child;
    }
    private sealed class BeforeTarget(Func<Task> callback) : DbCommandInterceptor
    {
        private bool used;
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (!used && command.CommandText.Contains("FROM cerberus.record ") && command.CommandText.Contains("FOR ")) { used = true; await callback(); } return result; }
    }
    private sealed class CaptureTarget(PostgresFixture postgres) : DbCommandInterceptor
    {
        internal List<string> Locks { get; } = [];
        internal string? Plan { get; private set; }
        private void Note(DbCommand command) { if (command.CommandText.Contains("FOR UPDATE") || command.CommandText.Contains("FOR NO KEY UPDATE") || command.CommandText.Contains("FOR SHARE")) Locks.Add(command.CommandText); }
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default) { Note(command); return ValueTask.FromResult(result); }
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        {
            Note(command);
            if (Plan is null && command.CommandText.StartsWith("WITH RECURSIVE"))
            {
                await using var db = postgres.CreateContext(); await db.Database.OpenConnectionAsync(ct);
                await using var explain = db.Database.GetDbConnection().CreateCommand(); explain.CommandText = "EXPLAIN (ANALYZE, FORMAT JSON) " + command.CommandText;
                foreach (var p in command.Parameters.Cast<NpgsqlParameter>()) explain.Parameters.Add(p.Clone());
                Plan = (string)(await explain.ExecuteScalarAsync(ct))!;
            }
            return result;
        }
    }
}
