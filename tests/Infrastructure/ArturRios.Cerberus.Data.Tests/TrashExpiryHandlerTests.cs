using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public partial class TrashExpiryHandlerTests(PostgresFixture fixture):IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"cerberus-trash-expiry-"+Guid.NewGuid().ToString("N"));
    private FileErasureLedger Ledger{get{if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException();Directory.CreateDirectory(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);return new(directory);}}
    private IRetentionExecutor Executor(IErasureLedger? ledger=null,IDbContextFactory<AppDbContext>? factory=null)=>new RetentionExecutor([new TrashExpiryHandler(factory??fixture,ledger??Ledger)]);
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenExpiredNativeOperation_WhenExecutingOriginalJob_ThenDurablyEraseOnlyThatOperationAndComplete(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);
        var other=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));
        var preserved=await TrashRestoreSetup.Resource(fixture,other);var account=await TrashRestoreSetup.Protected(fixture,s);var claim=await Claim(root);
        await Executor().ExecuteAsync(claim,default);Assert.True(await new RetentionWorkStore(fixture).TryCompleteAsync(claim,DateTimeOffset.UnixEpoch,default));
        Assert.False(await Exists(root));Assert.Equal(preserved.GetRawText(),(await TrashRestoreSetup.Resource(fixture,other)).GetRawText());Assert.Equal(account,await TrashRestoreSetup.Protected(fixture,s));
        await using var db=fixture.CreateContext();Assert.False(await db.TrashOperations.AnyAsync(x=>x.PublicId==root.OperationId));
        Assert.True(await db.RetentionWorkItems.AnyAsync(x=>x.PublicId==claim.WorkId&&x.CompletedAt!=null));
        Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind==kind&&x.ResourceId==root.ResourceId));Assert.Single(Directory.GetFiles(directory,"*.json"));
        Assert.Equal("not_found",(await new TrashRestoreStore(fixture).RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),default)).Error);
    }
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenLedgerOutage_WhenExpiringThenRetrying_ThenKeepNativeHiddenAndOriginalDeadline(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));
        var before=await TrashRestoreSetup.Resource(fixture,root);var claim=await Claim(root);
        await Assert.ThrowsAsync<IOException>(()=>Executor(new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>throw new IOException("fixture outage"))).ExecuteAsync(claim,default));
        Assert.Equal(before.GetRawText(),(await TrashRestoreSetup.Resource(fixture,root)).GetRawText());
        Assert.Equal("not_found",(await new TrashRestoreStore(fixture).RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),default)).Error);
        Assert.Empty((await new TrashListStore(fixture).ListAsync(new(s.Actor,s.Verifier,50,0,null),default)).Data!.Items);
        await Expire(claim);var retry=await Claim(root);Assert.NotEqual(claim.Token,retry.Token);await Executor().ExecuteAsync(retry,default);Assert.False(await Exists(root));
        Assert.True(await new RetentionWorkStore(fixture).TryCompleteAsync(retry,DateTimeOffset.UnixEpoch,default));
    }
    private async Task<RetentionClaim> Claim(TrashRestoreSetup.Root root)
    {
        await using var db=fixture.CreateContext();var id=await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+root.OperationId).Select(x=>x.PublicId).SingleAsync();
        return Assert.IsType<RetentionClaim>(await new RetentionWorkStore(fixture).TryClaimAsync(id,DateTimeOffset.UnixEpoch,TimeSpan.FromMinutes(5),default));
    }
    private async Task Expire(RetentionClaim claim){await using var db=fixture.CreateContext();await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));}
    private async Task<bool> Exists(TrashRestoreSetup.Root root)
    {
        await using var db=fixture.CreateContext();return root.Kind switch{"record"=>await db.Records.AnyAsync(x=>x.PublicId==root.ResourceId),"folder"=>await db.Folders.AnyAsync(x=>x.PublicId==root.ResourceId),"collection"=>await db.Collections.AnyAsync(x=>x.PublicId==root.ResourceId),_=>await db.Profiles.AnyAsync(x=>x.PublicId==root.ResourceId)};
    }
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
