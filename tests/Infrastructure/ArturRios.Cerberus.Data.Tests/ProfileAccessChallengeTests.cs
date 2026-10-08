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
public class ProfileAccessChallengeTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenCurrentOwnerWithoutAccessHandle_WhenIssuingProfileChallenge_ThenReturnOnlyBoundOpaqueBootstrapWithoutChangingContent(string mode)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var input=await Add(s,owner,scoped,mode);
        await using(var db=fixture.CreateContext()){await db.VaultAccessSessions.Where(x=>x.AccountId==s.InternalId).ExecuteDeleteAsync();await db.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.PolicyRevision,2).SetProperty(v=>v.RevocationGeneration,3));}
        var before=await Snapshot(input.ProfileId);var hash=Hash();var result=await Store().ChallengeAsync(new(s.Actor,input.ProfileId,1,hash),default);Assert.Null(result.Error);var d=result.Data!;var c=d.Challenge;
        Assert.Equal(s.AccountId,d.Profile.AccountId);Assert.Equal(input.ProfileId,d.Profile.ProfileId);Assert.Equal(1,d.Profile.Revision);Assert.Equal(input.Envelope,d.Profile.Envelope);Assert.Equal(input.KeyWrappers,d.Profile.KeyWrappers);Assert.True(d.Profile.IsValid(s.Actor,input.ProfileId,1));
        Assert.Equal("cerberus-challenge-v1",c.Format);Assert.Equal("unlock-profile",c.Operation);Assert.Equal("profile",c.ScopeKind);Assert.Equal(input.ProfileId,c.ScopeId);Assert.Equal(s.Actor,c.IdentityId);Assert.Equal(s.AccountId,c.AccountId);Assert.Equal(1,c.KeyEpoch);Assert.Equal(1,c.ProtectionRevision);Assert.Null(c.Generation);Assert.Equal(hash,c.RequestHash);Assert.Equal(c.IssuedAt+60,c.ExpiresAt);Assert.True(ProtocolBinary.TryDecode(c.Nonce,32,out _));Assert.NotEqual(Guid.Empty,c.ChallengeId);
        await using var check=fixture.CreateContext();var row=await check.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==c.ChallengeId);Assert.Equal(2,row.PolicyRevision);Assert.Equal(3,row.RevocationGeneration);Assert.False(row.Consumed);Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(c.ExpiresAt),row.ExpiresAt);Assert.Equal(c,JsonSerializer.Deserialize<VaultProofChallenge>(row.Challenge,ProtectionFixture.Json));Assert.False(await check.VaultAccessSessions.AnyAsync(x=>x.AccountId==s.InternalId));Assert.Equal(before,await Snapshot(input.ProfileId));
        using var json=JsonDocument.Parse(JsonSerializer.Serialize(d.Profile,ProtectionFixture.Json));Assert.Equal(5,json.RootElement.EnumerateObject().Count());
    }
    [FunctionalTheory][InlineData("foreign")][InlineData("missing")][InlineData("trash")][InlineData("erased")][InlineData("closingAccount")][InlineData("erasedAccount")][InlineData("missingAccount")]
    public async Task GivenHiddenProfileOrAccount_WhenIssuingWithStaleRevisionAndCorruptPins_ThenNotFoundBeforeConflictOrNativeValidation(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var target=kind=="foreign"?await ProfileSetup.Create(fixture,owner):s;var input=await Add(target,owner,scoped);
        await using(var db=fixture.CreateContext())
        {
            if(kind=="trash")await db.Profiles.Where(x=>x.PublicId==input.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));
            if(kind is "erased" or "erasedAccount"){db.TerminalErasures.Add(new(){ResourceId=kind=="erased"?input.ProfileId:s.AccountId,ResourceKind=kind=="erased"?"profile":"account",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
            if(kind=="closingAccount")await db.Accounts.Where(x=>x.Id==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.State,AccountState.ClosurePending));
            await db.VaultProtections.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Material,new byte[]{1}));
        }
        var before=await Snapshot(input.ProfileId);var result=await Store().ChallengeAsync(new(kind=="missingAccount"?Guid.NewGuid():s.Actor,kind=="missing"?Guid.NewGuid():input.ProfileId,55,Hash()),default);Assert.Equal("not_found",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(input.ProfileId));await NoChallenge(s);
    }
    [FunctionalFact]
    public async Task GivenChangedVisibleRevision_WhenIssuing_ThenConflictBeforeCorruptNativeMetadata()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var input=await Add(s,owner,scoped);await using(var db=fixture.CreateContext())await db.Profiles.Where(x=>x.PublicId==input.ProfileId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.KeyWrappers,new byte[]{1}));
        Assert.Equal("revision_conflict",(await Store().ChallengeAsync(new(s.Actor,input.ProfileId,2,Hash()),default)).Error);await NoChallenge(s);
    }
    [FunctionalTheory][InlineData("missingProtection")][InlineData("pins")][InlineData("pinEpoch")][InlineData("protectionRevision")][InlineData("envelope")][InlineData("wrappers")][InlineData("signature")][InlineData("roleReuse")][InlineData("grantRevision")][InlineData("contentEpoch")][InlineData("revision")][InlineData("sequence")][InlineData("unsafeSequence")][InlineData("time")][InlineData("policy")][InlineData("generation")][InlineData("unsafePolicy")][InlineData("renewal")]
    public async Task GivenCorruptRequiredCurrentState_WhenIssuing_ThenUnavailableWithoutWriting(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var input=await Add(s,owner,scoped);
        await using(var db=fixture.CreateContext())
        {
            var profile=await db.Profiles.SingleAsync(x=>x.PublicId==input.ProfileId);var protection=await db.VaultProtections.SingleAsync(x=>x.AccountId==s.InternalId);var account=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);
            if(kind=="missingProtection")db.VaultProtections.Remove(protection);if(kind=="pins")protection.Material=[1];if(kind=="pinEpoch")protection.KeyEpoch++;if(kind=="protectionRevision")protection.Revision=0;if(kind=="envelope")profile.Envelope=[1];if(kind=="wrappers")profile.KeyWrappers=[1];if(kind=="revision")profile.Revision=0;if(kind=="sequence")profile.ServerSequence=0;if(kind=="unsafeSequence")profile.ServerSequence=9007199254740992;if(kind=="time")profile.EditedAt=default;if(kind=="policy")account.PolicyRevision=0;if(kind=="unsafePolicy")account.PolicyRevision=9007199254740992;if(kind=="generation")account.RevocationGeneration=0;if(kind=="renewal")account.RenewalInterval=null;
            if(kind is "signature" or "roleReuse" or "grantRevision" or "contentEpoch")
            {
                var w=input.KeyWrappers;w=kind switch {"signature"=>w with{MasterKeyWrapper=w.MasterKeyWrapper with{Signature=ProtectionFixture.Encode(new byte[64])}},"roleReuse"=>w with{UnlockVerifier=owner.Material.UnlockVerifier},"grantRevision"=>w with{MasterKeyWrapper=w.MasterKeyWrapper with{GrantRevision=2}},_=>w with{MasterKeyWrapper=w.MasterKeyWrapper with{KeyEpoch=2}}};profile.KeyWrappers=JsonSerializer.SerializeToUtf8Bytes(w,ProtectionFixture.Json);
            }
            await db.SaveChangesAsync();
        }
        var before=await Snapshot(input.ProfileId);var result=await Store().ChallengeAsync(new(s.Actor,input.ProfileId,1,Hash()),default);Assert.Equal("persistence_unavailable",result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(input.ProfileId));await NoChallenge(s);
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenLegacyDuplicateScopedKeys_WhenIssuing_ThenNeverAdmitAnAmbiguousProfile(bool trashed)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var input=await Add(s,owner,scoped);var other=ProfileSetup.Input(s,owner,scoped);
        await using(var db=fixture.CreateContext()){db.Profiles.Add(new(){PublicId=other.ProfileId,AccountId=s.InternalId,Envelope=JsonSerializer.SerializeToUtf8Bytes(other.Envelope,ProtectionFixture.Json),KeyWrappers=JsonSerializer.SerializeToUtf8Bytes(other.KeyWrappers,ProtectionFixture.Json),EditedAt=other.EditedAt,DeletedAt=trashed?DateTimeOffset.UtcNow:null});await db.SaveChangesAsync();}
        Assert.Equal("persistence_unavailable",(await Store().ChallengeAsync(new(s.Actor,input.ProfileId,1,Hash()),default)).Error);await NoChallenge(s);
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenCorruptOtherRetainedScopedKey_WhenIssuing_ThenFailClosed(bool trashed)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();using var otherScoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var input=await Add(s,owner,scoped);var other=await Add(s,owner,otherScoped);
        await using(var db=fixture.CreateContext()){var row=await db.Profiles.SingleAsync(x=>x.PublicId==other.ProfileId);row.KeyWrappers=[1];if(trashed)row.DeletedAt=DateTimeOffset.UtcNow;await db.SaveChangesAsync();}
        Assert.Equal("persistence_unavailable",(await Store().ChallengeAsync(new(s.Actor,input.ProfileId,1,Hash()),default)).Error);await NoChallenge(s);
    }
    [FunctionalFact]
    public async Task GivenOlderSignedWrapperAndNewerProfileRevision_WhenIssuing_ThenBindCurrentRevisionAndRetainWrapper()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var input=await Add(s,owner,scoped);Assert.Null((await new ProfileAssociationStore(fixture).SetAsync(new(s.Actor,s.Verifier,input.ProfileId,new(1,[],[],[])),default)).Error);
        var result=await Store().ChallengeAsync(new(s.Actor,input.ProfileId,2,Hash()),default);Assert.Null(result.Error);Assert.Equal(2,result.Data!.Challenge.ProtectionRevision);Assert.Equal(1,result.Data.Profile.KeyWrappers.MasterKeyWrapper.GrantRevision);
    }
    [FunctionalFact]
    public async Task GivenConsumedExpiredAndLiveChallenges_WhenIssuing_ThenPruneOnlyExpiredOrConsumedOwnRows()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var foreign=await ProfileSetup.Create(fixture,owner);var input=await Add(s,owner,scoped);var expired=Seed(s.InternalId,false,true);var consumed=Seed(s.InternalId,true,false);var live=Seed(s.InternalId,false,false);var other=Seed(foreign.InternalId,false,true);
        await using(var db=fixture.CreateContext()){db.VaultUnlockChallenges.AddRange(expired,consumed,live,other);await db.SaveChangesAsync();}
        Assert.Null((await Store().ChallengeAsync(new(s.Actor,input.ProfileId,1,Hash()),default)).Error);await using var actual=fixture.CreateContext();Assert.False(await actual.VaultUnlockChallenges.AnyAsync(x=>x.PublicId==expired.PublicId || x.PublicId==consumed.PublicId));Assert.True(await actual.VaultUnlockChallenges.AnyAsync(x=>x.PublicId==live.PublicId));Assert.True(await actual.VaultUnlockChallenges.AnyAsync(x=>x.PublicId==other.PublicId));Assert.Equal(2,await actual.VaultUnlockChallenges.CountAsync(x=>x.AccountId==s.InternalId));
    }
    [FunctionalFact]
    public async Task GivenInsertFailureAfterPruning_WhenIssuing_ThenRollbackBothAndAllowRetry()
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var input=await Add(s,owner,scoped);var expired=Seed(s.InternalId,false,true);await using(var db=fixture.CreateContext()){db.VaultUnlockChallenges.Add(expired);await db.SaveChangesAsync();}
        var result=await new ProfileAccessStore(new ProfileSetup.Factory(fixture,new FailAfterInsert())).ChallengeAsync(new(s.Actor,input.ProfileId,1,Hash()),default);Assert.Equal("persistence_unavailable",result.Error);await using(var check=fixture.CreateContext())Assert.Equal(expired.PublicId,(await check.VaultUnlockChallenges.SingleAsync(x=>x.AccountId==s.InternalId)).PublicId);Assert.Null((await Store().ChallengeAsync(new(s.Actor,input.ProfileId,1,Hash()),default)).Error);
    }
    [FunctionalFact]
    public async Task GivenInvalidContextOrOutageAndCancellation_WhenIssuing_ThenRejectBeforeDependencyAndPropagateCallerCancellation()
    {
        var factory=new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=absent;Username=fixture;Password=fixture;Timeout=1").Options);var store=new ProfileAccessStore(factory);var request=new ProfileChallengeRequest(Guid.NewGuid(),Guid.NewGuid(),1,Hash());
        Assert.Equal("authentication_required",(await store.ChallengeAsync(request with{Actor=Guid.Empty},default)).Error);Assert.Equal("validation_failed",(await store.ChallengeAsync(request with{ExpectedRevision=0},default)).Error);Assert.Equal("persistence_unavailable",(await store.ChallengeAsync(request,default)).Error);using var c=new CancellationTokenSource();c.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>store.ChallengeAsync(request,c.Token));
    }
    [FunctionalTheory][InlineData("account")][InlineData("profile")]
    public async Task GivenLockWait_WhenIssuing_ThenChallengeLifetimeStartsAfterWait(string kind)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,owner);var input=await Add(s,owner,scoped);await using var held=fixture.CreateContext();await using var tx=await held.Database.BeginTransactionAsync();if(kind=="account")await held.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");else await held.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.profile WHERE public_id={input.ProfileId} FOR UPDATE");var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var pending=new ProfileAccessStore(new ProfileSetup.Factory(fixture,new Signal(reached,kind))).ChallengeAsync(new(s.Actor,input.ProfileId,1,Hash()),default);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);await Task.Delay(1100);var released=DateTimeOffset.UtcNow.ToUnixTimeSeconds();await tx.CommitAsync();var result=await pending;Assert.Null(result.Error);Assert.True(result.Data!.Challenge.IssuedAt>=released);Assert.Equal(60,result.Data.Challenge.ExpiresAt-result.Data.Challenge.IssuedAt);
    }
    private ProfileAccessStore Store()=>new(fixture);
    private static string Hash()=>ProtectionFixture.Encode(System.Security.Cryptography.SHA256.HashData("{\"expectedRevision\":1}"u8));
    private async Task<ProfileCreateInput> Add(ProfileSetup.State s,ProtectionFixture owner,ProtectionFixture scoped,string mode="Master"){var input=ProfileSetup.Input(s,owner,scoped,mode);Assert.Null((await new ProfileCreateStore(fixture).CreateAsync(new(s.Actor,s.Verifier,input),default)).Error);return input;}
    private async Task<string> Snapshot(Guid profile){await using var db=fixture.CreateContext();return JsonSerializer.Serialize(await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==profile));}
    private async Task NoChallenge(ProfileSetup.State s){await using var db=fixture.CreateContext();Assert.False(await db.VaultUnlockChallenges.AnyAsync(x=>x.AccountId==s.InternalId));}
    private static VaultUnlockChallenge Seed(long account,bool consumed,bool expired)=>new(){AccountId=account,PublicId=Guid.NewGuid(),Challenge=[1],PolicyRevision=1,RevocationGeneration=1,Consumed=consumed,ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(expired?-1:5)};
    private sealed class Signal(TaskCompletionSource reached,string kind):DbCommandInterceptor
    {public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){if(c.CommandText.Contains("FOR UPDATE")&&c.CommandText.Contains(kind=="account"?"cerberus.account":"cerberus.profile"))reached.TrySetResult();return ValueTask.FromResult(r);}}
    private sealed class FailAfterInsert:DbCommandInterceptor
    {public override ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand c,CommandExecutedEventData d,DbDataReader r,CancellationToken ct=default){if(c.CommandText.Contains("INSERT INTO cerberus.vault_unlock_challenge"))throw new TimeoutException("synthetic post-insert failure");return ValueTask.FromResult(r);}}
}
