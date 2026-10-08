using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Tests;
[Collection("PostgreSQL")]
public class ProfileVerifierIsolationTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("Master",false)][InlineData("PerProfile",false)][InlineData("Master",true)][InlineData("PerProfile",true)]
    public async Task GivenScopedKeyAlreadyUsedByRetainedProfile_WhenCreatingAnother_ThenRejectWithoutWriting(string mode,bool trashed)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var first=ProfileSetup.Input(s,owner,scoped,mode);var store=new ProfileCreateStore(fixture);Assert.Null((await store.CreateAsync(new(s.Actor,s.Verifier,first),default)).Error);
        await using(var db=fixture.CreateContext())if(trashed)await db.Profiles.Where(x=>x.PublicId==first.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));
        var next=ProfileSetup.Input(s,owner,scoped,mode);var result=await store.CreateAsync(new(s.Actor,s.Verifier,next),default);Assert.Equal("validation_failed",result.Error);Assert.Null(result.Data);
        await using var check=fixture.CreateContext();Assert.Single(await check.Profiles.Where(x=>x.AccountId==s.InternalId).ToArrayAsync());Assert.False(await check.Profiles.AnyAsync(x=>x.PublicId==next.ProfileId));
    }
    [FunctionalFact]
    public async Task GivenSamePublicProfileIdAndScopedKey_WhenRetryingCreate_ThenPreserveConflictPrecedence()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var input=ProfileSetup.Input(s,owner,scoped);var store=new ProfileCreateStore(fixture);Assert.Null((await store.CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);Assert.Equal("revision_conflict",(await store.CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);
    }
    [FunctionalFact]
    public async Task GivenSameScopedPublicKeyInOtherAccount_WhenCreating_ThenKeepOwnerIdentityBoundary()
    {
        using var owner=new ProtectionFixture();using var other=new ProtectionFixture();using var scoped=new ProtectionFixture();var one=await ProfileSetup.Create(fixture,owner);var two=await ProfileSetup.Create(fixture,other);var store=new ProfileCreateStore(fixture);
        Assert.Null((await store.CreateAsync(new(one.Actor,one.Verifier,ProfileSetup.Input(one,owner,scoped)),default)).Error);Assert.Null((await store.CreateAsync(new(two.Actor,two.Verifier,ProfileSetup.Input(two,other,scoped)),default)).Error);
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenCorruptRetainedScopedVerifier_WhenCreating_ThenFailClosedAndPreserveState(bool trashed)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();using var nextScoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var first=ProfileSetup.Input(s,owner,scoped);var store=new ProfileCreateStore(fixture);Assert.Null((await store.CreateAsync(new(s.Actor,s.Verifier,first),default)).Error);
        await using(var db=fixture.CreateContext()){var row=await db.Profiles.SingleAsync(x=>x.PublicId==first.ProfileId);row.KeyWrappers=[1];if(trashed)row.DeletedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();}
        var next=ProfileSetup.Input(s,owner,nextScoped);Assert.Equal("persistence_unavailable",(await store.CreateAsync(new(s.Actor,s.Verifier,next),default)).Error);await using var actual=fixture.CreateContext();Assert.Single(await actual.Profiles.Where(x=>x.AccountId==s.InternalId).ToArrayAsync());
    }
    [FunctionalFact]
    public async Task GivenConcurrentCreatesWithSameScopedKey_WhenCommitting_ThenOnlyOneProfileUsesIt()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var store=new ProfileCreateStore(fixture);var one=ProfileSetup.Input(s,owner,scoped);var two=ProfileSetup.Input(s,owner,scoped);
        var results=await Task.WhenAll(store.CreateAsync(new(s.Actor,s.Verifier,one),default),store.CreateAsync(new(s.Actor,s.Verifier,two),default));Assert.Single(results,x=>x.Error is null);Assert.Single(results,x=>x.Error=="validation_failed");await using var db=fixture.CreateContext();Assert.Single(await db.Profiles.Where(x=>x.AccountId==s.InternalId).ToArrayAsync());
    }
}
