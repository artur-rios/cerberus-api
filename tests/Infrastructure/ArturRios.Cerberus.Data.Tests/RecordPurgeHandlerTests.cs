using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Data.Records;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public sealed class RecordPurgeHandlerTests(PostgresFixture fixture) : IDisposable
{
    private readonly string directory = Directory.CreateTempSubdirectory("cerberus-purge-handler-").FullName;
    private FileErasureLedger Ledger => new(directory);

    [FunctionalTheory]
    [InlineData("expired")][InlineData("stolen")][InlineData("wrongWork")][InlineData("wrongKey")]
    [InlineData("missingTerminal")][InlineData("otherKind")][InlineData("completed")]
    public async Task GivenInvalidPersistedPurgeAuthority_WhenExecuting_ThenNoLedgerOrPhysicalRemoval(string kind)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        var (work, claim) = await Pending(w, kind == "missingTerminal" ? null : kind == "otherKind" ? "folder" : "record");
        await using var db = fixture.CreateContext();
        if (kind == "expired") await db.RetentionWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ClaimExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        if (kind == "stolen") await db.RetentionWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ClaimToken, Guid.NewGuid()));
        if (kind == "completed") await db.RetentionWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.CompletedAt, DateTimeOffset.UtcNow));
        if (kind == "wrongWork") claim = claim with { WorkId = Guid.NewGuid() };
        if (kind == "wrongKey") claim = claim with { OperationKey = "record-purge/" + Guid.NewGuid() };
        var before = await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RecordPurgeHandler(fixture, Ledger).ExecuteAsync(claim, default));
        Assert.Equal(before, await PermanentDeleteSetup.Snapshot(fixture, w.State.InternalId));
        Assert.Empty(Directory.EnumerateFiles(directory, "*.json"));
    }

    [FunctionalTheory]
    [InlineData("expired")][InlineData("stolen")]
    public async Task GivenClaimChangesDuringDurableLedgerWrite_WhenPurging_ThenFinalFenceKeepsCipherForNewClaim(string kind)
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        var (work, claim) = await Pending(w, "record");
        var ledger = new PermanentDeleteSetup.LedgerBoundary(Ledger, async () =>
        {
            await using var db = fixture.CreateContext();
            if (kind == "expired") await db.RetentionWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ClaimExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
            else await db.RetentionWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ClaimToken, Guid.NewGuid()));
        });
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RecordPurgeHandler(fixture, ledger).ExecuteAsync(claim, default));
        await using var check = fixture.CreateContext();
        Assert.True(await check.Records.AnyAsync(x => x.Id == w.Record.Id));
        Assert.Null((await check.RetentionWorkItems.AsNoTracking().SingleAsync(x => x.Id == work.Id)).CompletedAt);
        Assert.Single(Directory.EnumerateFiles(directory, "*.json"));
        await check.RetentionWorkItems.Where(x => x.Id == work.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ClaimExpiresAt, DateTimeOffset.UtcNow.AddSeconds(-1)));
        var next = await new RetentionWorkStore(fixture).TryClaimAsync(work.PublicId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(next);
        await new RecordPurgeHandler(fixture, Ledger).ExecuteAsync(next!, default);
        Assert.False(await check.Records.AnyAsync(x => x.Id == w.Record.Id));
    }

    [FunctionalFact]
    public async Task GivenFailureAfterPhysicalDelete_WhenPurging_ThenRollbackCipherRemovalAndRetryWithoutDuplicateLedger()
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        var (_, claim) = await Pending(w, "record");
        await Assert.ThrowsAsync<TimeoutException>(() => new RecordPurgeHandler(new ArturRios.Cerberus.Data.Tests.ProfileSetup.Factory(fixture, new FailAfterDelete()), Ledger).ExecuteAsync(claim, default));
        await using var db = fixture.CreateContext();
        Assert.True(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
        Assert.Single(Directory.EnumerateFiles(directory, "*.json"));
        await new RecordPurgeHandler(fixture, Ledger).ExecuteAsync(claim, default);
        Assert.False(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
        Assert.Single(Directory.EnumerateFiles(directory, "*.json"));
        await new RecordPurgeHandler(fixture, Ledger).ExecuteAsync(claim, default);
        Assert.Single(Directory.EnumerateFiles(directory, "*.json"));
    }

    [FunctionalFact]
    public async Task GivenNonemptyTrashCascadeWithSameGuidOtherKind_WhenPurgingRecord_ThenKeepOtherEntryOperationAndResources()
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", true);
        await using var db = fixture.CreateContext();
        var entry = await db.TrashEntries.SingleAsync(x => x.ResourceKind == "record" && x.ResourceId == w.Record.PublicId);
        var operation = await db.TrashOperations.SingleAsync(x => x.Id == entry.OperationId);
        db.TrashEntries.Add(new() { ResourceKind = "folder", ResourceId = w.Record.PublicId, OperationId = operation.Id, AssociationSnapshot = "{}"u8.ToArray() });
        await db.SaveChangesAsync();
        var (_, claim) = await Pending(w, "record");
        await new RecordPurgeHandler(fixture, Ledger).ExecuteAsync(claim, default);
        Assert.False(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
        Assert.False(await db.TrashEntries.AnyAsync(x => x.ResourceKind == "record" && x.ResourceId == w.Record.PublicId));
        Assert.True(await db.TrashEntries.AnyAsync(x => x.ResourceKind == "folder" && x.ResourceId == w.Record.PublicId));
        Assert.True(await db.TrashOperations.AnyAsync(x => x.Id == operation.Id));
        Assert.True(await db.RetentionWorkItems.AnyAsync(x => x.OperationKey == "trash/" + operation.PublicId));
        Assert.True(await db.Folders.AnyAsync(x => x.Id == w.Items.Folder.Id));
    }

    [FunctionalFact]
    public async Task GivenBackupBeforePermanentDeletion_WhenRestoringAndReplayingLedger_ThenRemoveCiphertextBeforeTrafficAndKeepOtherKinds()
    {
        using var w = await PermanentDeleteSetup.Create(fixture, "wide", false);
        await using var db = fixture.CreateContext();
        await db.Folders.Where(x => x.Id == w.Items.Folder.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.PublicId, w.Record.PublicId));
        var backup = await fixture.BackupAsync();
        var store = new RecordPermanentDeleteStore(fixture, Ledger, new CerberusOptions { RetentionInterval = "00:00:30" }, TimeProvider.System);
        Assert.Null((await store.DeleteAsync(w.Request(), default)).Error);
        Assert.False(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
        await fixture.RestoreAsync(backup);
        Assert.True(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
        var gate = new RestoreTrafficGate();
        Assert.False(gate.IsReady);
        Assert.True(await new RestoreReconciler(Ledger, new TerminalErasureStore(fixture), new RestorePermit(), gate).ReconcileAsync(default));
        Assert.True(gate.IsReady);
        Assert.False(await db.Records.AnyAsync(x => x.Id == w.Record.Id));
        Assert.True(await db.Folders.AnyAsync(x => x.PublicId == w.Record.PublicId));
        Assert.True(await new TerminalErasureStore(fixture).IsErasedAsync("record", w.Record.PublicId, default));
        Assert.False(await new TerminalErasureStore(fixture).IsErasedAsync("folder", w.Record.PublicId, default));
        Assert.Equal("revision_conflict", (await new RecordCreateStore(fixture).CreateAsync(new(w.State.Actor, w.State.Verifier,
            new(w.Record.PublicId, ArturRios.Cerberus.Data.Tests.ProfileSetup.Envelope(), DateTimeOffset.UtcNow, [], null)), default)).Error);
    }

    [FunctionalTheory]
    [InlineData("record")][InlineData("trash_entry")][InlineData("retention_work_item")][InlineData("trash_operation")]
    public async Task GivenClaimExpiresAfterCheckBeforePhysicalStatement_WhenPurging_ThenThatStatementCannotRemoveRows(string table)
    {
        using var w=await PermanentDeleteSetup.Create(fixture,"wide",true);
        var (work,claim)=await Pending(w,"record");
        var deadline=DateTimeOffset.UtcNow.AddSeconds(2);
        await using var db=fixture.CreateContext();
        await db.RetentionWorkItems.Where(x=>x.Id==work.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ClaimExpiresAt,deadline));
        var before=await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId);
        var hook=new ExpireBeforeMutation(table,deadline);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>new RecordPurgeHandler(new ProfileSetup.Factory(fixture,hook),Ledger).ExecuteAsync(claim,default));
        Assert.True(hook.Reached);
        Assert.Equal(0,hook.Affected);
        Assert.Equal(before,await PermanentDeleteSetup.Snapshot(fixture,w.State.InternalId));
        Assert.Single(Directory.EnumerateFiles(directory,"*.json"));
    }
    private sealed class ExpireBeforeMutation(string table,DateTimeOffset deadline):DbCommandInterceptor
    {
        internal bool Reached{get;private set;}
        internal int Affected{get;private set;}=-1;
        private bool Match(DbCommand command)=>command.CommandText.Contains("DELETE FROM cerberus."+table+" ",StringComparison.Ordinal)
            || command.CommandText.Contains("DELETE FROM cerberus."+table+"\n",StringComparison.Ordinal)
            || command.CommandText.Contains("DELETE FROM cerberus."+table+" AS",StringComparison.Ordinal);
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {
            if(!Reached && Match(command)){Reached=true;var left=deadline-DateTimeOffset.UtcNow;if(left>TimeSpan.Zero)await Task.Delay(left+TimeSpan.FromMilliseconds(50),ct);}
            return result;
        }
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,CommandExecutedEventData data,int result,CancellationToken ct=default)
        {if(Reached && Match(command))Affected=result;return ValueTask.FromResult(result);}
    }

    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenDatabaseClaimExpiresBeforeCompletion_WhenCompletingWithOldCallerTime_ThenNoAcknowledgement(bool afterLock)
    {
        using var w=await PermanentDeleteSetup.Create(fixture,"wide",false);
        var (work,claim)=await Pending(w,"record");
        await new RecordPurgeHandler(fixture,Ledger).ExecuteAsync(claim,default);
        var old=DateTimeOffset.UtcNow;
        var deadline=afterLock?old.AddSeconds(2):old.AddSeconds(-1);
        await using var db=fixture.CreateContext();
        await db.RetentionWorkItems.Where(x=>x.Id==work.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ClaimExpiresAt,deadline));
        var hook=new ExpireBeforeCompletion(deadline);
        var factory=afterLock?new ProfileSetup.Factory(fixture,hook):fixture as IDbContextFactory<AppDbContext>;
        Assert.False(await new RetentionWorkStore(factory!).TryCompleteAsync(claim,old.AddMinutes(-1),default));
        Assert.Null((await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.Id==work.Id)).CompletedAt);
        if(afterLock)Assert.True(hook.Reached);
    }
    private sealed class ExpireBeforeCompletion(DateTimeOffset deadline):DbCommandInterceptor
    {
        internal bool Reached{get;private set;}
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {if(!Reached && command.CommandText.Contains("UPDATE cerberus.retention_work_item")){Reached=true;var left=deadline-DateTimeOffset.UtcNow;if(left>TimeSpan.Zero)await Task.Delay(left+TimeSpan.FromMilliseconds(50),ct);}return result;}
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {if(!Reached && command.CommandText.Contains("UPDATE cerberus.retention_work_item")){Reached=true;var left=deadline-DateTimeOffset.UtcNow;if(left>TimeSpan.Zero)await Task.Delay(left+TimeSpan.FromMilliseconds(50),ct);}return result;}
    }

    private async Task<(RetentionWorkItem, RetentionClaim)> Pending(PermanentDeleteSetup.World w, string? terminalKind)
    {
        await using var db = fixture.CreateContext();
        if (terminalKind is not null) db.TerminalErasures.Add(new() { ResourceKind = terminalKind, ResourceId = w.Record.PublicId, DeletedAt = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) });
        var work = new RetentionWorkItem { OperationKey = "record-purge/" + w.Record.PublicId, DueAt = DateTimeOffset.UtcNow.AddMinutes(-1) };
        db.RetentionWorkItems.Add(work);
        await db.SaveChangesAsync();
        var claim = await new RetentionWorkStore(fixture).TryClaimAsync(work.PublicId, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1), default);
        Assert.NotNull(claim);
        return (work, claim!);
    }
    private sealed class RestorePermit : IRestoreAuthorizationVerifier
    { public Task<bool> VerifyAsync(CancellationToken ct) => Task.FromResult(true); }
    private sealed class FailAfterDelete : DbCommandInterceptor
    {
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command, CommandExecutedEventData data, int result, CancellationToken ct = default)
        { if (command.CommandText.Contains("DELETE FROM cerberus.record")) throw new TimeoutException("fixture post-delete crash"); return ValueTask.FromResult(result); }
    }
    public void Dispose() => Directory.Delete(directory, recursive: true);
}
