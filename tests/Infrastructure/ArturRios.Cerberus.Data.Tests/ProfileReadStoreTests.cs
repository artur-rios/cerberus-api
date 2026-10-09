using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class ProfileReadStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenOwnedActiveProfile_WhenReading_ThenExactOpaqueSnapshotAndActualEmptyLinks(string mode)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var input=ProfileSetup.Input(s,owner,scoped,mode);await Add(s,input);
        var r=await Store().ReadAsync(new(s.Actor,s.Verifier,input.ProfileId),default);Assert.Null(r.Error);var p=r.Data!.Profile;
        Assert.Equal(input.ProfileId,p.ProfileId);Assert.Equal(input.Envelope,JsonSerializer.Deserialize<EncryptedEnvelope>(p.Envelope,ProtectionFixture.Json));Assert.Equal(input.KeyWrappers,JsonSerializer.Deserialize<ProfileKeyWrappers>(p.KeyWrappers,ProtectionFixture.Json));
        Assert.Equal(1,p.Revision);Assert.True(p.ServerSequence>0);Assert.Empty(r.Data.RecordIds);Assert.Empty(r.Data.FolderIds);Assert.Empty(r.Data.CollectionIds);
        await Unchanged(input.ProfileId,p.Envelope,p.KeyWrappers);
    }
    [FunctionalTheory][InlineData("foreign")][InlineData("trash")][InlineData("erased")][InlineData("missing")]
    public async Task GivenHiddenOrAbsentTarget_WhenReading_ThenSameNotFoundAndNoPayload(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=kind=="foreign"?await ProfileSetup.Create(fixture,owner):s;var i=ProfileSetup.Input(target,owner,scoped);await Add(target,i);
        await using(var db=fixture.CreateContext()){
            if(kind=="trash")await db.Profiles.Where(x=>x.PublicId==i.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));
            if(kind=="erased"){db.TerminalErasures.Add(new(){ResourceId=i.ProfileId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        var r=await Store().ReadAsync(new(s.Actor,s.Verifier,kind=="missing"?Guid.NewGuid():i.ProfileId),default);Assert.Equal("not_found",r.Error);Assert.Null(r.Data);
        await using var check=fixture.CreateContext();Assert.Equal(1,(await check.Profiles.SingleAsync(x=>x.PublicId==i.ProfileId)).Revision);
    }
    [FunctionalTheory][InlineData("valid",null)][InlineData("otherTarget","not_found")][InlineData("missing","vault_access_denied")]
    [InlineData("foreign","vault_access_denied")][InlineData("trash","vault_access_denied")][InlineData("erased","vault_access_denied")]
    public async Task GivenSelectedSession_WhenReading_ThenRestrictToCurrentOwnedActiveSelection(string kind,string? error)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var one=ProfileSetup.Input(s,owner,scoped);using var secondScoped=new ProtectionFixture();var two=ProfileSetup.Input(s,owner,secondScoped);await Add(s,one);await Add(s,two);
        var foreign=await ProfileSetup.Create(fixture,owner);var other=ProfileSetup.Input(foreign,owner,scoped);await Add(foreign,other);
        await using(var db=fixture.CreateContext()){
            var p=await db.Profiles.SingleAsync(x=>x.PublicId==(kind=="foreign"?other.ProfileId:one.ProfileId));
            await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,kind=="missing"?long.MaxValue:p.Id));
            if(kind=="trash"){p.DeletedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();}
            if(kind=="erased"){db.TerminalErasures.Add(new(){ResourceId=p.PublicId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        var r=await Store().ReadAsync(new(s.Actor,s.Verifier,kind=="otherTarget"?two.ProfileId:one.ProfileId),default);Assert.Equal(error,r.Error);
        if(error is null)Assert.Equal(one.ProfileId,r.Data!.Profile.ProfileId);else Assert.Null(r.Data);
        await using var check=fixture.CreateContext();Assert.Equal(2,await check.Profiles.CountAsync(x=>x.AccountId==s.InternalId));
    }
    [FunctionalTheory][InlineData("missing","not_found")][InlineData("closing","not_found")][InlineData("erased","not_found")]
    [InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")]
    [InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")]
    [InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")]
    [InlineData("nullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)]
    public async Task GivenInvalidCurrentAccountOrSession_WhenReading_ThenFailClosedUnchanged(string kind,string? error)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var input=ProfileSetup.Input(s,owner,scoped);await Add(s,input);
        await using(var db=fixture.CreateContext()){
            var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);
            if(kind=="closing")a.State=AccountState.ClosurePending;if(kind=="erased")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});
            if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);
            if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="zeroPolicy")v.PolicyRevision=0;if(kind=="zeroGeneration")v.RevocationGeneration=0;
            if(kind is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(kind is "nullExpiry" or "noExpiry")v.ExpiresAt=null;await db.SaveChangesAsync();}
        var r=await Store().ReadAsync(new(kind=="missing"?Guid.NewGuid():s.Actor,kind=="access"?new string('a',64):s.Verifier,input.ProfileId),default);Assert.Equal(error,r.Error);if(error is not null)Assert.Null(r.Data);
        await using var check=fixture.CreateContext();var p=await check.Profiles.SingleAsync(x=>x.PublicId==input.ProfileId);Assert.Equal(1,p.Revision);Assert.Equal(input.Envelope,JsonSerializer.Deserialize<EncryptedEnvelope>(p.Envelope,ProtectionFixture.Json));
    }
    [FunctionalFact]
    public async Task GivenUnavailableProfileStorage_WhenReading_ThenUnavailable()
    {await using var db=fixture.CreateContext();await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.profile RENAME TO fixture_unavailable_profile");try{var r=await Store().ReadAsync(new(Guid.NewGuid(),new string('a',64),Guid.NewGuid()),default);Assert.Equal("persistence_unavailable",r.Error);Assert.Null(r.Data);}finally{await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_profile RENAME TO profile");}}
    [FunctionalFact]
    public async Task GivenCancellation_WhenReading_ThenPropagate()
    {using var c=new CancellationTokenSource();c.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().ReadAsync(new(Guid.NewGuid(),"a",Guid.NewGuid()),c.Token));}
    private ProfileReadStore Store()=>new(fixture);
    private async Task Add(ProfileSetup.State s,ProfileCreateInput input)=>Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);
    private async Task Unchanged(Guid id,byte[] envelope,byte[] wrappers)
    {await using var db=fixture.CreateContext();var p=await db.Profiles.SingleAsync(x=>x.PublicId==id);Assert.Equal(envelope,p.Envelope);Assert.Equal(wrappers,p.KeyWrappers);Assert.Equal(1,p.Revision);Assert.Null(p.DeletedAt);}
}
