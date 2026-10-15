using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public partial class AccountDeletionStoreTests
{
    private IRetentionExecutor ExpiryExecutor(IErasureLedger? ledger=null,IDbContextFactory<AppDbContext>? factory=null)=>new RetentionExecutor([new AccountExpiryHandler(factory??fixture,ledger??Ledger)]);
    private async Task<RetentionClaim> ExpiredClosure(World w,bool future=false)
    {
        Assert.Null((await new AccountClosureStore(fixture).RequestAsync(new(w.Owner.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),1),default)).Error);
        await using var db=fixture.CreateContext();var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync();var due=now.AddSeconds(future?3600:-1);
        await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ClosureRequestedAt,due.AddDays(-30)).SetProperty(a=>a.ClosurePurgeAt,due));
        await db.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+w.Owner.AccountId).ExecuteUpdateAsync(x=>x.SetProperty(j=>j.DueAt,now.AddSeconds(-1)));
        var id=await db.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+w.Owner.AccountId).Select(x=>x.PublicId).SingleAsync();
        return Assert.IsType<RetentionClaim>(await new RetentionWorkStore(fixture).TryClaimAsync(id,DateTimeOffset.UnixEpoch,TimeSpan.FromMinutes(5),default));
    }
    [FunctionalFact]
    public async Task GivenUncancelledNativeClosure_WhenAccountExpiryRuns_ThenPurgeOwnedInventoryAndCompleteOriginalJob()
    {
        var w=await Create();var inventory=await Enrich(w);var claim=await ExpiredClosure(w);await using var db=fixture.CreateContext();var foreign=await Foreign(db,w);
        db.RetentionWorkItems.Add(new(){OperationKey="account-erase/"+w.Owner.AccountId,DueAt=DateTimeOffset.UtcNow.AddDays(1)});await db.SaveChangesAsync();
        await ExpiryExecutor().ExecuteAsync(claim,default);await Purged(db,w);Assert.Equal(foreign,await Foreign(db,w));
        Assert.False(await db.SyncOperations.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.SyncStreams.AnyAsync(x=>x.Id==inventory.Stream));Assert.False(await db.SoftwareGrants.AnyAsync(x=>x.AccountId==w.Owner.InternalId));
        Assert.False(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="trash/"+inventory.TrashId||x.OperationKey=="record-purge/"+inventory.TerminalRecord));
        Assert.True(await new RetentionWorkStore(fixture).TryCompleteAsync(claim,DateTimeOffset.UnixEpoch,default));
        Assert.True(await db.RetentionWorkItems.AnyAsync(x=>x.PublicId==claim.WorkId&&x.CompletedAt!=null));
        Assert.False(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="account-erase/"+w.Owner.AccountId));
        Assert.Equal(2,await db.Accounts.Where(x=>x.Id==w.Recipient.InternalId).Select(x=>x.RevocationGeneration).SingleAsync());
        await foreach(var entry in Ledger.ReadAsync(default))Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind==entry.ResourceKind&&x.ResourceId==entry.ResourceId&&x.DeletedAt==entry.DeletedAt));
        Assert.True(Directory.GetFiles(directory,"*.json").Length>=9);
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenLedgerUnavailableOrPartial_WhenAccountExpiryRetries_ThenNeverChangeOriginalClosureOrNativeData(bool partial)
    {
        var w=await Create();var claim=await ExpiredClosure(w);await using var db=fixture.CreateContext();var before=await Snapshot(db,w.Owner);var foreign=await Foreign(db,w);var calls=0;
        var outage=new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>{if(++calls==(partial?2:1))throw new IOException("fixture account ledger");return Task.CompletedTask;});
        await Assert.ThrowsAsync<IOException>(()=>ExpiryExecutor(outage).ExecuteAsync(claim,default));Assert.Equal(before,await Snapshot(db,w.Owner));Assert.Equal(foreign,await Foreign(db,w));Assert.True(await db.Records.AnyAsync(x=>x.Id==w.Owned.Record.Id));
        Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==w.Owner.AccountId));Assert.Equal(partial?1:0,Directory.GetFiles(directory,"*.json").Length);
        Assert.Equal("authentication_required",(await new AccountClosureRecoveryStore(fixture).ReadAsync(new(w.Owner.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds()),default)).Error);
        await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(j=>j.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));
        var retry=Assert.IsType<RetentionClaim>(await new RetentionWorkStore(fixture).TryClaimAsync(claim.WorkId,DateTimeOffset.UnixEpoch,TimeSpan.FromMinutes(5),default));
        await ExpiryExecutor().ExecuteAsync(retry,default);await Purged(db,w);Assert.True(await new RetentionWorkStore(fixture).TryCompleteAsync(retry,DateTimeOffset.UnixEpoch,default));
    }
    [FunctionalTheory][InlineData("future")][InlineData("active")][InlineData("dates")][InlineData("revision")]
    [InlineData("session")][InlineData("incoming")][InlineData("outgoing")]
    [InlineData("policy")][InlineData("generation")][InlineData("incomingKey")][InlineData("outgoingKey")]
    [InlineData("software")][InlineData("softwareKey")][InlineData("challenge")]
    public async Task GivenInvalidClosingState_WhenAccountExpiryRuns_ThenFailBeforeIntentAndPreserveState(string damage)
    {
        var w=await Create();if(damage is "software" or "softwareKey" or "challenge")await Enrich(w);var claim=await ExpiredClosure(w,damage=="future");await using var db=fixture.CreateContext();
        if(damage=="active")await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.Active));
        if(damage=="dates")await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ClosureRequestedAt,DateTimeOffset.UtcNow.AddDays(-31)));
        if(damage=="revision")await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.Revision,0));
        if(damage=="session")await db.VaultAccessSessions.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.Revoked,false));
        if(damage is "incoming" or "outgoing"){var id=damage=="incoming"?w.Incoming.Id:w.Outgoing.Id;await db.CollectionGrants.Where(x=>x.Id==id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Active));}
        if(damage=="policy")await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.PolicyRevision,0));
        if(damage=="generation")await db.Accounts.Where(x=>x.Id==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.RevocationGeneration,0));
        if(damage is "incomingKey" or "outgoingKey"){var id=damage=="incomingKey"?w.Incoming.Id:w.Outgoing.Id;await db.CollectionGrants.Where(x=>x.Id==id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,new byte[]{1}));}
        if(damage=="software")await db.SoftwareGrants.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,SoftwareGrantState.Active));
        if(damage=="softwareKey")await db.SoftwareGrants.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKey,new byte[]{1}));
        if(damage=="challenge")await db.VaultUnlockChallenges.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.Consumed,false));
        var before=await Snapshot(db,w.Owner);var foreign=await Foreign(db,w);await Assert.ThrowsAsync<InvalidOperationException>(()=>ExpiryExecutor().ExecuteAsync(claim,default));Assert.Equal(before,await Snapshot(db,w.Owner));Assert.Equal(foreign,await Foreign(db,w));Assert.Empty(Directory.GetFiles(directory,"*.json"));Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==w.Owner.AccountId));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenPersistedClaimChangesDuringLedger_WhenAccountExpiryRuns_ThenNoPhysicalDeletionAndNewClaimResumes(bool stolen)
    {
        var w=await Create();var claim=await ExpiredClosure(w);var first=true;var boundary=new PermanentDeleteSetup.LedgerBoundary(Ledger,async()=>{
            if(!first)return;first=false;await using var db=fixture.CreateContext();await using var tx=await db.Database.BeginTransactionAsync();await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='1s'");await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={w.Owner.InternalId} FOR UPDATE");
            if(stolen)await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(j=>j.ClaimToken,Guid.NewGuid()));else await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(j=>j.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));await tx.CommitAsync();
        });
        await Assert.ThrowsAsync<InvalidOperationException>(()=>ExpiryExecutor(boundary).ExecuteAsync(claim,default));await using var check=fixture.CreateContext();Assert.True(await check.Accounts.AnyAsync(x=>x.Id==w.Owner.InternalId));Assert.True(await check.Records.AnyAsync(x=>x.Id==w.Owned.Record.Id));Assert.NotEmpty(Directory.GetFiles(directory,"*.json"));
        await check.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(j=>j.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));var retry=Assert.IsType<RetentionClaim>(await new RetentionWorkStore(fixture).TryClaimAsync(claim.WorkId,DateTimeOffset.UnixEpoch,TimeSpan.FromMinutes(5),default));await ExpiryExecutor().ExecuteAsync(retry,default);await Purged(check,w);
    }
}
