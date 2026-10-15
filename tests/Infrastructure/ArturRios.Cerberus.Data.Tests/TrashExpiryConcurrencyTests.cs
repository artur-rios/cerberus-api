using System.Data.Common;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Folders;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

public partial class TrashExpiryHandlerTests
{
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenLeaseExpiresBeforeActualIntentStatement_WhenExpiring_ThenNoTerminalOrPhysicalWrite(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));var claim=await Claim(root);var deadline=DateTimeOffset.UtcNow.AddSeconds(2);
        await using(var db=fixture.CreateContext())await db.RetentionWorkItems.Where(x=>x.PublicId==claim.WorkId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.ClaimExpiresAt,deadline));
        var hook=new ExpiryStatement(c=>c.CommandText.StartsWith("INSERT INTO cerberus.terminal_erasure AS t",StringComparison.Ordinal),()=>WaitPast(deadline));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>Executor(factory:new ProfileSetup.Factory(fixture,hook)).ExecuteAsync(claim,default));Assert.True(hook.Reached);Assert.True(await Exists(root));Assert.Empty(Directory.GetFiles(directory,"*.json"));
        await using var check=fixture.CreateContext();Assert.False(await check.TerminalErasures.AnyAsync(x=>x.ResourceKind==kind&&x.ResourceId==root.ResourceId));
    }
    [FunctionalFact]
    public async Task GivenRestoreAdmittedBeforeDeadline_WhenAccountExpiryWaits_ThenOnlyRestoreCommitsEvenAfterDeadline()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");var deadline=DateTimeOffset.UtcNow.AddSeconds(5);await TrashRestoreSetup.Deadline(fixture,root,deadline);
        await using(var db=fixture.CreateContext())await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+root.OperationId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.DueAt,DateTimeOffset.UtcNow.AddDays(-1)));
        var claim=await Claim(root);var admitted=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var release=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var gate=new AfterExpiryStatement(c=>c.CommandText.Contains("DELETE FROM cerberus.retention_work_item",StringComparison.Ordinal),async()=>{admitted.TrySetResult();await release.Task.WaitAsync(TimeSpan.FromSeconds(25));});
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var restoring=new TrashRestoreStore(new ProfileSetup.Factory(fixture,gate)).RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),timeout.Token);
        Task? expiring=null;
        try
        {
            await admitted.Task.WaitAsync(TimeSpan.FromSeconds(15));var signal=new PermanentDeleteSetup.LockSignal("account");expiring=Executor(factory:new ProfileSetup.Factory(fixture,signal)).ExecuteAsync(claim,timeout.Token);
            await signal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(expiring.IsCompleted);await WaitPast(deadline);
        }
        finally{release.TrySetResult();}
        Assert.Null((await restoring).Error);Assert.NotNull(expiring);await Assert.ThrowsAsync<InvalidOperationException>(()=>expiring);
        var current=await TrashRestoreSetup.Resource(fixture,root);Assert.Equal(System.Text.Json.JsonValueKind.Null,current.GetProperty("deleted_at").ValueKind);Assert.Equal(root.Revision+1,current.GetProperty("revision").GetInt64());Assert.Empty(Directory.GetFiles(directory,"*.json"));
    }
    [FunctionalFact]
    public async Task GivenCascadedFolderAndIndependentTrash_WhenExpiring_ThenEraseOriginalUnitOnly()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var items=await AssociationSetup.Items(fixture,s);var independent=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");
        await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==items.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,items.Folder.Id));
        var deleted=await new FolderTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,items.Folder.PublicId,1),default);Assert.Null(deleted.Error);
        var root=new TrashRestoreSetup.Root("folder",items.Folder.PublicId,items.Folder.Id,deleted.Data!.TrashOperationId,deleted.Data.Revision,deleted.Data.ServerSequence,items.Folder.EditedAt,deleted.Data.DeletedAt,deleted.Data.PurgeAt);
        await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));
        await using(var db=fixture.CreateContext()){var operation=await db.TrashOperations.SingleAsync(x=>x.PublicId==root.OperationId);await db.Records.Where(x=>x.Id==items.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,operation.DeletedAt).SetProperty(r=>r.PurgeAt,operation.PurgeAt));}
        var before=await TrashRestoreSetup.Resource(fixture,independent);await Executor().ExecuteAsync(await Claim(root),default);
        Assert.False(await Exists(root));await using var check=fixture.CreateContext();Assert.False(await check.Records.AnyAsync(x=>x.Id==items.Record.Id));Assert.Equal(before.GetRawText(),(await TrashRestoreSetup.Resource(fixture,independent)).GetRawText());Assert.Equal(2,Directory.GetFiles(directory,"*.json").Length);
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenFolderLedgerPartlyWrittenOrPhysicalFault_WhenExpiring_ThenAtomicRemovalWaitsForCompleteDurability(bool physicalFault)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var items=await AssociationSetup.Items(fixture,s);
        await using(var db=fixture.CreateContext())await db.Records.Where(x=>x.Id==items.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.FolderId,items.Folder.Id));
        var deleted=await new FolderTrashStore(fixture).TrashAsync(new(s.Actor,s.Verifier,items.Folder.PublicId,1),default);Assert.Null(deleted.Error);
        var root=new TrashRestoreSetup.Root("folder",items.Folder.PublicId,items.Folder.Id,deleted.Data!.TrashOperationId,deleted.Data.Revision,deleted.Data.ServerSequence,items.Folder.EditedAt,deleted.Data.DeletedAt,deleted.Data.PurgeAt);await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddSeconds(-1));
        await using(var db=fixture.CreateContext()){var op=await db.TrashOperations.SingleAsync(x=>x.PublicId==root.OperationId);await db.Records.Where(x=>x.Id==items.Record.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,op.DeletedAt).SetProperty(r=>r.PurgeAt,op.PurgeAt));}
        var calls=0;var ledger=new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>{if(!physicalFault&&++calls==2)throw new IOException("partial ledger");return Task.CompletedTask;});
        var fault=new ExpiryStatement(c=>c.CommandText.StartsWith("DELETE FROM cerberus.folder",StringComparison.Ordinal),()=>throw new IOException("physical fault"));var claim=await Claim(root);
        await Assert.ThrowsAsync<IOException>(()=>Executor(ledger,physicalFault?new ProfileSetup.Factory(fixture,fault):fixture).ExecuteAsync(claim,default));
        Assert.True(await Exists(root));await using(var db=fixture.CreateContext())Assert.True(await db.Records.AnyAsync(x=>x.Id==items.Record.Id));Assert.Equal(physicalFault?2:1,Directory.GetFiles(directory,"*.json").Length);
        await Expire(claim);await Executor().ExecuteAsync(await Claim(root),default);Assert.False(await Exists(root));
    }
    private static async Task WaitPast(DateTimeOffset deadline){var delay=deadline-DateTimeOffset.UtcNow+TimeSpan.FromMilliseconds(30);if(delay>TimeSpan.Zero)await Task.Delay(delay);}
    private sealed class ExpiryStatement(Func<DbCommand,bool> match,Func<Task> action):DbCommandInterceptor
    {
        internal bool Reached;
        private async Task Visit(DbCommand c){if(!Reached&&match(c)){Reached=true;await action();}}
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){await Visit(c);return r;}
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){await Visit(c);return r;}
    }
    private sealed class AfterExpiryStatement(Func<DbCommand,bool> match,Func<Task> action):DbCommandInterceptor
    {
        private bool reached;
        public override async ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default)
        {if(!reached&&match(c)){reached=true;await action();}return r;}
    }
}
