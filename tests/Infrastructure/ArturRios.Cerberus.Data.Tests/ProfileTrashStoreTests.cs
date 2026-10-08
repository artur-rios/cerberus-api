using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class ProfileTrashStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenOwnedProfile_WhenTrashing_ThenRetainOpaqueContentAndPersistOneRecoverableOperation(string mode)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped,mode);var before=await Row(i.ProfileId);
        var r=await Store().TrashAsync(new(s.Actor,s.Verifier,i.ProfileId,1),default);Assert.Null(r.Error);var data=r.Data!;Assert.Equal(i.ProfileId,data.ProfileId);Assert.NotEqual(Guid.Empty,data.TrashOperationId);Assert.Equal(2,data.Revision);Assert.True(data.ServerSequence>before.ServerSequence);Assert.Equal(TimeSpan.Zero,data.DeletedAt.Offset);Assert.Equal(TimeSpan.FromDays(30),data.PurgeAt-data.DeletedAt);
        var row=await Row(i.ProfileId);Assert.Equal(before.Envelope,row.Envelope);Assert.Equal(before.KeyWrappers,row.KeyWrappers);Assert.Equal(before.EditedAt,row.EditedAt);Assert.Equal(before.AccountId,row.AccountId);Assert.Equal(data.DeletedAt,row.DeletedAt);Assert.Equal(data.PurgeAt,row.PurgeAt);Assert.NotEqual(before.ConcurrencyStamp,row.ConcurrencyStamp);
        await using var db=fixture.CreateContext();var op=await db.TrashOperations.SingleAsync(x=>x.PublicId==data.TrashOperationId);Assert.Equal(s.InternalId,op.AccountId);Assert.Equal("profile",op.RootResourceKind);Assert.Equal(i.ProfileId,op.RootResourceId);Assert.Equal(data.DeletedAt,op.DeletedAt);Assert.Equal(data.PurgeAt,op.PurgeAt);var entry=await db.TrashEntries.SingleAsync(x=>x.OperationId==op.Id);Assert.Equal("profile",entry.ResourceKind);Assert.Equal(i.ProfileId,entry.ResourceId);var links=JsonSerializer.Deserialize<ProfileAssociationSnapshot>(entry.AssociationSnapshot,ProtectionFixture.Json)!;Assert.Empty(links.RecordIds);Assert.Empty(links.FolderIds);Assert.Empty(links.CollectionIds);var work=await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="trash/"+data.TrashOperationId);Assert.Equal(data.PurgeAt,work.DueAt);Assert.Null(work.CompletedAt);
        Assert.Equal(s.AccountEnvelope,(await db.Accounts.SingleAsync(x=>x.Id==s.InternalId)).DetailsEnvelope);Assert.Equal(1,(await db.VaultProtections.SingleAsync(x=>x.AccountId==s.InternalId)).Revision);Assert.False((await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier)).Revoked);
        Assert.Equal("not_found",(await new ProfileReadStore(fixture).ReadAsync(new(s.Actor,s.Verifier,i.ProfileId),default)).Error);
        var listed=await new ProfileListStore(fixture).ListAsync(new(s.Actor,s.Verifier,50,0,null),default);Assert.Null(listed.Error);Assert.DoesNotContain(listed.Data!.Items,x=>x.ProfileId==i.ProfileId);
    }
    [FunctionalFact]
    public async Task GivenSelectedHandles_WhenTrashing_ThenRevokeAllOwnedTargetSelectionsAndKeepOtherHandles()
    {
        using var additionalScoped1=new ProtectionFixture();
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var other=await Add(s,owner,additionalScoped1);var foreign=await ProfileSetup.Create(fixture,owner);var target=await Row(i.ProfileId);var unrelated=await Row(other.ProfileId);var handles=new[]{Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N"),Guid.NewGuid().ToString("N")};
        await using(var db=fixture.CreateContext()){for(var n=0;n<4;n++)db.VaultAccessSessions.Add(new(){AccountId=n==3?foreign.InternalId:s.InternalId,HandleVerifier=handles[n],ProfileId=n==2?unrelated.Id:target.Id,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();}
        Assert.Null((await Store().TrashAsync(new(s.Actor,handles[0],i.ProfileId,1),default)).Error);
        await using var check=fixture.CreateContext();for(var n=0;n<4;n++)Assert.Equal(n<2,(await check.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==handles[n])).Revoked);Assert.False((await check.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier)).Revoked);Assert.Equal("vault_access_denied",(await new ProfileReadStore(fixture).ReadAsync(new(s.Actor,handles[0],other.ProfileId),default)).Error);
    }
    [FunctionalTheory][InlineData("foreign")][InlineData("trash")][InlineData("erased")][InlineData("missing")]
    public async Task GivenHiddenTarget_WhenTrashingWithStaleRevision_ThenNotFoundWithoutOperation(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=kind=="foreign"?await ProfileSetup.Create(fixture,owner):s;var i=await Add(target,owner,scoped);
        await using(var db=fixture.CreateContext()){if(kind=="trash")await db.Profiles.Where(x=>x.PublicId==i.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));if(kind=="erased"){db.TerminalErasures.Add(new(){ResourceId=i.ProfileId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        var before=await Snapshot(i.ProfileId);Assert.Equal("not_found",(await Store().TrashAsync(new(s.Actor,s.Verifier,kind=="missing"?Guid.NewGuid():i.ProfileId,22),default)).Error);Assert.Equal(before,await Snapshot(i.ProfileId));await NoOperation(i.ProfileId);
    }
    [FunctionalTheory][InlineData("valid",null)][InlineData("otherTarget","not_found")][InlineData("missing","vault_access_denied")][InlineData("foreign","vault_access_denied")][InlineData("trash","vault_access_denied")][InlineData("erased","vault_access_denied")]
    public async Task GivenSelectedSession_WhenTrashing_ThenOnlyCurrentOwnedSelectionPermitted(string kind,string? error)
    {
        using var additionalScoped1=new ProtectionFixture();
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var two=await Add(s,owner,additionalScoped1);var foreign=await ProfileSetup.Create(fixture,owner);var other=await Add(foreign,owner,scoped);
        await using(var db=fixture.CreateContext()){var p=await db.Profiles.SingleAsync(x=>x.PublicId==(kind=="foreign"?other.ProfileId:i.ProfileId));await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,kind=="missing"?long.MaxValue:p.Id));if(kind=="trash"){p.DeletedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();}if(kind=="erased"){db.TerminalErasures.Add(new(){ResourceId=p.PublicId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        var target=kind=="otherTarget"?two.ProfileId:i.ProfileId;var before=await Snapshot(target);var r=await Store().TrashAsync(new(s.Actor,s.Verifier,target,1),default);Assert.Equal(error,r.Error);if(error is not null){Assert.Null(r.Data);Assert.Equal(before,await Snapshot(target));await NoOperation(target);}
    }
    [FunctionalTheory][InlineData("missing","not_found")][InlineData("closing","not_found")][InlineData("erased","not_found")]
    [InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")][InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")][InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")][InlineData("nullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)]
    public async Task GivenInvalidCurrentAccountOrSession_WhenTrashing_ThenFailClosed(string kind,string? error)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);
        await using(var db=fixture.CreateContext()){var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);if(kind=="closing")a.State=AccountState.ClosurePending;if(kind=="erased")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="zeroPolicy")v.PolicyRevision=0;if(kind=="zeroGeneration")v.RevocationGeneration=0;if(kind is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(kind is "nullExpiry" or "noExpiry")v.ExpiresAt=null;await db.SaveChangesAsync();}
        var before=await Snapshot(i.ProfileId);var r=await Store().TrashAsync(new(kind=="missing"?Guid.NewGuid():s.Actor,kind=="access"?new string('a',64):s.Verifier,i.ProfileId,1),default);Assert.Equal(error,r.Error);if(error is not null){Assert.Null(r.Data);Assert.Equal(before,await Snapshot(i.ProfileId));await NoOperation(i.ProfileId);}
    }
    [FunctionalFact]
    public async Task GivenConcurrentDeleteAndRetry_WhenTrashing_ThenOneOperationAndNoDeadlineExtension()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var request=new ProfileTrashRequest(s.Actor,s.Verifier,i.ProfileId,1);var r=await Task.WhenAll(Store().TrashAsync(request,default),Store().TrashAsync(request,default));Assert.Single(r,x=>x.Error is null);Assert.Equal("not_found",Assert.Single(r,x=>x.Error is not null).Error);var before=await Snapshot(i.ProfileId);Assert.Equal("not_found",(await Store().TrashAsync(request,default)).Error);Assert.Equal(before,await Snapshot(i.ProfileId));await using var db=fixture.CreateContext();Assert.Single(await db.TrashEntries.Where(x=>x.ResourceId==i.ProfileId).ToListAsync());
    }
    [FunctionalFact]
    public async Task GivenEditAndDeleteRace_WhenCommitting_ThenOnlyOneRevisionWins()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);
        var edit=new ProfileUpdateStore(fixture).UpdateAsync(new(s.Actor,s.Verifier,i.ProfileId,new(1,ProfileSetup.Envelope(),i.EditedAt)),default);var trash=Store().TrashAsync(new(s.Actor,s.Verifier,i.ProfileId,1),default);await Task.WhenAll(edit,trash);Assert.True((edit.Result.Error is null)^(trash.Result.Error is null));if(edit.Result.Error is null){Assert.Equal("revision_conflict",trash.Result.Error);await NoOperation(i.ProfileId);}else Assert.Equal("not_found",edit.Result.Error);Assert.Equal(2,(await Row(i.ProfileId)).Revision);
    }
    [FunctionalTheory][InlineData("zeroRevision","persistence_unavailable")][InlineData("zeroSequence","persistence_unavailable")][InlineData("maxRevision","revision_conflict")][InlineData("stale","revision_conflict")]
    public async Task GivenInvalidOrStaleMetadata_WhenTrashing_ThenPreserveAllState(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);await using(var db=fixture.CreateContext()){var p=await db.Profiles.SingleAsync(x=>x.PublicId==i.ProfileId);if(kind=="zeroRevision")p.Revision=0;if(kind=="zeroSequence")p.ServerSequence=0;if(kind=="maxRevision")p.Revision=ProtocolBinary.MaxInteger;await db.SaveChangesAsync();}var before=await Snapshot(i.ProfileId);Assert.Equal(error,(await Store().TrashAsync(new(s.Actor,s.Verifier,i.ProfileId,kind=="maxRevision"?ProtocolBinary.MaxInteger:kind=="stale"?2:1),default)).Error);Assert.Equal(before,await Snapshot(i.ProfileId));await NoOperation(i.ProfileId);
    }
    [FunctionalTheory][InlineData("account",1)][InlineData("session",1)][InlineData("profile",1)][InlineData("profile",22)]
    public async Task GivenNaturalExpiryDuringLockWait_WhenTrashing_ThenDenyBeforeRevisionAndPreserveState(string kind,long revision)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var before=await Snapshot(i.ProfileId);var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));await using var holder=fixture.CreateContext();await using var tx=await holder.Database.BeginTransactionAsync();if(kind=="account")await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");else if(kind=="session")await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE handle_verifier={s.Verifier} FOR UPDATE");else await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.profile WHERE public_id={i.ProfileId} FOR UPDATE");var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var pending=new ProfileTrashStore(new ProfileSetup.Factory(fixture,new Signal(reached,kind))).TrashAsync(new(s.Actor,s.Verifier,i.ProfileId,revision),default);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);var wait=deadline-DateTimeOffset.UtcNow;if(wait>TimeSpan.Zero)await Task.Delay(wait+TimeSpan.FromMilliseconds(100));await tx.CommitAsync();Assert.Equal("vault_access_denied",(await pending).Error);Assert.Equal(before,await Snapshot(i.ProfileId));await NoOperation(i.ProfileId);
    }
    [FunctionalFact]
    public async Task GivenExpiryImmediatelyBeforeWrite_WhenTrashing_ThenStatementTimeDenies()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var before=await Snapshot(i.ProfileId);var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));Assert.Equal("vault_access_denied",(await new ProfileTrashStore(new ProfileSetup.Factory(fixture,new Expire(deadline))).TrashAsync(new(s.Actor,s.Verifier,i.ProfileId,1),default)).Error);Assert.Equal(before,await Snapshot(i.ProfileId));await NoOperation(i.ProfileId);
    }
    [FunctionalTheory][InlineData("write")][InlineData("entry")][InlineData("queue")]
    public async Task GivenFailureDuringTrashTransaction_WhenTrashing_ThenRollbackRevocationAndRetrySucceeds(string stage)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);await using(var db=fixture.CreateContext()){var p=await db.Profiles.SingleAsync(x=>x.PublicId==i.ProfileId);await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,p.Id));}var request=new ProfileTrashRequest(s.Actor,s.Verifier,i.ProfileId,1);var before=await Snapshot(i.ProfileId);Assert.Equal("persistence_unavailable",(await new ProfileTrashStore(new ProfileSetup.Factory(fixture,new Fail(stage))).TrashAsync(request,default)).Error);Assert.Equal(before,await Snapshot(i.ProfileId));await NoOperation(i.ProfileId);await using(var db=fixture.CreateContext())Assert.False((await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier)).Revoked);Assert.Null((await Store().TrashAsync(request,default)).Error);
    }
    [FunctionalFact]
    public async Task GivenSequenceExhaustion_WhenTrashing_ThenConflictAndRollback()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var before=await Snapshot(i.ProfileId);await using var db=fixture.CreateContext();var previous=await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();try{await db.Database.ExecuteSqlRawAsync("SELECT setval('cerberus.server_sequence',9007199254740991,true)");Assert.Equal("revision_conflict",(await Store().TrashAsync(new(s.Actor,s.Verifier,i.ProfileId,1),default)).Error);Assert.Equal(before,await Snapshot(i.ProfileId));await NoOperation(i.ProfileId);}finally{await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{previous},true)");}
    }
    [FunctionalTheory][InlineData("actor","authentication_required")][InlineData("id","validation_failed")][InlineData("revision","validation_failed")][InlineData("unsafeRevision","validation_failed")]
    public async Task GivenInvalidInternalRequest_WhenTrashing_ThenRejectBeforeMutation(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var before=await Snapshot(i.ProfileId);Assert.Equal(error,(await Store().TrashAsync(new(kind=="actor"?Guid.Empty:s.Actor,s.Verifier,kind=="id"?Guid.Empty:i.ProfileId,kind=="revision"?0:kind=="unsafeRevision"?ProtocolBinary.MaxInteger+1:1),default)).Error);Assert.Equal(before,await Snapshot(i.ProfileId));await NoOperation(i.ProfileId);
    }
    [FunctionalFact]
    public async Task GivenOutageOrCancellation_WhenTrashing_ThenMapOutageAndPropagateCallerCancellation()
    {
        var request=new ProfileTrashRequest(Guid.NewGuid(),new string('a',64),Guid.NewGuid(),1);var dead=new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=absent;Username=fixture;Password=fixture;Timeout=1").Options);Assert.Equal("persistence_unavailable",(await new ProfileTrashStore(dead).TrashAsync(request,default)).Error);using var c=new CancellationTokenSource();c.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().TrashAsync(request,c.Token));
    }
    private ProfileTrashStore Store()=>new(fixture);
    private async Task<ProfileCreateInput> Add(ProfileSetup.State s,ProtectionFixture owner,ProtectionFixture scoped,string mode="Master"){var i=ProfileSetup.Input(s,owner,scoped,mode);Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,i),default)).Error);return i;}
    private async Task<Profile> Row(Guid id){await using var db=fixture.CreateContext();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==id);}
    private async Task<string> Snapshot(Guid id)=>JsonSerializer.Serialize(await Row(id));
    private async Task NoOperation(Guid id){await using var db=fixture.CreateContext();Assert.False(await db.TrashOperations.AnyAsync(x=>x.RootResourceId==id));Assert.False(await db.TrashEntries.AnyAsync(x=>x.ResourceId==id));}
    private sealed class Signal(TaskCompletionSource reached,string kind):DbCommandInterceptor
    {public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){if(c.CommandText.Contains("FOR UPDATE")&&c.CommandText.Contains(kind switch {"account"=>"cerberus.account","session"=>"cerberus.vault_access_session",_=>"cerberus.profile"}))reached.TrySetResult();return ValueTask.FromResult(r);}}
    private sealed class Expire(DateTimeOffset deadline):DbCommandInterceptor
    {public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){if(c.CommandText.Contains("UPDATE cerberus.profile")){var wait=deadline-DateTimeOffset.UtcNow;if(wait>TimeSpan.Zero)await Task.Delay(wait+TimeSpan.FromMilliseconds(100),ct);}return r;}}
    private sealed class Fail(string stage):DbCommandInterceptor
    {private bool written;public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default){if(c.CommandText.Contains("UPDATE cerberus.profile"))written=true;return ValueTask.FromResult(r);}public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){if(stage=="write"&&written || stage=="entry"&&c.CommandText.Contains("INSERT INTO cerberus.trash_entry") || stage=="queue"&&c.CommandText.Contains("INSERT INTO cerberus.retention_work_item"))throw new TimeoutException("synthetic trash failure");return ValueTask.FromResult(r);}}
}
