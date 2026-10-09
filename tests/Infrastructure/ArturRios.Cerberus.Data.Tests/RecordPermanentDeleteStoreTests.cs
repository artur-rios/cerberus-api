using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public sealed partial class RecordPermanentDeleteStoreTests(PostgresFixture fixture) : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("cerberus-record-erasure-").FullName;
    private FileErasureLedger Ledger => new(directory);
    private RecordPermanentDeleteStore Store(IErasureLedger? ledger = null, IDbContextFactory<AppDbContext>? factory = null) =>
        new(factory ?? fixture, ledger ?? Ledger, new CerberusOptions { RetentionInterval = "00:00:30" }, TimeProvider.System);

    // Missing terminal/purge/ledger side effects or an owner-scope escape fails these cases.
    [FunctionalTheory]
    [InlineData("wide", false)][InlineData("direct", false)][InlineData("folder", false)]
    [InlineData("collection", false)][InlineData("descendant", false)]
    [InlineData("wide", true)][InlineData("direct", true)][InlineData("folder", true)]
    [InlineData("collection", true)][InlineData("descendant", true)]
    public async Task GivenCurrentOwnedActiveOrTrashedScope_WhenPermanentlyDeleting_ThenDurableTerminalEventAndOnlyTargetRemoved(string route, bool trash)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, route, trash);
        var result = await Store().DeleteAsync(w.Request(), default);
        Assert.Null(result.Error);
        Assert.Equal(w.Record.PublicId, result.Data!.RecordId);
        Assert.Equal(TimeSpan.Zero, result.Data.DeletedAt.Offset);
        Assert.Equal(0, result.Data.DeletedAt.Ticks % 10);
        await using var db = fixture.CreateContext();
        Assert.False(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
        Assert.False(await db.ProfileRecords.AnyAsync(x => x.RecordId == w.Record.Id));
        Assert.False(await db.CollectionRecords.AnyAsync(x => x.RecordId == w.Record.Id));
        Assert.False(await db.TrashEntries.AnyAsync(x => x.ResourceKind == "record" && x.ResourceId == w.Record.PublicId));
        var terminal = await db.TerminalErasures.SingleAsync(x => x.ResourceKind == "record" && x.ResourceId == w.Record.PublicId);
        Assert.Equal(result.Data.DeletedAt, terminal.DeletedAt);
        var events = new List<ErasureEntry>();
        await foreach (var entry in Ledger.ReadAsync(default)) events.Add(entry);
        Assert.Equal(new ErasureEntry(w.Record.PublicId, "record", terminal.DeletedAt), Assert.Single(events));
        Assert.NotNull(await db.Folders.SingleOrDefaultAsync(x => x.Id == w.Items.Folder.Id));
        Assert.NotNull(await db.Collections.SingleOrDefaultAsync(x => x.Id == w.Items.Collection.Id));
        Assert.True(await db.Records.AnyAsync(x => x.Id == w.Items.Record.Id));
        var session = await db.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == w.State.Verifier);
        Assert.Equal(w.Selection, session.ProfileId);
        Assert.False(session.Revoked);
        Assert.Equal("not_found", (await Store().DeleteAsync(w.Request(), default)).Error);
        Assert.Equal("not_found", (await new RecordReadStore(fixture).ReadAsync(new(w.State.Actor, w.State.Verifier, w.Record.PublicId), default)).Error);
    }

    [FunctionalTheory]
    [InlineData("stale", "revision_conflict")][InlineData("missing", "not_found")]
    [InlineData("unselected", "not_found")][InlineData("expired", "vault_access_denied")]
    [InlineData("revoked", "vault_access_denied")][InlineData("future", "vault_access_denied")]
    [InlineData("policy", "vault_access_denied")][InlineData("generation", "vault_access_denied")]
    [InlineData("dangling", "vault_access_denied")][InlineData("closing", "not_found")]
    [InlineData("actor", "not_found")][InlineData("handle", "vault_access_denied")]
    [InlineData("terminal", "not_found")][InlineData("ancestorTrash", "not_found")]
    public async Task GivenInvalidCurrentAuthorityOrState_WhenPermanentlyDeleting_ThenNoIntentOrLedgerOrRemoval(string kind, string error)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        await using var db = fixture.CreateContext();
        var request = w.Request();
        if (kind == "stale") request = request with { ExpectedRevision = 99 };
        if (kind == "missing") request = request with { RecordId = Guid.NewGuid() };
        if (kind == "actor") request = request with { Actor = Guid.NewGuid() };
        if (kind == "handle") request = request with { AccessVerifier = new string('a', 64) };
        if (kind is "unselected" or "dangling")
        {
            var p = await AssociationSetup.Profile(fixture, w.State, w.Owner);
            await db.VaultAccessSessions.Where(x => x.HandleVerifier == w.State.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ProfileId, kind == "dangling" ? long.MaxValue : p));
        }
        if (kind == "expired") await db.VaultAccessSessions.Where(x => x.HandleVerifier == w.State.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        if (kind == "future") await db.VaultAccessSessions.Where(x => x.HandleVerifier == w.State.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.IssuedAt, DateTimeOffset.UtcNow.AddHours(1)));
        if (kind == "revoked") await db.VaultAccessSessions.Where(x => x.HandleVerifier == w.State.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true));
        if (kind == "policy") await db.VaultAccessSessions.Where(x => x.HandleVerifier == w.State.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.PolicyRevision, 99));
        if (kind == "generation") await db.VaultAccessSessions.Where(x => x.HandleVerifier == w.State.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.RevocationGeneration, 99));
        if (kind == "closing") await db.Accounts.Where(x => x.Id == w.State.InternalId).ExecuteUpdateAsync(x => x.SetProperty(v => v.State, AccountState.ClosurePending));
        if (kind == "terminal") { db.TerminalErasures.Add(new() { ResourceKind = "record", ResourceId = w.Record.PublicId, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        if (kind == "ancestorTrash")
        {
            await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.FolderId, w.Items.Folder.Id));
            await db.Folders.Where(x => x.Id == w.Items.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow));
        }
        var before = await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId);
        Assert.Equal(error, (await Store().DeleteAsync(request, default)).Error);
        Assert.Equal(before, await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
    }

    [FunctionalTheory]
    [InlineData(CollectionGrantAccess.ReadOnly, false)][InlineData(CollectionGrantAccess.ReadWrite, false)]
    [InlineData(CollectionGrantAccess.ReadOnly, true)][InlineData(CollectionGrantAccess.ReadWrite, true)]
    public async Task GivenNativeVisibleForeignOwner_WhenDeletingWithStaleRevisionAndBadCipher_Then403WithoutLedger(CollectionGrantAccess access, bool selected)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        using var client = new ProtectionFixture();
        var recipient = await ProfileSetup.Create(fixture, client);
        await using var db = fixture.CreateContext();
        db.CollectionRecords.Add(new() { AccountId = w.State.InternalId, CollectionId = w.Items.Collection.Id, RecordId = w.Record.Id });
        await db.SaveChangesAsync();
        await AssociationSetup.Grant(fixture, w.State, w.Owner, recipient, client, w.Items.Collection, access);
        if (selected)
        {
            using var key = new ProtectionFixture();
            var input = ProfileSetup.Input(recipient, client, key) with { CollectionIds = [w.Items.Collection.PublicId] };
            Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(recipient.Actor, recipient.Verifier, input), default)).Error);
            var id = await db.Profiles.Where(x => x.PublicId == input.ProfileId).Select(x => x.Id).SingleAsync();
            await db.VaultAccessSessions.Where(x => x.HandleVerifier == recipient.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ProfileId, id));
        }
        await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Envelope, "bad"u8.ToArray()));
        var before = await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId);
        Assert.Equal("vault_access_denied", (await Store().DeleteAsync(new(recipient.Actor, recipient.Verifier, w.Record.PublicId, 99), default)).Error);
        Assert.Equal(before, await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
    }

    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenOwnedDamagedCipherAndMaximumRevision_WhenDeleting_ThenEraseWithoutDecryptionOrRevisionIncrement(bool trash)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", trash);
        await using var db = fixture.CreateContext();
        await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Envelope, "corrupt"u8.ToArray()).SetProperty(v => v.Revision, ProtocolBinary.MaxInteger));
        Assert.Null((await Store().DeleteAsync(w.Request() with { ExpectedRevision = ProtocolBinary.MaxInteger }, default)).Error);
        Assert.False(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
    }

    [FunctionalTheory]
    [InlineData("profile")][InlineData("collection")][InlineData("folder")]
    public async Task GivenActiveDirectParent_WhenDeleting_ThenAdvanceStructuralMetadataOnceAndKeepCipher(string parent)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, parent == "profile" ? "direct" : parent, false);
        await using var db = fixture.CreateContext();
        var before = await PermanentDeleteSetup.Parent(fixture, w, parent);
        Assert.Null((await Store().DeleteAsync(w.Request(), default)).Error);
        var after = await PermanentDeleteSetup.Parent(fixture, w, parent);
        Assert.Equal(before.Revision + 1, after.Revision);
        Assert.True(after.Sequence > before.Sequence);
        Assert.Equal(before.Envelope, after.Envelope);
        Assert.Equal(before.EditedAt, after.EditedAt);
        Assert.Equal("not_found", (await Store().DeleteAsync(w.Request(), default)).Error);
        Assert.Equal(after, await PermanentDeleteSetup.Parent(fixture, w, parent));
    }

    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenLedgerFailureAfterCommittedIntent_WhenDeletingThenRunningWorker_ThenDenyAllWritersAndRecoverDurably(bool failAfterLedger)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        var ledger = new PermanentDeleteSetup.LedgerBoundary(Ledger, async () =>
        {
            await using var db = fixture.CreateContext();
            Assert.True(await db.TerminalErasures.AnyAsync(x => x.ResourceKind == "record" && x.ResourceId == w.Record.PublicId));
            Assert.True(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
            Assert.Equal("not_found", (await new RecordReadStore(fixture).ReadAsync(new(w.State.Actor, w.State.Verifier, w.Record.PublicId), default)).Error);
            if (!failAfterLedger) throw new IOException("fixture ledger unavailable");
        }, failAfterLedger ? () => throw new IOException("fixture crash after fsync") : null);
        Assert.Equal("persistence_unavailable", (await Store(ledger).DeleteAsync(w.Request(), default)).Error);
        await using var db = fixture.CreateContext();
        Assert.True(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
        var work = await db.RetentionWorkItems.SingleAsync(x => x.OperationKey == "record-purge/" + w.Record.PublicId);
        Assert.Null(work.CompletedAt);
        Assert.Equal("not_found", (await new RecordMoveStore(fixture).MoveAsync(new(w.State.Actor, w.State.Verifier, w.Record.PublicId, w.Record.Revision, null), default)).Error);
        Assert.Equal("not_found", (await new RecordTrashStore(fixture).TrashAsync(new(w.State.Actor, w.State.Verifier, w.Record.PublicId, w.Record.Revision), default)).Error);
        Assert.Equal("revision_conflict", (await new RecordCreateStore(fixture).CreateAsync(new(w.State.Actor, w.State.Verifier, new(w.Record.PublicId, ProfileSetup.Envelope(), DateTimeOffset.UtcNow, [], null)), default)).Error);
        await db.RetentionWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ClaimExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var workStore = new RetentionWorkStore(fixture);
        var claim = await workStore.TryClaimAsync(work.PublicId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(claim);
        await new RecordPurgeHandler(fixture, Ledger).ExecuteAsync(claim!, default);
        Assert.True(await workStore.TryCompleteAsync(claim!, DateTimeOffset.UtcNow, default));
        Assert.False(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
        Assert.True(await db.TerminalErasures.AnyAsync(x => x.ResourceKind == "record" && x.ResourceId == w.Record.PublicId));
    }

    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenOtherKindsSharingRecordGuid_WhenPermanentlyDeleting_ThenPreserveTheirRowsAndAccess(bool trash)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", trash);
        await using var db = fixture.CreateContext();
        await db.Folders.Where(x => x.Id == w.Items.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.PublicId, w.Record.PublicId));
        await db.Collections.Where(x => x.Id == w.Items.Collection.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.PublicId, w.Record.PublicId));
        Assert.Null((await Store().DeleteAsync(w.Request(), default)).Error);
        Assert.True(await db.Folders.AnyAsync(x => x.PublicId == w.Record.PublicId));
        Assert.True(await db.Collections.AnyAsync(x => x.PublicId == w.Record.PublicId));
        Assert.False(await db.TerminalErasures.AnyAsync(x => x.ResourceKind != "record" && x.ResourceId == w.Record.PublicId));
        Assert.Null((await new RecordMoveStore(fixture).MoveAsync(new(w.State.Actor, w.State.Verifier, w.Items.Record.PublicId, 1, w.Record.PublicId), default)).Error);
    }

    [FunctionalTheory]
    [InlineData("account")][InlineData("session")][InlineData("profile")][InlineData("collection")][InlineData("folder")][InlineData("record")]
    public async Task GivenExpiryDuringAnyRequiredLockWait_WhenDeletingStaleRevision_Then403BeforeConflictAndNoIntent(string kind)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "direct", false);
        await using var db = fixture.CreateContext();
        await db.Records.Where(x => x.Id == w.Record.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.FolderId, w.Items.Folder.Id));
        db.CollectionRecords.Add(new() { AccountId = w.State.InternalId, CollectionId = w.Items.Collection.Id, RecordId = w.Record.Id });
        await db.SaveChangesAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(2);
        await db.VaultAccessSessions.Where(x => x.HandleVerifier == w.State.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ExpiresAt, deadline));
        var before = await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId);
        await using var held = fixture.CreateContext();
        await using var transaction = await held.Database.BeginTransactionAsync();
        var table = kind == "session" ? "vault_access_session" : kind;
        var id = kind switch { "account" => w.State.InternalId, "profile" => w.Selection!.Value, "collection" => w.Items.Collection.Id, "folder" => w.Items.Folder.Id, _ => w.Record.Id };
        if (kind == "session") await held.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE handle_verifier={w.State.Verifier} FOR UPDATE");
        else { var lockSql="SELECT FROM cerberus." + table + " WHERE id={0} FOR UPDATE"; await held.Database.ExecuteSqlRawAsync(lockSql, id); }
        var signal = new PermanentDeleteSetup.LockSignal(table);
        using var ct = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var pending = Store(factory: new ProfileSetup.Factory(fixture, signal)).DeleteAsync(w.Request() with { ExpectedRevision = 99 }, ct.Token);
        await signal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(pending.IsCompleted);
        var remaining = deadline - DateTimeOffset.UtcNow;
        if (remaining > TimeSpan.Zero) await Task.Delay(remaining + TimeSpan.FromMilliseconds(50));
        await transaction.CommitAsync();
        Assert.Equal("vault_access_denied", (await pending).Error);
        Assert.Equal(before, await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
    }

    [FunctionalFact]
    public async Task GivenCancelledCaller_WhenDeleting_ThenPropagateWithoutIntent()
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        var before = await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId);
        using var ct = new CancellationTokenSource();
        await ct.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().DeleteAsync(w.Request(), ct.Token));
        Assert.Equal(before, await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId));
    }

    public void Dispose() => Directory.Delete(directory, recursive: true);
}

internal static class PermanentDeleteSetup
{
    internal sealed class World(ProtectionFixture owner, ProtectionFixture key, ProfileSetup.State state, AssociationSetup.ResourceSet items, VaultRecord record, long? selection) : IDisposable
    {
        internal ProtectionFixture Owner => owner;
        internal ProfileSetup.State State { get; set; } = state;
        internal AssociationSetup.ResourceSet Items => items;
        internal VaultRecord Record => record;
        internal long? Selection => selection;
        internal RecordPermanentDeleteRequest Request() => new(State.Actor, State.Verifier, record.PublicId, record.Revision);
        public void Dispose() { owner.Dispose(); key.Dispose(); }
    }
    internal static async Task<World> Create(PostgresFixture fixture, string route, bool trash)
    {
        var owner = new ProtectionFixture();
        var key = new ProtectionFixture();
        var s = await ProfileSetup.Create(fixture, owner);
        var items = await AssociationSetup.Items(fixture, s);
        await using var db = fixture.CreateContext();
        var target = new VaultRecord { AccountId = s.InternalId, PublicId = Guid.NewGuid(), Envelope = JsonSerializer.SerializeToUtf8Bytes(ProfileSetup.Envelope(), ProtectionFixture.Json), EditedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), FolderId = route is "folder" or "descendant" ? items.Folder.Id : null };
        db.Records.Add(target);
        await db.SaveChangesAsync();
        if (route == "collection") db.CollectionRecords.Add(new() { AccountId = s.InternalId, CollectionId = items.Collection.Id, RecordId = target.Id });
        if (route == "descendant") db.CollectionFolders.Add(new() { AccountId = s.InternalId, CollectionId = items.Collection.Id, FolderId = items.Folder.Id });
        await db.SaveChangesAsync();
        long? selection = null;
        if (route != "wide")
        {
            var input = ProfileSetup.Input(s, owner, key) with { RecordIds = route == "direct" ? [target.PublicId] : [], FolderIds = route == "folder" ? [items.Folder.PublicId] : [], CollectionIds = route is "collection" or "descendant" ? [items.Collection.PublicId] : [] };
            Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor, s.Verifier, input), default)).Error);
            selection = await db.Profiles.Where(x => x.PublicId == input.ProfileId).Select(x => x.Id).SingleAsync();
        }
        if (trash)
        {
            Assert.Null((await new RecordTrashStore(fixture).TrashAsync(new(s.Actor, s.Verifier, target.PublicId, target.Revision), default)).Error);
            target = await db.Records.AsNoTracking().SingleAsync(x => x.Id == target.Id);
        }
        if (selection is not null) await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ProfileId, selection));
        return new(owner, key, s, items, target, selection);
    }
    internal static async Task<string> Snapshot(PostgresFixture fixture, long account)
    {
        await using var db = fixture.CreateContext();
        return JsonSerializer.Serialize(new {
            Records = await db.Records.AsNoTracking().Where(x => x.AccountId == account).OrderBy(x => x.Id).ToListAsync(),
            Profiles = await db.Profiles.AsNoTracking().Where(x => x.AccountId == account).OrderBy(x => x.Id).ToListAsync(),
            Folders = await db.Folders.AsNoTracking().Where(x => x.AccountId == account).OrderBy(x => x.Id).ToListAsync(),
            Collections = await db.Collections.AsNoTracking().Where(x => x.AccountId == account).OrderBy(x => x.Id).ToListAsync(),
            ProfileRecords = await db.ProfileRecords.AsNoTracking().Where(x => x.AccountId == account).OrderBy(x => x.ProfileId).ThenBy(x => x.RecordId).ToListAsync(),
            CollectionRecords = await db.CollectionRecords.AsNoTracking().Where(x => x.AccountId == account).OrderBy(x => x.CollectionId).ThenBy(x => x.RecordId).ToListAsync(),
            Trash = await db.TrashOperations.AsNoTracking().Where(x => x.AccountId == account).OrderBy(x => x.Id).ToListAsync(),
            TrashEntries = await db.TrashEntries.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Terminal = await db.TerminalErasures.AsNoTracking().OrderBy(x => x.Id).ToListAsync(),
            Work = await db.RetentionWorkItems.AsNoTracking().OrderBy(x => x.Id).ToListAsync()
        }, ProtectionFixture.Json);
    }
    internal sealed record ParentState(long Revision, long Sequence, string Envelope, DateTimeOffset EditedAt);
    internal static async Task<ParentState> Parent(PostgresFixture fixture, World w, string kind)
    {
        await using var db = fixture.CreateContext();
        if (kind == "profile") { var p = await db.Profiles.SingleAsync(x => x.Id == w.Selection); return new(p.Revision, p.ServerSequence, Convert.ToHexString(p.Envelope), p.EditedAt); }
        if (kind == "collection") { var c = await db.Collections.SingleAsync(x => x.Id == w.Items.Collection.Id); return new(c.Revision, c.ServerSequence, Convert.ToHexString(c.Envelope), c.EditedAt); }
        var f = await db.Folders.SingleAsync(x => x.Id == w.Items.Folder.Id); return new(f.Revision, f.ServerSequence, Convert.ToHexString(f.Envelope), f.EditedAt);
    }
    internal sealed class LedgerBoundary(IErasureLedger inner, Func<Task> before, Action? after = null) : IErasureLedger
    {
        public async Task RecordAsync(ErasureEntry entry, CancellationToken ct) { await before(); await inner.RecordAsync(entry, ct); after?.Invoke(); }
        public IAsyncEnumerable<ErasureEntry> ReadAsync(CancellationToken ct) => inner.ReadAsync(ct);
    }
    internal sealed class LockSignal(string table) : DbCommandInterceptor
    {
        internal TaskCompletionSource Reached { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<int> result, CancellationToken ct = default)
        { if (command.CommandText.Contains("cerberus." + table) && command.CommandText.Contains("FOR ")) Reached.TrySetResult(); return ValueTask.FromResult(result); }
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData data, InterceptionResult<DbDataReader> result, CancellationToken ct = default)
        { if (command.CommandText.Contains("cerberus." + table) && command.CommandText.Contains("FOR ")) Reached.TrySetResult(); return ValueTask.FromResult(result); }
    }
}
