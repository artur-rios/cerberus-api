using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountDeletionStoreTests
{
    [FunctionalFact]
    public async Task GivenBackupBeforeRetainedExpiry_WhenReplayingCompletedLedger_ThenNeverReviveAccountOrAnyTrashKindBeforeTraffic()
    {
        var isolated=new PostgresFixture();try{await isolated.InitializeAsync();using var scenario=new AccountDeletionStoreTests(isolated);await scenario.VerifyRetainedExpiryBackup();}finally{await isolated.DisposeAsync();}
    }
    private async Task VerifyRetainedExpiryBackup()
    {
        var w=await Create();await Enrich(w);using var keys=new ProtectionFixture();var trashOwner=await ProfileSetup.Create(fixture,keys);var roots=new List<TrashRestoreSetup.Root>();
        foreach(var kind in new[]{"record","folder","collection","profile"}){var root=await TrashRestoreSetup.RootAsync(fixture,trashOwner,keys,kind);await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));roots.Add(root);}
        // This valid isolated backup predates account closure and all expiry intent.
        // Other corruption tests intentionally leave invalid rows in the shared DB.
        var backup=await fixture.BackupAsync();var accountClaim=await ExpiredClosure(w);await ExpiryExecutor().ExecuteAsync(accountClaim,default);
        foreach(var root in roots)
        {
            await using var job=fixture.CreateContext();var id=await job.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+root.OperationId).Select(x=>x.PublicId).SingleAsync();var claim=Assert.IsType<RetentionClaim>(await new RetentionWorkStore(fixture).TryClaimAsync(id,DateTimeOffset.UnixEpoch,TimeSpan.FromMinutes(5),default));await new TrashExpiryHandler(fixture,Ledger).ExecuteAsync(claim,default);
        }
        await using var db=fixture.CreateContext();await Purged(db,w);var foreign=await Foreign(db,w);var survivors=await TrashRestoreSetup.Snapshot(fixture,trashOwner);
        await fixture.RestoreAsync(backup);Assert.True(await db.Accounts.AnyAsync(x=>x.PublicId==w.Owner.AccountId&&x.State==AccountState.Active));Assert.True(await db.Records.AnyAsync(x=>x.Id==w.Owned.Record.Id));Assert.True(await db.TrashOperations.CountAsync(x=>x.AccountId==trashOwner.InternalId)==4);
        var gate=new RestoreTrafficGate();Assert.False(gate.IsReady);var reconciler=new RestoreReconciler(Ledger,new TerminalErasureStore(fixture),new RestorePermit(),gate);Assert.True(await reconciler.ReconcileAsync(default));Assert.True(gate.IsReady);
        await Purged(db,w);Assert.False(await db.SyncOperations.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.TrashOperations.AnyAsync(x=>x.AccountId==trashOwner.InternalId));Assert.Equal(foreign,await Foreign(db,w));Assert.Equal(survivors,await TrashRestoreSetup.Snapshot(fixture,trashOwner));Assert.Equal(2,await db.Accounts.Where(x=>x.Id==w.Recipient.InternalId).Select(x=>x.RevocationGeneration).SingleAsync());
        Assert.True(await reconciler.ReconcileAsync(default));Assert.Equal(foreign,await Foreign(db,w));Assert.Equal(survivors,await TrashRestoreSetup.Snapshot(fixture,trashOwner));Assert.Equal(2,await db.Accounts.Where(x=>x.Id==w.Recipient.InternalId).Select(x=>x.RevocationGeneration).SingleAsync());
    }
}
