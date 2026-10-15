using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Accounts;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public partial class AccountDeletionStoreTests(PostgresFixture fixture):IDisposable
{
    private readonly string directory=Path.Combine(Path.GetTempPath(),"cerberus-account-erasure-"+Guid.NewGuid().ToString("N"));
    private FileErasureLedger Ledger{get{if(!OperatingSystem.IsLinux())throw new PlatformNotSupportedException();Directory.CreateDirectory(directory,UnixFileMode.UserRead|UnixFileMode.UserWrite|UnixFileMode.UserExecute);return new(directory);}}
    private AccountDeletionStore Store(IDbContextFactory<AppDbContext>? factory=null,IErasureLedger? ledger=null)=>new(factory??fixture,ledger??Ledger,new CerberusOptions{RetentionInterval="00:01:00"},TimeProvider.System);
    [FunctionalFact]
    public async Task GivenOwnedAndForeignSharedData_WhenDeletingAccount_ThenPurgeOnlyOwnedDataAndReserveTypedIdentifiers()
    {
        var w=await Create();await using var db=fixture.CreateContext();var foreign=await Foreign(db,w);
        var r=await Store().DeleteAsync(Request(w.Owner),default);Assert.Null(r.Error);Assert.Equal("completed",r.Details!.State);Assert.Equal(w.Owner.AccountId,r.Details.AccountId);
        await Purged(db,w);Assert.Equal(foreign,await Foreign(db,w));Assert.Equal(2,await db.Accounts.Where(x=>x.Id==w.Recipient.InternalId).Select(x=>x.RevocationGeneration).SingleAsync());
        Assert.Equal(1,await db.Accounts.Where(x=>x.Id==w.Other.InternalId).Select(x=>x.RevocationGeneration).SingleAsync());
        foreach(var (kind,id) in new[]{("account",w.Owner.AccountId),("record",w.Owned.Record.PublicId),("folder",w.Owned.Folder.PublicId),("collection",w.Owned.Collection.PublicId),("grant",w.Incoming.PublicId),("grant",w.Outgoing.PublicId)})
            Assert.True(await new TerminalErasureStore(fixture).IsErasedAsync(kind,id,default));
        Assert.Equal("not_found",(await Store().DeleteAsync(Request(w.Owner),default)).Error);
        Assert.True(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="account-erase/"+w.Owner.AccountId&&x.CompletedAt!=null));
    }
    [FunctionalFact]
    public async Task GivenLedgerUnavailableAfterIntent_WhenDeletingThenRetryingWorker_ThenNeverReviveOrLoseTrackedErasure()
    {
        var w=await Create();var unavailable=new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>throw new IOException("fixture outage"));
        var r=await Store(ledger:unavailable).DeleteAsync(Request(w.Owner),default);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Details);
        await using var db=fixture.CreateContext();Assert.True(await db.Accounts.AnyAsync(x=>x.Id==w.Owner.InternalId));Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==w.Owner.AccountId));
        Assert.False(await db.VaultAccessSessions.AnyAsync(x=>x.AccountId==w.Owner.InternalId&&!x.Revoked));
        Assert.Equal("not_found",(await new AccountClosureCancellationStore(fixture).CancelAsync(new(w.Owner.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),2),default)).Error);
        var work=await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="account-erase/"+w.Owner.AccountId);Assert.Null(work.CompletedAt);
        await db.RetentionWorkItems.Where(x=>x.Id==work.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));
        var run=await new RetentionRunner(new RetentionWorkStore(fixture),new RetentionExecutor([new AccountPurgeHandler(fixture,Ledger)]),TimeProvider.System).RunOnceAsync(TimeSpan.FromMinutes(1),100,default);
        Assert.True(run.Completed>=1);await Purged(db,w);
    }
    [FunctionalTheory][InlineData("revision","revision_conflict")][InlineData("missing","not_found")]
    [InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")]
    [InlineData("selected","vault_access_denied")][InlineData("old","fresh_authentication_required")]
    [InlineData("future","fresh_authentication_required")]
    public async Task GivenInvalidCurrentOwnerAuthority_WhenDeleting_ThenDoNotCommitIntentOrReduction(string kind,string error)
    {
        var w=await Create();await using var db=fixture.CreateContext();var request=Request(w.Owner);
        if(kind=="revoked")await db.VaultAccessSessions.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revoked,true));
        if(kind=="selected")await db.VaultAccessSessions.Where(x=>x.AccountId==w.Owner.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,w.Profile));
        request=kind switch{"revision"=>request with{ExpectedRevision=2},"missing"=>request with{Actor=Guid.NewGuid()},"access"=>request with{AccessVerifier=new string('0',64)},"old"=>request with{AuthenticationIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-60},"future"=>request with{AuthenticationIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+120},_=>request};
        var before=await Snapshot(db,w.Owner);var result=await Store().DeleteAsync(request,default);Assert.Equal(error,result.Error);Assert.Equal(before,await Snapshot(db,w.Owner));
        Assert.False(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==w.Owner.AccountId));
    }
    [FunctionalFact]
    public async Task GivenFaultAfterNestedClosure_WhenCommittingIntent_ThenOuterTransactionRollsBackEveryReduction()
    {
        var w=await Create();await using var db=fixture.CreateContext();var before=await Snapshot(db,w.Owner);var fault=new IntentFault();
        var r=await Store(new ProfileSetup.Factory(fixture,fault)).DeleteAsync(Request(w.Owner),default);Assert.Equal("persistence_unavailable",r.Error);Assert.True(fault.Reached);
        Assert.Equal(before,await Snapshot(db,w.Owner));Assert.Null((await Store().DeleteAsync(Request(w.Owner),default)).Error);
    }
    [FunctionalFact]
    public async Task GivenBackupBeforeAccountDeletion_WhenRestoringAndReplayingLedger_ThenRemoveOwnedCiphertextBeforeTrafficAndKeepForeignContent()
    {
        // Other corruption tests intentionally retain FK-invalid rows in the shared fixture.
        // Restore a dedicated valid backup without replacing that shared database.
        var isolated=new PostgresFixture();
        try{await isolated.InitializeAsync();using var scenario=new AccountDeletionStoreTests(isolated);await scenario.VerifyAccountBackupReplay();}
        finally{await isolated.DisposeAsync();}
    }
    private async Task VerifyAccountBackupReplay()
    {
        var w=await Create();var inventory=await Enrich(w);await using var db=fixture.CreateContext();var foreign=await Foreign(db,w);var backup=await fixture.BackupAsync();
        Assert.Null((await Store().DeleteAsync(Request(w.Owner),default)).Error);await fixture.RestoreAsync(backup);
        Assert.True(await db.Accounts.AnyAsync(x=>x.PublicId==w.Owner.AccountId));var gate=new RestoreTrafficGate();
        Assert.True(await new RestoreReconciler(Ledger,new TerminalErasureStore(fixture),new RestorePermit(),gate).ReconcileAsync(default));Assert.True(gate.IsReady);
        await Purged(db,w);await InventoryPurged(db,w,inventory);Assert.Equal(foreign,await Foreign(db,w));Assert.Equal(2,await db.Accounts.Where(x=>x.Id==w.Recipient.InternalId).Select(x=>x.RevocationGeneration).SingleAsync());
        Assert.True(await new RestoreReconciler(Ledger,new TerminalErasureStore(fixture),new RestorePermit(),gate).ReconcileAsync(default));Assert.Equal(foreign,await Foreign(db,w));
        Assert.Equal(2,await db.Accounts.Where(x=>x.Id==w.Recipient.InternalId).Select(x=>x.RevocationGeneration).SingleAsync());
    }
    private sealed record World(ProfileSetup.State Owner,ProfileSetup.State Other,ProfileSetup.State Recipient,AssociationSetup.ResourceSet Owned,AssociationSetup.ResourceSet Foreign,CollectionGrant Incoming,CollectionGrant Outgoing,long Profile);
    private async Task<World> Create()
    {
        using var owner=new ProtectionFixture();using var other=new ProtectionFixture();using var recipient=new ProtectionFixture();
        var a=await ProfileSetup.Create(fixture,owner);var b=await ProfileSetup.Create(fixture,other);var c=await ProfileSetup.Create(fixture,recipient);
        var own=await AssociationSetup.Items(fixture,a);var foreign=await AssociationSetup.Items(fixture,b);var profile=await AssociationSetup.Profile(fixture,a,owner);
        var incoming=await AssociationSetup.Grant(fixture,b,other,a,owner,foreign.Collection);var outgoing=await AssociationSetup.Grant(fixture,a,owner,c,recipient,own.Collection);
        await AssociationSetup.Grant(fixture,b,other,c,recipient,foreign.Collection);
        await using var db=fixture.CreateContext();db.RegistrationOperations.Add(new(){OperationId=Guid.NewGuid(),AccountPublicId=a.AccountId,RequestFingerprint=new string('a',64),CompletedIdentityId=a.Actor});await db.SaveChangesAsync();
        await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.record SET folder_id={own.Folder.Id} WHERE id={own.Record.Id}");
        return new(a,b,c,own,foreign,incoming,outgoing,profile);
    }
    private static AccountDeletionRequest Request(ProfileSetup.State s)=>new(s.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),s.Verifier,1);
    private static async Task Purged(AppDbContext db,World w)
    {
        Assert.False(await db.Accounts.AnyAsync(x=>x.PublicId==w.Owner.AccountId));Assert.False(await db.Records.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.Folders.AnyAsync(x=>x.AccountId==w.Owner.InternalId));
        Assert.False(await db.Collections.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.Profiles.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.VaultProtections.AnyAsync(x=>x.AccountId==w.Owner.InternalId));
        Assert.False(await db.VaultAccessSessions.AnyAsync(x=>x.AccountId==w.Owner.InternalId));Assert.False(await db.RegistrationOperations.AnyAsync(x=>x.AccountPublicId==w.Owner.AccountId));
        Assert.False(await db.CollectionGrants.AnyAsync(x=>x.Id==w.Incoming.Id||x.Id==w.Outgoing.Id));
    }
    private static Task<string> Foreign(AppDbContext db,World w)=>db.Database.SqlQuery<string>($"""
        SELECT jsonb_build_object('account',(SELECT to_jsonb(a) FROM cerberus.account a WHERE id={w.Other.InternalId}),
          'records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.record r WHERE account_id={w.Other.InternalId}),
          'folders',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.folder r WHERE account_id={w.Other.InternalId}),
          'collections',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.collection r WHERE account_id={w.Other.InternalId}),
          'grants',(SELECT jsonb_agg(to_jsonb(g) ORDER BY id) FROM cerberus.collection_grant g WHERE g.recipient_account_id={w.Recipient.InternalId} AND g.collection_id={w.Foreign.Collection.Id}))::text AS "Value"
        """).SingleAsync();
    private static Task<string> Snapshot(AppDbContext db,ProfileSetup.State s)=>db.Database.SqlQuery<string>($"""
        SELECT jsonb_build_object('account',(SELECT to_jsonb(a) FROM cerberus.account a WHERE id={s.InternalId}),
        'sessions',(SELECT jsonb_agg(to_jsonb(v) ORDER BY id) FROM cerberus.vault_access_session v WHERE account_id={s.InternalId}),
        'work',(SELECT jsonb_agg(to_jsonb(w) ORDER BY id) FROM cerberus.retention_work_item w WHERE operation_key IN({"account-purge/"+s.AccountId},{"account-erase/"+s.AccountId})),
        'grants',(SELECT jsonb_agg(to_jsonb(g) ORDER BY g.id) FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id WHERE c.account_id={s.InternalId} OR g.recipient_account_id={s.InternalId}))::text AS "Value"
        """).SingleAsync();
    private sealed class IntentFault:DbCommandInterceptor
    {
        public bool Reached{get;private set;}
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int result,CancellationToken ct=default)
        {if(c.CommandText.Contains("INSERT INTO cerberus.terminal_erasure")){Reached=true;throw new TimeoutException("fixture intent failure");}return ValueTask.FromResult(result);}
    }
    private sealed class RestorePermit:IRestoreAuthorizationVerifier{public Task<bool> VerifyAsync(CancellationToken ct)=>Task.FromResult(true);}
    public void Dispose(){if(Directory.Exists(directory))Directory.Delete(directory,true);}
}
