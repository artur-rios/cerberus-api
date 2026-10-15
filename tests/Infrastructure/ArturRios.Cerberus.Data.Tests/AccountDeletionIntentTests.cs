using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountDeletionStoreTests
{
    [FunctionalTheory]
    [InlineData("due")][InlineData("completed")][InlineData("key")][InlineData("attempts")]
    [InlineData("claim")][InlineData("publicId")][InlineData("recoverable")]
    [InlineData("owner")][InlineData("childMarker")][InlineData("foreignMarker")]
    public async Task GivenPostSaveIntentCorruption_WhenDeleting_ThenRollBackAllReductionsAndIntent(string kind)
    {
        var w=await Create();await using var db=fixture.CreateContext();var before=await Snapshot(db,w.Owner);var foreign=await Foreign(db,w);
        var fault=new CorruptIntent(w,kind);var result=await Store(new ProfileSetup.Factory(fixture,fault)).DeleteAsync(Request(w.Owner),default);
        Assert.True(fault.Reached);Assert.Equal("persistence_unavailable",result.Error);Assert.Null(result.Details);
        Assert.Equal(before,await Snapshot(db,w.Owner));Assert.Equal(foreign,await Foreign(db,w));
        Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&(x.ResourceId==w.Owner.AccountId||x.ResourceId==w.Other.AccountId)));
        Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="record"&&x.ResourceId==w.Owned.Record.PublicId));
    }
    [FunctionalFact]
    public async Task GivenCallerCancellationDuringLedgerIo_WhenIntentAlreadyCommitted_ThenKeepInaccessibleCiphertextAndAllowWorkerRetry()
    {
        var w=await Create();using var ct=new CancellationTokenSource();
        var ledger=new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>{ct.Cancel();return Task.CompletedTask;});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store(ledger:ledger).DeleteAsync(Request(w.Owner),ct.Token));
        await using var db=fixture.CreateContext();Assert.True(await db.Records.AnyAsync(x=>x.Id==w.Owned.Record.Id));
        Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==w.Owner.AccountId));
        Assert.False(await db.VaultAccessSessions.AnyAsync(x=>x.AccountId==w.Owner.InternalId&&!x.Revoked));
        var work=await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="account-erase/"+w.Owner.AccountId);Assert.Null(work.CompletedAt);
        await db.RetentionWorkItems.Where(x=>x.Id==work.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));
        var store=new RetentionWorkStore(fixture);var claim=await store.TryClaimAsync(work.PublicId,DateTimeOffset.UtcNow,TimeSpan.FromMinutes(1),default);Assert.NotNull(claim);
        await new AccountPurgeHandler(fixture,Ledger).ExecuteAsync(claim!,default);Assert.True(await store.TryCompleteAsync(claim!,DateTimeOffset.UtcNow,default));await Purged(db,w);
    }
    private sealed class CorruptIntent(World w,string kind):SaveChangesInterceptor
    {
        public bool Reached{get;private set;}
        public override async ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData,int result,CancellationToken ct=default)
        {
            var db=(AppDbContext)eventData.Context!;var key="account-erase/"+w.Owner.AccountId;
            if(Reached||!await db.RetentionWorkItems.AsNoTracking().AnyAsync(x=>x.OperationKey==key,ct))return result;
            Reached=true;
            switch(kind)
            {
                case "due":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET due_at=due_at+interval '60 days' WHERE operation_key={key}",ct);break;
                case "completed":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET completed_at=statement_timestamp() WHERE operation_key={key}",ct);break;
                case "key":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET operation_key={"other/"+w.Owner.AccountId} WHERE operation_key={key}",ct);break;
                case "attempts":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET attempts=1 WHERE operation_key={key}",ct);break;
                case "claim":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET claim_token={Guid.NewGuid()},claim_expires_at=statement_timestamp()+interval '1 day' WHERE operation_key={key}",ct);break;
                case "publicId":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET public_id={Guid.NewGuid()} WHERE operation_key={key}",ct);break;
                case "recoverable":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.retention_work_item SET completed_at=NULL WHERE operation_key={"account-purge/"+w.Owner.AccountId}",ct);break;
                case "owner":await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.account SET state={AccountState.Active},revision=revision+1 WHERE id={w.Owner.InternalId}",ct);break;
                case "childMarker":await db.Database.ExecuteSqlInterpolatedAsync($"DELETE FROM cerberus.terminal_erasure WHERE resource_kind='record' AND resource_id={w.Owned.Record.PublicId}",ct);break;
                case "foreignMarker":await db.Database.ExecuteSqlInterpolatedAsync($"INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at) VALUES('account',{w.Other.AccountId},statement_timestamp())",ct);break;
            }
            return result;
        }
    }
}
