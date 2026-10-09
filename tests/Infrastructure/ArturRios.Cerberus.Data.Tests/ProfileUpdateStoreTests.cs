using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class ProfileUpdateStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenOwnedProfile_WhenUpdatingTwice_ThenOpaqueContentAndSequenceAdvanceWithImmutableProtection(string mode)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped,mode);var before=await Row(i.ProfileId);
        var input=Input();var r=await Store().UpdateAsync(new(s.Actor,s.Verifier,i.ProfileId,input),default);Assert.Null(r.Error);Assert.Equal(2,r.Data!.Revision);Assert.True(r.Data.ServerSequence>before.ServerSequence);
        var row=await Row(i.ProfileId);Assert.Equal(input.Envelope,JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,ProtectionFixture.Json));Assert.Equal(input.EditedAt,row.EditedAt);Assert.Equal(before.KeyWrappers,row.KeyWrappers);Assert.Equal(before.AccountId,row.AccountId);
        var second=await Store().UpdateAsync(new(s.Actor,s.Verifier,i.ProfileId,Input(2)),default);Assert.Null(second.Error);Assert.Equal(3,second.Data!.Revision);Assert.True(second.Data.ServerSequence>r.Data.ServerSequence);
        await using var db=fixture.CreateContext();Assert.Equal(s.AccountEnvelope,(await db.Accounts.SingleAsync(x=>x.Id==s.InternalId)).DetailsEnvelope);Assert.Equal(1,(await db.VaultProtections.SingleAsync(x=>x.AccountId==s.InternalId)).Revision);Assert.False((await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier)).Revoked);
    }
    [FunctionalTheory][InlineData("foreign")][InlineData("trash")][InlineData("erased")][InlineData("missing")]
    public async Task GivenHiddenTarget_WhenUpdatingWithStaleRevision_ThenNotFoundBeforeConflictAndNoMutation(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=kind=="foreign"?await ProfileSetup.Create(fixture,owner):s;var i=await Add(target,owner,scoped);
        await using(var db=fixture.CreateContext()){if(kind=="trash")await db.Profiles.Where(x=>x.PublicId==i.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));if(kind=="erased"){db.TerminalErasures.Add(new(){ResourceId=i.ProfileId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        var before=await Snapshot(i.ProfileId);var r=await Store().UpdateAsync(new(s.Actor,s.Verifier,kind=="missing"?Guid.NewGuid():i.ProfileId,Input(22)),default);Assert.Equal("not_found",r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(i.ProfileId));
    }
    [FunctionalTheory][InlineData("valid",null)][InlineData("otherTarget","not_found")][InlineData("missing","vault_access_denied")][InlineData("foreign","vault_access_denied")][InlineData("trash","vault_access_denied")][InlineData("erased","vault_access_denied")]
    public async Task GivenSelectedSession_WhenUpdating_ThenOnlyCurrentOwnedSelectionPermitted(string kind,string? error)
    {
        using var additionalScoped1=new ProtectionFixture();
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var two=await Add(s,owner,additionalScoped1);var foreign=await ProfileSetup.Create(fixture,owner);var other=await Add(foreign,owner,scoped);
        await using(var db=fixture.CreateContext()){var p=await db.Profiles.SingleAsync(x=>x.PublicId==(kind=="foreign"?other.ProfileId:i.ProfileId));await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,kind=="missing"?long.MaxValue:p.Id));if(kind=="trash"){p.DeletedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();}if(kind=="erased"){db.TerminalErasures.Add(new(){ResourceId=p.PublicId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        var target=kind=="otherTarget"?two.ProfileId:i.ProfileId;var before=await Snapshot(target);var r=await Store().UpdateAsync(new(s.Actor,s.Verifier,target,Input()),default);Assert.Equal(error,r.Error);if(error is not null){Assert.Null(r.Data);Assert.Equal(before,await Snapshot(target));}else Assert.Equal(2,r.Data!.Revision);
    }
    [FunctionalTheory][InlineData("missing","not_found")][InlineData("closing","not_found")][InlineData("erased","not_found")]
    [InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")][InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")][InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")][InlineData("nullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)]
    public async Task GivenInvalidCurrentAccountOrSession_WhenUpdating_ThenFailClosed(string kind,string? error)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);
        await using(var db=fixture.CreateContext()){var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);if(kind=="closing")a.State=AccountState.ClosurePending;if(kind=="erased")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="zeroPolicy")v.PolicyRevision=0;if(kind=="zeroGeneration")v.RevocationGeneration=0;if(kind is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(kind is "nullExpiry" or "noExpiry")v.ExpiresAt=null;await db.SaveChangesAsync();}
        var before=await Snapshot(i.ProfileId);var r=await Store().UpdateAsync(new(kind=="missing"?Guid.NewGuid():s.Actor,kind=="access"?new string('a',64):s.Verifier,i.ProfileId,Input()),default);Assert.Equal(error,r.Error);if(error is not null){Assert.Null(r.Data);Assert.Equal(before,await Snapshot(i.ProfileId));}
    }
    [FunctionalFact]
    public async Task GivenConcurrentSameRevisionAndRetry_WhenUpdating_ThenExactlyOneWinner()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var request=new ProfileUpdateRequest(s.Actor,s.Verifier,i.ProfileId,Input());var other=request with {Input=Input()};
        var r=await Task.WhenAll(Store().UpdateAsync(request,default),Store().UpdateAsync(other,default));Assert.Single(r,x=>x.Error is null);Assert.Equal("revision_conflict",Assert.Single(r,x=>x.Error is not null).Error);var before=await Snapshot(i.ProfileId);Assert.Equal("revision_conflict",(await Store().UpdateAsync(request,default)).Error);Assert.Equal(before,await Snapshot(i.ProfileId));Assert.Equal(2,(await Row(i.ProfileId)).Revision);
    }
    [FunctionalTheory][InlineData("1999-12-31T23:59:59.9999999Z","1999-12-31T23:59:59.9999990Z")][InlineData("2000-01-01T00:00:00.0000000Z","2000-01-01T00:00:00.0000000Z")][InlineData("2000-01-01T00:00:00.0000009Z","2000-01-01T00:00:00.0000000Z")]
    public async Task GivenSubmicrosecondTimestamp_WhenUpdating_ThenNormalizeBeforePostgresBinding(string submitted,string expected)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var r=await Store().UpdateAsync(new(s.Actor,s.Verifier,i.ProfileId,Input() with {EditedAt=DateTimeOffset.Parse(submitted)}),default);Assert.Null(r.Error);Assert.Equal(DateTimeOffset.Parse(expected),r.Data!.EditedAt);Assert.Equal(DateTimeOffset.Parse(expected),(await Row(i.ProfileId)).EditedAt);
    }
    [FunctionalTheory][InlineData("epoch","validation_failed")][InlineData("envelope","persistence_unavailable")][InlineData("wrapper","persistence_unavailable")][InlineData("signature","persistence_unavailable")][InlineData("recipient","persistence_unavailable")][InlineData("futureGrant","persistence_unavailable")][InlineData("zeroRevision","persistence_unavailable")][InlineData("zeroSequence","persistence_unavailable")][InlineData("maxRevision","revision_conflict")][InlineData("noProtection","persistence_unavailable")][InlineData("pins","persistence_unavailable")]
    public async Task GivenInvalidEpochOrStoredMetadata_WhenUpdating_ThenPreserveAllState(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);
        await using(var db=fixture.CreateContext()){var p=await db.Profiles.SingleAsync(x=>x.PublicId==i.ProfileId);if(kind=="envelope")p.Envelope=[1];if(kind=="wrapper")p.KeyWrappers=[1];if(kind=="signature")p.KeyWrappers=JsonSerializer.SerializeToUtf8Bytes(i.KeyWrappers with {MasterKeyWrapper=i.KeyWrappers.MasterKeyWrapper with {Signature=ProtectionFixture.Encode(new byte[64])}},ProtectionFixture.Json);if(kind=="recipient")p.KeyWrappers=JsonSerializer.SerializeToUtf8Bytes(i.KeyWrappers with {MasterKeyWrapper=owner.Wrap(s.AccountId,"profile",i.ProfileId,Guid.NewGuid())},ProtectionFixture.Json);if(kind=="futureGrant")p.KeyWrappers=JsonSerializer.SerializeToUtf8Bytes(i.KeyWrappers with {MasterKeyWrapper=owner.Wrap(s.AccountId,"profile",i.ProfileId,s.Actor,1,2)},ProtectionFixture.Json);if(kind=="zeroRevision")p.Revision=0;if(kind=="zeroSequence")p.ServerSequence=0;if(kind=="maxRevision")p.Revision=ProtocolBinary.MaxInteger;if(kind=="noProtection")await db.VaultProtections.Where(x=>x.AccountId==s.InternalId).ExecuteDeleteAsync();if(kind=="pins")await db.VaultProtections.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Material,new byte[]{1}));await db.SaveChangesAsync();}
        var before=await Snapshot(i.ProfileId);var input=Input(kind=="maxRevision"?ProtocolBinary.MaxInteger:1);if(kind=="epoch")input=input with {Envelope=input.Envelope with {KeyEpoch=2}};var r=await Store().UpdateAsync(new(s.Actor,s.Verifier,i.ProfileId,input),default);Assert.Equal(error,r.Error);Assert.Null(r.Data);Assert.Equal(before,await Snapshot(i.ProfileId));
    }
    [FunctionalTheory][InlineData("actor","authentication_required")][InlineData("id","validation_failed")][InlineData("revision","validation_failed")][InlineData("time","validation_failed")][InlineData("offset","validation_failed")][InlineData("envelope","validation_failed")]
    public async Task GivenInvalidInternalRequest_WhenUpdating_ThenRejectBeforeMutation(string kind,string error)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var input=Input();input=kind switch {"revision"=>input with {ExpectedRevision=0},"time"=>input with {EditedAt=default},"offset"=>input with {EditedAt=DateTimeOffset.Now.ToOffset(TimeSpan.FromHours(1))},"envelope"=>input with {Envelope=null!},_=>input};var before=await Snapshot(i.ProfileId);var r=await Store().UpdateAsync(new(kind=="actor"?Guid.Empty:s.Actor,s.Verifier,kind=="id"?Guid.Empty:i.ProfileId,input),default);Assert.Equal(error,r.Error);Assert.Equal(before,await Snapshot(i.ProfileId));
    }
    [FunctionalTheory][InlineData("account")][InlineData("session")][InlineData("profile")]
    public async Task GivenNaturalExpiryDuringLockWait_WhenUpdating_ThenNoMutation(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var before=await Snapshot(i.ProfileId);var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));
        await using var holder=fixture.CreateContext();await using var tx=await holder.Database.BeginTransactionAsync();if(kind=="account")await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");else if(kind=="session")await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE handle_verifier={s.Verifier} FOR UPDATE");else await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.profile WHERE public_id={i.ProfileId} FOR UPDATE");
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var pending=new ProfileUpdateStore(new ProfileSetup.Factory(fixture,new Signal(reached,kind))).UpdateAsync(new(s.Actor,s.Verifier,i.ProfileId,Input()),default);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);var wait=deadline-DateTimeOffset.UtcNow;if(wait>TimeSpan.Zero)await Task.Delay(wait+TimeSpan.FromMilliseconds(100));await tx.CommitAsync();Assert.Equal("vault_access_denied",(await pending).Error);Assert.Equal(before,await Snapshot(i.ProfileId));
    }
    [FunctionalFact]
    public async Task GivenExpiryImmediatelyBeforeWrite_WhenUpdating_ThenStatementTimeDenies()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var before=await Snapshot(i.ProfileId);var deadline=DateTimeOffset.UtcNow.AddSeconds(2);await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));var r=await new ProfileUpdateStore(new ProfileSetup.Factory(fixture,new Expire(deadline))).UpdateAsync(new(s.Actor,s.Verifier,i.ProfileId,Input()),default);Assert.Equal("vault_access_denied",r.Error);Assert.Equal(before,await Snapshot(i.ProfileId));
    }
    [FunctionalFact]
    public async Task GivenFailureAfterWrite_WhenUpdating_ThenRollbackAndRetrySucceeds()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var request=new ProfileUpdateRequest(s.Actor,s.Verifier,i.ProfileId,Input());var before=await Snapshot(i.ProfileId);Assert.Equal("persistence_unavailable",(await new ProfileUpdateStore(new ProfileSetup.Factory(fixture,new Fail())).UpdateAsync(request,default)).Error);Assert.Equal(before,await Snapshot(i.ProfileId));Assert.Null((await Store().UpdateAsync(request,default)).Error);
    }
    [FunctionalFact]
    public async Task GivenSequenceExhaustion_WhenUpdating_ThenConflictAndRollback()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var before=await Snapshot(i.ProfileId);await using var db=fixture.CreateContext();var previous=await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();try{await db.Database.ExecuteSqlRawAsync("SELECT setval('cerberus.server_sequence',9007199254740991,true)");Assert.Equal("revision_conflict",(await Store().UpdateAsync(new(s.Actor,s.Verifier,i.ProfileId,Input()),default)).Error);Assert.Equal(before,await Snapshot(i.ProfileId));}finally{await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{previous},true)");}
    }
    [FunctionalFact]
    public async Task GivenOutageOrCancellation_WhenUpdating_ThenMapOutageAndPropagateCallerCancellation()
    {
        var request=new ProfileUpdateRequest(Guid.NewGuid(),new string('a',64),Guid.NewGuid(),Input());var dead=new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=absent;Username=fixture;Password=fixture;Timeout=1").Options);Assert.Equal("persistence_unavailable",(await new ProfileUpdateStore(dead).UpdateAsync(request,default)).Error);using var c=new CancellationTokenSource();c.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().UpdateAsync(request,c.Token));
    }
    [FunctionalTheory][InlineData("salt")][InlineData("nonce")]
    public async Task GivenChangedCiphertextReusesCurrentEncryptionContext_WhenUpdating_ThenRejectWithoutMutation(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var i=await Add(s,owner,scoped);var before=await Snapshot(i.ProfileId);var input=Input();input=input with {Envelope=kind=="salt"?input.Envelope with {KeySalt=i.Envelope.KeySalt,Ciphertext="BAUG"}:input.Envelope with {Nonce=i.Envelope.Nonce,Ciphertext="BAUG"}};
        var r=await Store().UpdateAsync(new(s.Actor,s.Verifier,i.ProfileId,input),default);Assert.Equal("validation_failed",r.Error);Assert.Equal(before,await Snapshot(i.ProfileId));
    }
    private ProfileUpdateStore Store()=>new(fixture);
    private static ProfileUpdateInput Input(long revision=1)=>new(revision,ProfileSetup.Envelope(),DateTimeOffset.Parse("2026-10-08T12:00:00Z"));
    private async Task<ProfileCreateInput> Add(ProfileSetup.State s,ProtectionFixture owner,ProtectionFixture scoped,string mode="Master"){var i=ProfileSetup.Input(s,owner,scoped,mode);Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,i),default)).Error);return i;}
    private async Task<Profile> Row(Guid id){await using var db=fixture.CreateContext();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==id);}
    private async Task<string> Snapshot(Guid id)=>JsonSerializer.Serialize(await Row(id));
    private sealed class Signal(TaskCompletionSource reached,string kind):DbCommandInterceptor
    {public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){if(c.CommandText.Contains("FOR UPDATE")&&c.CommandText.Contains(kind switch {"account"=>"cerberus.account","session"=>"cerberus.vault_access_session",_=>"cerberus.profile"}))reached.TrySetResult();return ValueTask.FromResult(r);}}
    private sealed class Expire(DateTimeOffset deadline):DbCommandInterceptor
    {public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){if(c.CommandText.Contains("UPDATE cerberus.profile")){var wait=deadline-DateTimeOffset.UtcNow;if(wait>TimeSpan.Zero)await Task.Delay(wait+TimeSpan.FromMilliseconds(100),ct);}return r;}}
    private sealed class Fail:DbCommandInterceptor
    {private bool written;public override ValueTask<int> NonQueryExecutedAsync(DbCommand c,CommandExecutedEventData d,int r,CancellationToken ct=default){if(c.CommandText.Contains("UPDATE cerberus.profile"))written=true;return ValueTask.FromResult(r);}public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){if(written)throw new TimeoutException("synthetic post-update failure");return ValueTask.FromResult(r);}}
}
