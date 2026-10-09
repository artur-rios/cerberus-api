using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class ProfileListStoreTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenForeignTrashErasedAndSequenceGaps_WhenPaging_ThenFilterBeforeTakeAndRetainBoundary()
    {
        using var additionalScoped1=new ProtectionFixture();using var additionalScoped2=new ProtectionFixture();using var additionalScoped3=new ProtectionFixture();using var additionalScoped4=new ProtectionFixture();
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var foreign=await ProfileSetup.Create(fixture,owner);
        var hidden=await Add(s,owner,scoped);await Add(foreign,owner,scoped);var erased=await Add(s,owner,additionalScoped1);var first=await Add(s,owner,additionalScoped2);var second=await Add(s,owner,additionalScoped3);
        await using(var db=fixture.CreateContext()){
            await db.Profiles.Where(x=>x.PublicId==hidden.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow));
            db.TerminalErasures.Add(new(){ResourceId=erased.ProfileId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
        var page=await Store().ListAsync(new(s.Actor,s.Verifier,1,0,null),default);
        Assert.Null(page.Error);Assert.Equal(first.ProfileId,Assert.Single(page.Data!.Items).ProfileId);Assert.True(page.Data.HasMore);Assert.Equal(second.ServerSequence,page.Data.Boundary);
        var later=await Add(s,owner,additionalScoped4);var next=await Store().ListAsync(new(s.Actor,s.Verifier,1,first.ServerSequence,page.Data.Boundary),default);
        Assert.Null(next.Error);Assert.Equal(second.ProfileId,Assert.Single(next.Data!.Items).ProfileId);Assert.False(next.Data.HasMore);Assert.DoesNotContain(next.Data.Items,x=>x.ProfileId==later.ProfileId);
        await using var check=fixture.CreateContext();Assert.Equal(5,await check.Profiles.CountAsync(x=>x.AccountId==s.InternalId));
    }
    [FunctionalTheory]
    [InlineData("valid",null)][InlineData("missing","vault_access_denied")][InlineData("foreign","vault_access_denied")]
    [InlineData("trash","vault_access_denied")][InlineData("erased","vault_access_denied")]
    public async Task GivenSelectedSession_WhenListing_ThenOnlyCurrentOwnedActiveSelection(string kind,string? error)
    {
        using var additionalScoped1=new ProtectionFixture();
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var first=await Add(s,owner,scoped);await Add(s,owner,additionalScoped1);
        var other=await ProfileSetup.Create(fixture,owner);var foreign=await Add(other,owner,scoped);
        await using(var db=fixture.CreateContext()){
            var profile=await db.Profiles.SingleAsync(x=>x.PublicId==(kind=="foreign"?foreign.ProfileId:first.ProfileId));
            await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ProfileId,kind=="missing"?long.MaxValue:profile.Id));
            if(kind=="trash"){profile.DeletedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();}
            if(kind=="erased"){db.TerminalErasures.Add(new(){ResourceId=profile.PublicId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}
        var page=await Store().ListAsync(new(s.Actor,s.Verifier,10,0,null),default);Assert.Equal(error,page.Error);
        if(error is null){Assert.Equal(first.ProfileId,Assert.Single(page.Data!.Items).ProfileId);Assert.False(page.Data.HasMore);}else Assert.Null(page.Data);
    }
    [FunctionalTheory]
    [InlineData("missing","not_found")][InlineData("closing","not_found")][InlineData("erased","not_found")]
    [InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("expired","vault_access_denied")]
    [InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")]
    [InlineData("zeroPolicy","vault_access_denied")][InlineData("zeroGeneration","vault_access_denied")]
    [InlineData("nullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")][InlineData("noExpiry",null)]
    public async Task GivenInvalidAccountOrSession_WhenListing_ThenFailClosedWithoutMutation(string kind,string? error)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);await Add(s,owner,scoped);
        await using(var db=fixture.CreateContext()){
            var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);
            if(kind=="closing")a.State=AccountState.ClosurePending;
            if(kind=="erased")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});
            if(kind=="revoked")v.Revoked=true;if(kind=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(kind=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);
            if(kind=="policy")v.PolicyRevision++;if(kind=="generation")v.RevocationGeneration++;if(kind=="zeroPolicy")v.PolicyRevision=0;if(kind=="zeroGeneration")v.RevocationGeneration=0;
            if(kind is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;if(kind is "nullExpiry" or "noExpiry")v.ExpiresAt=null;await db.SaveChangesAsync();}
        var result=await Store().ListAsync(new(kind=="missing"?Guid.NewGuid():s.Actor,kind=="access"?new string('a',64):s.Verifier,10,0,null),default);
        Assert.Equal(error,result.Error);if(error is not null)Assert.Null(result.Data);
        await using var check=fixture.CreateContext();Assert.Equal(1,await check.Profiles.CountAsync(x=>x.AccountId==s.InternalId));
    }
    [FunctionalFact]
    public async Task GivenEmptyOwner_WhenListing_ThenEmptyFinalPage()
    {using var owner=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var r=await Store().ListAsync(new(s.Actor,s.Verifier,10,0,null),default);Assert.Null(r.Error);Assert.Empty(r.Data!.Items);Assert.Equal(0,r.Data.Boundary);Assert.False(r.Data.HasMore);}
    [FunctionalFact]
    public async Task GivenRevocationBetweenPages_WhenContinuing_ThenDenyCurrentAccess()
    {
        using var additionalScoped1=new ProtectionFixture();using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);await Add(s,owner,scoped);await Add(s,owner,additionalScoped1);var r=(await Store().ListAsync(new(s.Actor,s.Verifier,1,0,null),default)).Data!;
        await using var db=fixture.CreateContext();await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revoked,true));
        Assert.Equal("vault_access_denied",(await Store().ListAsync(new(s.Actor,s.Verifier,1,r.Items[0].ServerSequence,r.Boundary),default)).Error);}
    [FunctionalFact]
    public async Task GivenUnavailablePersistence_WhenListing_ThenUnavailable()
    {using var db=fixture.CreateContext();await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.profile RENAME TO fixture_unavailable_profile");try{Assert.Equal("persistence_unavailable",(await Store().ListAsync(new(Guid.NewGuid(),new string('a',64),1,0,null),default)).Error);}finally{await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_profile RENAME TO profile");}}
    [FunctionalFact]
    public async Task GivenCancellation_WhenListing_ThenPropagate()
    {using var ct=new CancellationTokenSource();ct.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().ListAsync(new(Guid.NewGuid(),"a",1,0,null),ct.Token));}
    [FunctionalTheory][InlineData("zero")][InlineData("negative")][InlineData("excessive")][InlineData("duplicate")]
    public async Task GivenCorruptPermittedSequence_WhenListingFirstPage_ThenUnavailableInsteadOfOmission(string kind)
    {
        using var additionalScoped1=new ProtectionFixture();
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var first=await Add(s,owner,scoped);var second=await Add(s,owner,additionalScoped1);
        await using(var db=fixture.CreateContext())await db.Profiles.Where(x=>x.PublicId==second.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ServerSequence,kind=="zero"?0:kind=="negative"?-1:kind=="excessive"?long.MaxValue:first.ServerSequence));
        var result=await Store().ListAsync(new(s.Actor,s.Verifier,1,0,null),default);Assert.Equal("persistence_unavailable",result.Error);Assert.Null(result.Data);
        await using var check=fixture.CreateContext();Assert.Equal(2,await check.Profiles.CountAsync(x=>x.AccountId==s.InternalId));
    }
    [FunctionalFact]
    public async Task GivenCorruptInaccessibleSequences_WhenListing_ThenDoNotDiscloseOrDenyUnrelatedOwner()
    {
        using var additionalScoped1=new ProtectionFixture();
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var foreign=await ProfileSetup.Create(fixture,owner);var hidden=await Add(s,owner,scoped);var other=await Add(foreign,owner,scoped);var visible=await Add(s,owner,additionalScoped1);
        await using(var db=fixture.CreateContext()){await db.Profiles.Where(x=>x.PublicId==hidden.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.DeletedAt,DateTimeOffset.UtcNow).SetProperty(p=>p.ServerSequence,0));await db.Profiles.Where(x=>x.PublicId==other.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.ServerSequence,0));}
        var result=await Store().ListAsync(new(s.Actor,s.Verifier,1,0,null),default);Assert.Null(result.Error);Assert.Equal(visible.ProfileId,Assert.Single(result.Data!.Items).ProfileId);
    }
    private ProfileListStore Store()=>new(fixture);
    private async Task<ProfileCreateDetails> Add(ProfileSetup.State s,ProtectionFixture owner,ProtectionFixture scoped)
    {var r=await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,ProfileSetup.Input(s,owner,scoped)),default);Assert.Null(r.Error);return r.Data!;}
}
