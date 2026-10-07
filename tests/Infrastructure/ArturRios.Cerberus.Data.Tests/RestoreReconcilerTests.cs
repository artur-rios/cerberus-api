using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public sealed class RestoreReconcilerTests(PostgresFixture fixture) : IDisposable
{
    private readonly string _directory = Directory.CreateTempSubdirectory("cerberus-restore-test-").FullName;

    [FunctionalFact]
    public async Task GivenBackupBeforeErasure_WhenRestoringActualPostgresDump_ThenReplayLedgerBeforeOpeningTraffic()
    {
        var backup = await fixture.BackupAsync();
        var entry = new ErasureEntry(Guid.NewGuid(), "record", DateTimeOffset.Parse("2026-10-07T00:00:00Z"));
        var ledger = new FileErasureLedger(_directory);
        var erasures = new TerminalErasureStore(fixture);
        await ledger.RecordAsync(entry, default);
        await erasures.ReapplyAsync(entry, default);
        Assert.True(await erasures.IsErasedAsync(entry.ResourceId, default));
        await fixture.RestoreAsync(backup);
        Assert.False(await erasures.IsErasedAsync(entry.ResourceId, default));
        var gate = new RestoreTrafficGate();
        var reconciler = new RestoreReconciler(ledger, erasures, new AuthorizationFixture(true), gate);
        Assert.False(gate.IsReady);
        Assert.True(await reconciler.ReconcileAsync(default));
        Assert.True(await erasures.IsErasedAsync(entry.ResourceId, default));
        Assert.True(gate.IsReady);
    }

    [FunctionalFact]
    public async Task GivenRolledBackDatabase_WhenReplayingExternalLedger_ThenKeepDeletedIdErased()
    {
        var entry = new ErasureEntry(Guid.NewGuid(), "record", DateTimeOffset.Parse("2026-10-07T00:00:00Z"));
        var ledger = new FileErasureLedger(_directory);
        await ledger.RecordAsync(entry, default);
        await using (var database = fixture.CreateContext())
        {
            await using var transaction = await database.Database.BeginTransactionAsync();
            database.Add(new TerminalErasure { ResourceId = entry.ResourceId, ResourceKind = entry.ResourceKind, DeletedAt = entry.DeletedAt });
            await database.SaveChangesAsync();
            await transaction.RollbackAsync();
        }
        var erasures = new TerminalErasureStore(fixture);
        Assert.False(await erasures.IsErasedAsync(entry.ResourceId, default));
        var gate = new RestoreTrafficGate();
        Assert.False(gate.IsReady);
        var reconciler = new RestoreReconciler(ledger, erasures, new AuthorizationFixture(true), gate);
        Assert.True(await reconciler.ReconcileAsync(default));
        Assert.True(await erasures.IsErasedAsync(entry.ResourceId, default));
        Assert.True(gate.IsReady);
        Assert.True(await reconciler.ReconcileAsync(default));
        await using var verification = fixture.CreateContext();
        Assert.Equal(1, await verification.TerminalErasures.CountAsync(x => x.ResourceId == entry.ResourceId));
    }

    [FunctionalFact]
    public async Task GivenRevokedAuthorization_WhenReconcilingRestore_ThenKeepTrafficClosed()
    {
        var gate = new RestoreTrafficGate();
        var reconciler = new RestoreReconciler(new FileErasureLedger(_directory), new TerminalErasureStore(fixture),
            new AuthorizationFixture(false), gate);
        Assert.False(await reconciler.ReconcileAsync(default));
        Assert.False(gate.IsReady);
    }

    [FunctionalFact]
    public async Task GivenCorruptLedger_WhenReconcilingRestore_ThenNeverOpenTraffic()
    {
        await File.WriteAllTextAsync(Path.Combine(_directory, Guid.NewGuid().ToString("N") + ".json"), "protected-corruption");
        var gate = new RestoreTrafficGate();
        var reconciler = new RestoreReconciler(new FileErasureLedger(_directory), new TerminalErasureStore(fixture),
            new AuthorizationFixture(true), gate);
        await Assert.ThrowsAsync<InvalidDataException>(() => reconciler.ReconcileAsync(default));
        Assert.False(gate.IsReady);
    }

    private sealed class AuthorizationFixture(bool authorized) : IRestoreAuthorizationVerifier
    {
        public Task<bool> VerifyAsync(CancellationToken cancellationToken) => Task.FromResult(authorized);
    }

    [FunctionalFact]
    public async Task GivenPreviouslyReadyRestore_WhenNewReconciliationIsCancelled_ThenCloseTraffic()
    {
        var gate = new RestoreTrafficGate();
        var reconciler = new RestoreReconciler(new FileErasureLedger(_directory), new TerminalErasureStore(fixture), new AuthorizationFixture(true), gate);
        Assert.True(await reconciler.ReconcileAsync(default));
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reconciler.ReconcileAsync(cancellation.Token));
        Assert.False(gate.IsReady);
    }

    [FunctionalFact]
    public async Task GivenQueuedRestoreReconciliation_WhenPriorCheckCompletes_ThenDoNotOpenTrafficPrematurely()
    {
        var gate = new RestoreTrafficGate();
        var authorization = new BlockingAuthorization();
        var reconciler = new RestoreReconciler(new FileErasureLedger(_directory), new TerminalErasureStore(fixture), authorization, gate);
        var first = reconciler.ReconcileAsync(default);
        await authorization.Started.Task;
        var queued = reconciler.ReconcileAsync(default);
        authorization.Release.SetResult(true);
        Assert.False(await first);
        Assert.True(await queued);
        Assert.True(gate.IsReady);
    }

    private sealed class BlockingAuthorization : IRestoreAuthorizationVerifier
    {
        private int _calls;
        public TaskCompletionSource<bool> Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async Task<bool> VerifyAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref _calls) != 1) return true;
            Started.SetResult(true);
            return await Release.Task.WaitAsync(cancellationToken);
        }
    }

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
