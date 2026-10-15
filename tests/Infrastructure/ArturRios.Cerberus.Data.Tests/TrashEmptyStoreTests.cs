using System.Text.Json;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Data.Trash;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public partial class TrashEmptyStoreTests(PostgresFixture fixture):IDisposable
{
    private readonly string directory=Directory.CreateTempSubdirectory("cerberus-uc52-ledger-").FullName;
    private FileErasureLedger Ledger=>new(directory);
    private TrashEmptyStore Store(IErasureLedger? ledger=null,IDbContextFactory<AppDbContext>? factory=null)=>new(factory??fixture,ledger??Ledger,new CerberusOptions{RetentionInterval="00:05:00"},TimeProvider.System);
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenOneNativeOwnedTrashKind_WhenEmptying_ThenDurableTypedPhysicalErasureAndMetadataOnly(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);var protectedState=await TrashRestoreSetup.Protected(fixture,s);
        var r=await Store().EmptyAsync(Request(s),default);Assert.Null(r.Error);Assert.Equal(s.AccountId,r.Data!.AccountId);Assert.Equal(1,r.Data.TotalPurged);Assert.Equal(4,r.Data.Counts.Count);Assert.Equal(1,r.Data.Counts.Single(x=>x.Kind==kind).Count);Assert.All(r.Data.Counts.Where(x=>x.Kind!=kind),x=>Assert.Equal(0,x.Count));
        Assert.Equal(TimeSpan.Zero,r.Data.PurgedAt.Offset);Assert.Equal(0,r.Data.PurgedAt.Ticks%TimeSpan.TicksPerMicrosecond);Assert.DoesNotContain(root.ResourceId.ToString(),JsonSerializer.Serialize(r.Data));
        Assert.False(await Exists(root));Assert.Equal(protectedState,await TrashRestoreSetup.Protected(fixture,s));await TrashRestoreSetup.NoOperation(fixture,root.OperationId);
        var entries=new List<ErasureEntry>();await foreach(var e in Ledger.ReadAsync(default))entries.Add(e);var entry=Assert.Single(entries);Assert.Equal(kind,entry.ResourceKind);Assert.Equal(root.ResourceId,entry.ResourceId);
        await using var db=fixture.CreateContext();var marker=await db.TerminalErasures.AsNoTracking().SingleAsync(x=>x.ResourceKind==kind&&x.ResourceId==root.ResourceId);Assert.Equal(marker.DeletedAt,entry.DeletedAt);var work=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId);Assert.NotNull(work.CompletedAt);Assert.Equal(work.CompletedAt,r.Data.PurgedAt);
        var before=await State(s);var retry=await Store().EmptyAsync(Request(s),default);Assert.Null(retry.Error);Assert.Equal(0,retry.Data!.TotalPurged);Assert.Equal(before,await State(s));
    }
    [FunctionalFact]
    public async Task GivenNoCurrentTrash_WhenEmptying_ThenFourZeroCountsAndNoQueueOrLedgerWrites()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);await AssociationSetup.Items(fixture,s);var before=await State(s);
        var r=await Store(new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>throw new IOException("ledger must not be used"))).EmptyAsync(Request(s),default);
        Assert.Null(r.Error);Assert.Equal(0,r.Data!.TotalPurged);Assert.Equal(4,r.Data.Counts.Count);Assert.All(r.Data.Counts,x=>Assert.Equal(0,x.Count));Assert.Equal(before,await State(s));Assert.Empty(Directory.GetFiles(directory,"*.json"));
    }
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenExpiredOrDamagedClearlyOwnedTrash_WhenEmptying_ThenEraseWithoutDecryptingOrExtendingDeadline(string kind)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,kind);await TrashRestoreSetup.Deadline(fixture,root,DateTimeOffset.UtcNow.AddMinutes(-1));
        await using(var db=fixture.CreateContext()){await db.TrashEntries.Where(x=>x.ResourceKind==kind&&x.ResourceId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(e=>e.AssociationSnapshot,"bad history"u8.ToArray()));
            if(kind=="record")await db.Records.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()));
            if(kind=="folder")await db.Folders.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()));
            if(kind=="collection")await db.Collections.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()));
            if(kind=="profile")await db.Profiles.Where(x=>x.PublicId==root.ResourceId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,"bad"u8.ToArray()).SetProperty(r=>r.KeyWrappers,"bad"u8.ToArray()));}
        var r=await Store().EmptyAsync(Request(s),default);Assert.Null(r.Error);Assert.Equal(1,r.Data!.TotalPurged);Assert.False(await Exists(root));
    }
    [FunctionalTheory][InlineData("missing","not_found")][InlineData("closing","not_found")][InlineData("terminal","not_found")]
    [InlineData("access","vault_access_denied")][InlineData("selected","vault_access_denied")][InlineData("revoked","vault_access_denied")]
    [InlineData("expired","vault_access_denied")][InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")]
    [InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")][InlineData("nullExpiry","vault_access_denied")]
    [InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)]
    public async Task GivenInvalidCurrentOwnerOrWideAuthority_WhenEmptying_ThenFailBeforeIntent(string damage,string? error)
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");var p=damage=="selected"?await AssociationSetup.Profile(fixture,s,keys):0;
        await using(var db=fixture.CreateContext()){
            var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);
            if(damage=="closing")a.State=AccountState.ClosurePending;if(damage=="terminal")db.TerminalErasures.Add(new(){ResourceKind="account",ResourceId=s.AccountId,DeletedAt=DateTimeOffset.UtcNow});
            if(damage=="selected")v.ProfileId=p;if(damage=="revoked")v.Revoked=true;if(damage=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(damage=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);
            if(damage=="policy")v.PolicyRevision++;if(damage=="generation")v.RevocationGeneration++;if(damage=="zeroPolicy")v.PolicyRevision=0;if(damage=="zeroGeneration")v.RevocationGeneration=0;
            if(damage is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(damage is "nullExpiry" or "noExpiry")v.ExpiresAt=null;await db.SaveChangesAsync();}
        var before=await State(s);var request=Request(s) with{Actor=damage=="missing"?Guid.NewGuid():s.Actor,AccessVerifier=damage=="access"?new string('a',64):s.Verifier};var r=await Store().EmptyAsync(request,default);Assert.Equal(error,r.Error);
        if(error is not null){Assert.Null(r.Data);Assert.Equal(before,await State(s));Assert.Empty(Directory.GetFiles(directory,"*.json"));}else Assert.False(await Exists(root));
    }
    [FunctionalTheory][InlineData(0L,"validation_failed")][InlineData(-1L,"validation_failed")][InlineData(ProtocolBinary.MaxInteger+1,"validation_failed")][InlineData(2L,"revision_conflict")]
    public async Task GivenInvalidOrStaleAccountRevision_WhenEmptying_ThenNoIntentOrLedger(long revision,string error)
    {using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");var before=await State(s);var r=await Store().EmptyAsync(Request(s) with{ExpectedRevision=revision},default);Assert.Equal(error,r.Error);Assert.Equal(before,await State(s));Assert.Empty(Directory.GetFiles(directory,"*.json"));}
    [FunctionalFact]
    public async Task GivenLedgerOutageAfterAdmission_WhenEmptying_ThenRetainIntentAndCipherAndHideRestoreUntilSafeRetry()
    {
        using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);var root=await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");var old=await TrashRestoreSetup.Resource(fixture,root);
        var r=await Store(new PermanentDeleteSetup.LedgerBoundary(Ledger,()=>throw new IOException("fixture ledger unavailable"))).EmptyAsync(Request(s),default);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);Assert.True(await Exists(root));Assert.Equal(old.GetRawText(),(await TrashRestoreSetup.Resource(fixture,root)).GetRawText());
        await using var db=fixture.CreateContext();var marker=await db.TerminalErasures.AsNoTracking().SingleAsync(x=>x.ResourceKind=="record"&&x.ResourceId==root.ResourceId);var job=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId);Assert.NotNull(job.ClaimToken);Assert.Null(job.CompletedAt);
        Assert.Equal("not_found",(await new TrashRestoreStore(fixture).RestoreAsync(new(s.Actor,s.Verifier,root.OperationId,root.Revision),default)).Error);var page=await new TrashListStore(fixture).ListAsync(new(s.Actor,s.Verifier,50,0,null),default);Assert.Null(page.Error);Assert.Empty(page.Data!.Items);
        var pending=JsonSerializer.Serialize(job);var retry=await Store().EmptyAsync(Request(s),default);Assert.Equal("persistence_unavailable",retry.Error);Assert.Equal(pending,JsonSerializer.Serialize(await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.Id==job.Id)));Assert.Equal(marker.DeletedAt,(await db.TerminalErasures.AsNoTracking().SingleAsync(x=>x.ResourceKind=="record"&&x.ResourceId==root.ResourceId)).DeletedAt);
        await db.RetentionWorkItems.Where(x=>x.Id==job.Id).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));
        var success=await Store().EmptyAsync(Request(s),default);Assert.Null(success.Error);Assert.Equal(1,success.Data!.TotalPurged);Assert.False(await Exists(root));Assert.Equal(marker.DeletedAt,(await db.TerminalErasures.AsNoTracking().SingleAsync(x=>x.ResourceKind=="record"&&x.ResourceId==root.ResourceId)).DeletedAt);var entries=new List<ErasureEntry>();await foreach(var e in Ledger.ReadAsync(default))entries.Add(e);Assert.Equal(marker.DeletedAt,Assert.Single(entries).DeletedAt);
    }
    [FunctionalFact]
    public async Task GivenCallerCancellationBeforeAdmission_WhenEmptying_ThenPropagateWithoutIntent()
    {using var keys=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,keys);await TrashRestoreSetup.RootAsync(fixture,s,keys,"record");var before=await State(s);using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().EmptyAsync(Request(s),ct.Token));Assert.Equal(before,await State(s));Assert.Empty(Directory.GetFiles(directory,"*.json"));}
    private static TrashEmptyRequest Request(ProfileSetup.State s)=>new(s.Actor,s.Verifier,1);
    private async Task<bool> Exists(TrashRestoreSetup.Root root){await using var db=fixture.CreateContext();return root.Kind switch{"record"=>await db.Records.AnyAsync(x=>x.PublicId==root.ResourceId),"folder"=>await db.Folders.AnyAsync(x=>x.PublicId==root.ResourceId),"collection"=>await db.Collections.AnyAsync(x=>x.PublicId==root.ResourceId),_=>await db.Profiles.AnyAsync(x=>x.PublicId==root.ResourceId)};}
    private async Task<string> State(ProfileSetup.State s)
    {
        await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new{Owned=await TrashRestoreSetup.Snapshot(fixture,s),Markers=await db.TerminalErasures.AsNoTracking().OrderBy(x=>x.ResourceKind).ThenBy(x=>x.ResourceId).ToArrayAsync(),Work=await db.RetentionWorkItems.AsNoTracking().Where(x=>x.OperationKey=="trash-erase/"+s.AccountId).ToArrayAsync()});
    }
    public void Dispose()=>Directory.Delete(directory,true);
}
