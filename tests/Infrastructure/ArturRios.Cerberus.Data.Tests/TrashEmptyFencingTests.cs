using System.Data.Common;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace ArturRios.Cerberus.Data.Tests;

public partial class TrashEmptyStoreTests
{
    [FunctionalTheory]
    [InlineData("record",false)][InlineData("folder",false)][InlineData("collection",false)][InlineData("profile",false)]
    [InlineData("record",true)][InlineData("folder",true)][InlineData("collection",true)][InlineData("profile",true)]
    public async Task GivenPersistedClaimLostDuringLedgerIo_WhenEmptying_ThenNoPhysicalMutationAndDurableRetry(string kind,bool stolen)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);var before=await TrashRestoreSetup.Snapshot(fixture,s);
        var ledger=new PermanentDeleteSetup.LedgerBoundary(Ledger,async()=>{await using var db=fixture.CreateContext();var job=await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId);if(stolen)job.ClaimToken=Guid.NewGuid();else job.ClaimExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1);await db.SaveChangesAsync();});
        Assert.Equal("persistence_unavailable",(await Store(ledger).EmptyAsync(Request(s),default)).Error);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));Assert.True(await Exists(root));Assert.Single(Directory.GetFiles(directory,"*.json"));
        await Expire(s);Assert.Null((await Store().EmptyAsync(Request(s),default)).Error);Assert.False(await Exists(root));
    }
    [FunctionalTheory]
    [InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    [InlineData("trash_entry")][InlineData("trash_operation")][InlineData("retention_work_item")]
    [InlineData("selected_session")][InlineData("child_record")]
    public async Task GivenLeaseExpiresImmediatelyBeforeActualPhysicalStatement_WhenEmptying_ThenStatementFenceRollsBackPass(string target)
    {
        var kind=target is "selected_session"?"profile":target is "child_record"?"folder":target.StartsWith("trash_",StringComparison.Ordinal)||target=="retention_work_item"?"record":target;
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);
        await using(var db=fixture.CreateContext()){
            if(target=="selected_session"){var p=await db.Profiles.SingleAsync(x=>x.PublicId==root.ResourceId);db.VaultAccessSessions.Add(new(){AccountId=s.InternalId,ProfileId=p.Id,HandleVerifier=Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)),IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});}
            if(target=="child_record"){var folder=await db.Folders.SingleAsync(x=>x.PublicId==root.ResourceId);db.Records.Add(new(){AccountId=s.InternalId,PublicId=Guid.NewGuid(),FolderId=folder.Id,Envelope=folder.Envelope,EditedAt=DateTimeOffset.UnixEpoch});}await db.SaveChangesAsync();}
        var before=await TrashRestoreSetup.Snapshot(fixture,s);DateTimeOffset deadline=default;
        var ledger=new PermanentDeleteSetup.LedgerBoundary(Ledger,async()=>{deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using var db=fixture.CreateContext();await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash-erase/"+s.AccountId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.ClaimExpiresAt,deadline));});
        var prefix=target switch{"selected_session"=>"UPDATE cerberus.vault_access_session ","child_record"=>"UPDATE cerberus.record r ",_=>"DELETE FROM cerberus."+target+" "};
        var hook=new BeforeEmptyStatement(c=>c.CommandText.StartsWith(prefix,StringComparison.Ordinal),()=>WaitPast(deadline));
        Assert.Equal("persistence_unavailable",(await Store(ledger,new ProfileSetup.Factory(fixture,hook)).EmptyAsync(Request(s),default)).Error);Assert.True(hook.Reached);Assert.Equal(0,hook.Affected);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));Assert.True(await Exists(root));Assert.Single(Directory.GetFiles(directory,"*.json"));
        await Expire(s);Assert.Null((await Store().EmptyAsync(Request(s),default)).Error);Assert.False(await Exists(root));
    }
    [FunctionalTheory]
    [InlineData("account")][InlineData("vault_access_session")][InlineData("retention_work_item")]
    [InlineData("profile")][InlineData("collection")][InlineData("folder")][InlineData("record")]
    public async Task GivenWideSessionExpiresDuringActualAdmissionRowWait_WhenEmptying_Then403WithoutAnyIntent(string table)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await AssociationSetup.Profile(fixture,s,keys);
        DateTimeOffset deadline;await using(var db=fixture.CreateContext()){db.RetentionWorkItems.Add(new(){OperationKey="trash-erase/"+s.AccountId,DueAt=DateTimeOffset.UtcNow.AddMinutes(-1)});var session=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);deadline=DateTimeOffset.UtcNow.AddSeconds(2);session.ExpiresAt=deadline;await db.SaveChangesAsync();}
        var before=await State(s);await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();
        var sql=table switch{
            "account"=>"SELECT FROM cerberus.account WHERE id={0} FOR UPDATE",
            "vault_access_session"=>"SELECT FROM cerberus.vault_access_session WHERE account_id={0} FOR UPDATE",
            "retention_work_item"=>"SELECT FROM cerberus.retention_work_item WHERE operation_key={0} FOR UPDATE",
            "profile"=>"SELECT FROM cerberus.profile WHERE account_id={0} FOR UPDATE",
            "collection"=>"SELECT FROM cerberus.collection WHERE account_id={0} FOR UPDATE",
            "folder"=>"SELECT FROM cerberus.folder WHERE account_id={0} FOR UPDATE",
            _=>"SELECT FROM cerberus.record WHERE account_id={0} FOR UPDATE"};
        await held.Database.ExecuteSqlRawAsync(sql,table=="retention_work_item"?["trash-erase/"+s.AccountId]:new object[]{s.InternalId});
        var signal=new PermanentDeleteSetup.LockSignal(table);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));var pending=Store(factory:new ProfileSetup.Factory(fixture,signal)).EmptyAsync(Request(s),ct.Token);
        try{await signal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);await WaitPast(deadline);}finally{await tx.CommitAsync();}
        Assert.Equal("vault_access_denied",(await pending).Error);Assert.Equal(before,await State(s));Assert.Empty(Directory.GetFiles(directory,"*.json"));
    }
    [FunctionalFact]
    public async Task GivenSessionExpiresImmediatelyBeforeGuardedAdmission_WhenEmptying_Then403WithoutQueueOrTerminalIntent()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");var deadline=DateTimeOffset.UtcNow.AddSeconds(2);
        await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));var before=await State(s);
        var hook=new BeforeEmptyStatement(c=>c.CommandText.Contains("INSERT INTO cerberus.retention_work_item AS w",StringComparison.Ordinal),()=>WaitPast(deadline));
        Assert.Equal("vault_access_denied",(await Store(factory:new ProfileSetup.Factory(fixture,hook)).EmptyAsync(Request(s),default)).Error);Assert.True(hook.Reached);Assert.Equal(before,await State(s));Assert.Empty(Directory.GetFiles(directory,"*.json"));
    }
    [FunctionalTheory][InlineData("account")][InlineData("retention_work_item")][InlineData("profile")][InlineData("collection")][InlineData("folder")][InlineData("record")]
    public async Task GivenPersistedLeaseExpiresDuringActualWorkerRowWait_WhenPurging_ThenNoPhysicalRemoval(string table)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");await AssociationSetup.Profile(fixture,s,keys);
        Assert.Equal("persistence_unavailable",(await Store(new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>throw new IOException("leave pending intent"))).EmptyAsync(Request(s),default)).Error);
        DateTimeOffset deadline;RetentionClaim claim;await using(var db=fixture.CreateContext()){var job=await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId);deadline=DateTimeOffset.UtcNow.AddSeconds(2);job.ClaimExpiresAt=deadline;await db.SaveChangesAsync();claim=new(job.PublicId,job.ClaimToken!.Value,job.OperationKey,deadline);}
        var before=await TrashRestoreSetup.Snapshot(fixture,s);await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();
        var sql=table switch{
            "account"=>"SELECT FROM cerberus.account WHERE id={0} FOR UPDATE",
            "retention_work_item"=>"SELECT FROM cerberus.retention_work_item WHERE operation_key={0} FOR UPDATE",
            "profile"=>"SELECT FROM cerberus.profile WHERE account_id={0} FOR UPDATE",
            "collection"=>"SELECT FROM cerberus.collection WHERE account_id={0} FOR UPDATE",
            "folder"=>"SELECT FROM cerberus.folder WHERE account_id={0} FOR UPDATE",
            _=>"SELECT FROM cerberus.record WHERE account_id={0} FOR UPDATE"};
        await held.Database.ExecuteSqlRawAsync(sql,table=="retention_work_item"?["trash-erase/"+s.AccountId]:new object[]{s.InternalId});
        var signal=new PermanentDeleteSetup.LockSignal(table);using var ct=new CancellationTokenSource(TimeSpan.FromSeconds(20));var pending=new TrashPurgeHandler(new ProfileSetup.Factory(fixture,signal),Ledger).ExecuteAsync(claim,ct.Token);
        try{await signal.Reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);await WaitPast(deadline);}finally{await tx.CommitAsync();}
        await Assert.ThrowsAsync<InvalidOperationException>(()=>pending);Assert.Equal(before,await TrashRestoreSetup.Snapshot(fixture,s));Assert.True(await Exists(root));await Expire(s);Assert.Null((await Store().EmptyAsync(Request(s),default)).Error);Assert.False(await Exists(root));
    }
    private static async Task WaitPast(DateTimeOffset deadline){var left=deadline-DateTimeOffset.UtcNow;if(left>TimeSpan.Zero)await Task.Delay(left+TimeSpan.FromMilliseconds(150));}
    private sealed class BeforeEmptyStatement(Func<DbCommand,bool> match,Func<Task> before):DbCommandInterceptor
    {
        internal bool Reached{get;private set;}
        internal int Affected{get;private set;}=-1;
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default){if(Reached&&match(c))Affected=r;return ValueTask.FromResult(r);}
        private async Task Visit(DbCommand c){if(!Reached&&match(c)){Reached=true;await before();}}
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){await Visit(c);return r;}
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){await Visit(c);return r;}
    }
}
