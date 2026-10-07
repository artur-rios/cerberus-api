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

    public void Dispose() => Directory.Delete(_directory, recursive: true);
}
