using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

public partial class TrashExpiryHandlerTests
{
    [FunctionalTheory][InlineData("record",false)][InlineData("folder",false)][InlineData("collection",false)][InlineData("profile",false)]
    [InlineData("record",true)][InlineData("folder",true)][InlineData("collection",true)][InlineData("profile",true)]
    public async Task GivenClaimLostDuringDurableIo_WhenExpiring_ThenKeepNativeAndRetryWithNewClaim(string kind,bool stolen)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));var before=await TrashRestoreSetup.Resource(fixture,root);var claim=await Claim(root);
        var ledger=new PermanentDeleteSetup.LedgerBoundary(Ledger,async()=>{
            await using var db=fixture.CreateContext();await using var tx=await db.Database.BeginTransactionAsync();
            await db.Database.ExecuteSqlRawAsync("SET LOCAL lock_timeout='1s'");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
            if(stolen)await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.ClaimToken,Guid.NewGuid()));
            else await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));
            await tx.CommitAsync();
        });
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Executor(ledger).ExecuteAsync(claim,default));Assert.Equal(before.GetRawText(),(await TrashRestoreSetup.Resource(fixture,root)).GetRawText());Assert.Single(Directory.GetFiles(directory,"*.json"));
        await Expire(claim);var retry=await Claim(root);await Executor().ExecuteAsync(retry,default);Assert.False(await Exists(root));
    }
    [FunctionalTheory][InlineData("future")][InlineData("dates")][InlineData("revision")][InlineData("active")][InlineData("foreign")][InlineData("rootEntry")]
    public async Task GivenInvalidCurrentOperation_WhenExpiring_ThenFailBeforeIntentAndPreserveNative(string damage)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(damage=="future"?3600:-1));
        await using(var db=fixture.CreateContext()){
            await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+root.OperationId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.DueAt,DateTimeOffset.UtcNow.AddDays(-1)));
            if(damage=="dates")await db.TrashOperations.Where(x=>x.PublicId==root.OperationId).ExecuteUpdateAsync(x=>x.SetProperty(o=>o.DeletedAt,DateTimeOffset.UtcNow.AddDays(-31)));
            if(damage=="revision")await db.Records.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Revision,0));
            if(damage=="active")await db.Records.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,(DateTimeOffset?)null).SetProperty(r=>r.PurgeAt,(DateTimeOffset?)null));
            if(damage=="rootEntry")await db.TrashEntries.Where(x=>x.ResourceKind=="record"&&x.ResourceId==root.ResourceId).ExecuteDeleteAsync();
            if(damage=="foreign"){using var foreignKeys=new ProtectionFixture();var other=await ProfileSetup.Create(fixture,foreignKeys);await db.TrashOperations.Where(x=>x.PublicId==root.OperationId).ExecuteUpdateAsync(x=>x.SetProperty(o=>o.AccountId,other.InternalId));}
        }
        var before=await TrashRestoreSetup.Resource(fixture,root);var claim=await Claim(root);
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Executor().ExecuteAsync(claim,default));Assert.Equal(before.GetRawText(),(await TrashRestoreSetup.Resource(fixture,root)).GetRawText());Assert.Empty(Directory.GetFiles(directory,"*.json"));
        await using var check=fixture.CreateContext();Assert.False(await check.TerminalErasures.AnyAsync(x=>x.ResourceKind=="record"&&x.ResourceId==root.ResourceId));
    }
    [FunctionalFact]
    public async Task GivenDifferentKindWithSameIdentifier_WhenExpiring_ThenPreserveTypedTerminalIsolation()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));
        await using(var db=fixture.CreateContext()){db.TerminalErasures.Add(new(){ResourceKind="folder",ResourceId=root.ResourceId,DeletedAt=DateTimeOffset.UnixEpoch});await db.SaveChangesAsync();}
        await Executor().ExecuteAsync(await Claim(root),default);Assert.False(await Exists(root));await using var check=fixture.CreateContext();Assert.Equal(DateTimeOffset.UnixEpoch,(await check.TerminalErasures.SingleAsync(x=>x.ResourceKind=="folder"&&x.ResourceId==root.ResourceId)).DeletedAt);
    }
    [FunctionalFact]
    public async Task GivenCancellationDuringDurableIo_WhenExpiring_ThenRollbackPhysicalPassAndRetry()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));var claim=await Claim(root);using var cancellation=new CancellationTokenSource();
        var ledger=new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>Task.CompletedTask,()=>cancellation.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Executor(ledger).ExecuteAsync(claim,cancellation.Token));Assert.True(await Exists(root));Assert.Single(Directory.GetFiles(directory,"*.json"));await Expire(claim);await Executor().ExecuteAsync(await Claim(root),default);Assert.False(await Exists(root));
    }
    [FunctionalFact]
    public async Task GivenAlreadyErasedOperation_WhenOldExpiryJobRemains_ThenCompleteWithoutAnotherLedgerWrite()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));var claim=await Claim(root);
        await Executor().ExecuteAsync(claim,default);Assert.False(await Exists(root));var files=Directory.GetFiles(directory,"*.json");await Executor().ExecuteAsync(claim,default);Assert.Equal(files,Directory.GetFiles(directory,"*.json"));
    }
}
