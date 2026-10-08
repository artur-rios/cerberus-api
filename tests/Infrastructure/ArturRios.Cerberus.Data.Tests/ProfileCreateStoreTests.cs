using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Data.Profiles;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class ProfileCreateStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenActiveOwnerAndNativeWrapper_WhenCreating_ThenPersistOpaqueProfileAndMonotonicSequence(string mode)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);
        var input=ProfileSetup.Input(s,client,scoped,mode);var result=await Store().CreateAsync(new(s.Actor,s.Verifier,input),default);
        Assert.Null(result.Error);Assert.Equal(input.ProfileId,result.Data!.ProfileId);Assert.Equal(1,result.Data.Revision);Assert.True(result.Data.ServerSequence>0);
        await using var db=fixture.CreateContext();var row=await db.Profiles.SingleAsync(x=>x.PublicId==input.ProfileId);
        Assert.Equal(s.InternalId,row.AccountId);Assert.Equal(input.EditedAt.ToUnixTimeMilliseconds(),row.EditedAt.ToUnixTimeMilliseconds());
        Assert.Equal(input.Envelope,JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,ProtectionFixture.Json));
        Assert.Equal(input.KeyWrappers,JsonSerializer.Deserialize<ProfileKeyWrappers>(row.KeyWrappers,ProtectionFixture.Json));Assert.Null(row.DeletedAt);Assert.Null(row.PurgeAt);
        using var secondScoped=new ProtectionFixture();
        var second=await Store().CreateAsync(new(s.Actor,s.Verifier,ProfileSetup.Input(s,client,secondScoped,mode)),default);
        Assert.Null(second.Error);Assert.True(second.Data!.ServerSequence>result.Data.ServerSequence);
        Assert.Equal(s.AccountEnvelope,(await db.Accounts.SingleAsync(x=>x.Id==s.InternalId)).DetailsEnvelope);
        Assert.Equal(1,(await db.VaultProtections.SingleAsync(x=>x.AccountId==s.InternalId)).Revision);
    }
    [FunctionalTheory]
    [InlineData("missing","not_found")][InlineData("closing","not_found")][InlineData("erased","not_found")][InlineData("noProtection","not_found")]
    [InlineData("access","vault_access_denied")][InlineData("revoked","vault_access_denied")][InlineData("profile","vault_access_denied")]
    [InlineData("expired","vault_access_denied")][InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")]
    [InlineData("generation","vault_access_denied")][InlineData("nullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")]
    [InlineData("noExpiry",null)][InlineData("corrupt","persistence_unavailable")][InlineData("pinEpoch","persistence_unavailable")]
    public async Task GivenInvalidOwnerOrAccess_WhenCreating_ThenFailClosed(string state,string? error)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);
        await using(var db=fixture.CreateContext())
        {
            var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);
            if(state=="closing")a.State=AccountState.ClosurePending;
            if(state=="erased")db.TerminalErasures.Add(new(){ResourceId=s.AccountId,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow});
            if(state=="noProtection")await db.VaultProtections.Where(x=>x.AccountId==s.InternalId).ExecuteDeleteAsync();
            if(state=="corrupt")await db.VaultProtections.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Material,new byte[]{1}));
            if(state=="pinEpoch")await db.VaultProtections.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.KeyEpoch,2));
            if(state=="revoked")v.Revoked=true;if(state=="profile")v.ProfileId=123;
            if(state=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(state=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);
            if(state=="policy")v.PolicyRevision=2;if(state=="generation")v.RevocationGeneration=2;
            if(state=="nullExpiry")v.ExpiresAt=null;if(state is "noExpiry" or "disabledExpiry")a.RenewalEnabled=false;
            if(state=="noExpiry")v.ExpiresAt=null;await db.SaveChangesAsync();
        }
        var result=await Store().CreateAsync(new(state=="missing"?Guid.NewGuid():s.Actor,state=="access"?new string('a',64):s.Verifier,ProfileSetup.Input(s,client,scoped)),default);
        Assert.Equal(error,result.Error);await using var check=fixture.CreateContext();Assert.Equal(error is null?1:0,await check.Profiles.CountAsync(x=>x.AccountId==s.InternalId));
    }
    [FunctionalTheory][InlineData("records")][InlineData("folders")][InlineData("collections")]
    public async Task GivenUnresolvableRelationship_WhenCreating_ThenReturnNonrevealingNotFound(string kind)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var i=ProfileSetup.Input(s,client,scoped);
        i=kind switch {"records"=>i with {RecordIds=[Guid.NewGuid()]},"folders"=>i with {FolderIds=[Guid.NewGuid()]},_=>i with {CollectionIds=[Guid.NewGuid()]}};
        Assert.Equal("not_found",(await Store().CreateAsync(new(s.Actor,s.Verifier,i),default)).Error);
        await using var db=fixture.CreateContext();Assert.False(await db.Profiles.AnyAsync(x=>x.AccountId==s.InternalId));
    }
    [FunctionalTheory][InlineData("owner")][InlineData("actor")][InlineData("resource")][InlineData("epoch")][InlineData("roleReuse")][InlineData("signature")]
    public async Task GivenWrongNativeWrapperBinding_WhenCreating_ThenRejectBeforePersistence(string invalid)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var i=ProfileSetup.Input(s,client,scoped);
        var w=client.Wrap(invalid=="owner"?Guid.NewGuid():s.AccountId,"profile",invalid=="resource"?Guid.NewGuid():i.ProfileId,invalid=="actor"?Guid.NewGuid():s.Actor,invalid=="epoch"?2:1);
        if(invalid=="signature")w=w with {Signature=ProtectionFixture.Encode(new byte[64])};
        i=i with {KeyWrappers=i.KeyWrappers with {MasterKeyWrapper=w,UnlockVerifier=invalid=="roleReuse"?client.Material.UnlockVerifier:i.KeyWrappers.UnlockVerifier}};
        Assert.Equal("validation_failed",(await Store().CreateAsync(new(s.Actor,s.Verifier,i),default)).Error);
        await using var db=fixture.CreateContext();Assert.False(await db.Profiles.AnyAsync(x=>x.AccountId==s.InternalId));
    }
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenDuplicateOrPermanentlyReservedId_WhenCreating_ThenConflictAndPreserveWinner(bool erased)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var i=ProfileSetup.Input(s,client,scoped);
        await using(var db=fixture.CreateContext())
        {
            if(erased){db.TerminalErasures.Add(new(){ResourceId=i.ProfileId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}
            else Assert.Null((await Store().CreateAsync(new(s.Actor,s.Verifier,i),default)).Error);
        }
        Assert.Equal("revision_conflict",(await Store().CreateAsync(new(s.Actor,s.Verifier,i),default)).Error);
        await using var check=fixture.CreateContext();Assert.Equal(erased?0:1,await check.Profiles.CountAsync(x=>x.PublicId==i.ProfileId));
    }
    [FunctionalFact]
    public async Task GivenConcurrentIdenticalCreates_WhenCommitting_ThenOneWinner()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var request=new ProfileCreateRequest(s.Actor,s.Verifier,ProfileSetup.Input(s,client,scoped));
        var results=await Task.WhenAll(Store().CreateAsync(request,default),Store().CreateAsync(request,default));
        Assert.Single(results,x=>x.Error is null);Assert.Equal("revision_conflict",Assert.Single(results,x=>x.Error is not null).Error);
        await using var db=fixture.CreateContext();Assert.Single(await db.Profiles.Where(x=>x.AccountId==s.InternalId).ToListAsync());
    }
    [FunctionalTheory][InlineData("account")][InlineData("session")]
    public async Task GivenNaturalExpiryDuringLockWait_WhenCreating_ThenPersistNothing(string locked)
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);
        var deadline=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+2);
        await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));
        await using var holder=fixture.CreateContext();await using var tx=await holder.Database.BeginTransactionAsync();
        if(locked=="account")await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
        else await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE handle_verifier={s.Verifier} FOR UPDATE");
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending=new ProfileCreateStore(new ProfileSetup.Factory(fixture,new Signal(reached,locked))).CreateAsync(new(s.Actor,s.Verifier,ProfileSetup.Input(s,client,scoped)),default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);var remaining=deadline-DateTimeOffset.UtcNow;
        if(remaining>TimeSpan.Zero)await Task.Delay(remaining+TimeSpan.FromMilliseconds(100));await tx.CommitAsync();
        Assert.Equal("vault_access_denied",(await pending).Error);await using var check=fixture.CreateContext();Assert.False(await check.Profiles.AnyAsync(x=>x.AccountId==s.InternalId));
    }
    [FunctionalFact]
    public async Task GivenFailureAfterInsert_WhenCreating_ThenRollbackAndAllowRetry()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var request=new ProfileCreateRequest(s.Actor,s.Verifier,ProfileSetup.Input(s,client,scoped));
        Assert.Equal("persistence_unavailable",(await new ProfileCreateStore(new ProfileSetup.Factory(fixture,new FailAfterInsert())).CreateAsync(request,default)).Error);
        await using(var db=fixture.CreateContext())Assert.False(await db.Profiles.AnyAsync(x=>x.AccountId==s.InternalId));
        Assert.Null((await Store().CreateAsync(request,default)).Error);
    }
    [FunctionalFact]
    public async Task GivenDatabaseUnavailableOrCallerCancellation_WhenCreating_ThenMapOutageAndPropagateCancellation()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var request=new ProfileCreateRequest(s.Actor,s.Verifier,ProfileSetup.Input(s,client,scoped));
        var dead=new ProfileSetup.Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=absent;Username=fixture;Password=fixture;Timeout=1").Options);
        Assert.Equal("persistence_unavailable",(await new ProfileCreateStore(dead).CreateAsync(request,default)).Error);
        using var cts=new CancellationTokenSource();cts.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().CreateAsync(request,cts.Token));
    }
    [FunctionalFact]
    public async Task GivenAccessExpiresAfterValidationBeforeInsert_WhenCreating_ThenAtomicInsertDenies()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);
        var deadline=DateTimeOffset.UtcNow.AddSeconds(2);
        await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));
        var store=new ProfileCreateStore(new ProfileSetup.Factory(fixture,new ExpireAtInsert(deadline)));
        Assert.Equal("vault_access_denied",(await store.CreateAsync(new(s.Actor,s.Verifier,ProfileSetup.Input(s,client,scoped)),default)).Error);
        await using var check=fixture.CreateContext();Assert.False(await check.Profiles.AnyAsync(x=>x.AccountId==s.InternalId));
    }
    [FunctionalFact]
    public async Task GivenSequenceExhaustion_WhenCreating_ThenConflictAndNoProfile()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);
        await using var db=fixture.CreateContext();var previous=await db.Database.SqlQuery<long>($"SELECT nextval('cerberus.server_sequence') AS \"Value\"").SingleAsync();
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT setval('cerberus.server_sequence',9007199254740991,true)");
            Assert.Equal("revision_conflict",(await Store().CreateAsync(new(s.Actor,s.Verifier,ProfileSetup.Input(s,client,scoped)),default)).Error);
            Assert.False(await db.Profiles.AnyAsync(x=>x.AccountId==s.InternalId));
        }
        finally {await db.Database.ExecuteSqlInterpolatedAsync($"SELECT setval('cerberus.server_sequence',{previous},true)");}
    }
    [FunctionalFact]
    public async Task GivenInternalInvalidInputOrMissingActor_WhenCreating_ThenRejectBeforeDependency()
    {
        using var client=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var input=ProfileSetup.Input(s,client,scoped);
        Assert.Equal("authentication_required",(await Store().CreateAsync(new(Guid.Empty,s.Verifier,input),default)).Error);
        Assert.Equal("validation_failed",(await Store().CreateAsync(new(s.Actor,s.Verifier,input with {ProfileId=Guid.Empty}),default)).Error);
    }
    [FunctionalFact]
    public async Task GivenOtherOwnersPublicId_WhenCreating_ThenConflictWithoutContentDisclosure()
    {
        using var client=new ProtectionFixture();using var other=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await ProfileSetup.Create(fixture,client);var foreign=await ProfileSetup.Create(fixture,other);
        var first=ProfileSetup.Input(foreign,other,scoped);Assert.Null((await Store().CreateAsync(new(foreign.Actor,foreign.Verifier,first),default)).Error);
        var own=ProfileSetup.Input(s,client,scoped);own=own with {ProfileId=first.ProfileId,KeyWrappers=own.KeyWrappers with {MasterKeyWrapper=client.Wrap(s.AccountId,"profile",first.ProfileId,s.Actor)}};
        var result=await Store().CreateAsync(new(s.Actor,s.Verifier,own),default);Assert.Equal("revision_conflict",result.Error);Assert.Null(result.Data);
        await using var db=fixture.CreateContext();var winner=await db.Profiles.SingleAsync(x=>x.PublicId==first.ProfileId);Assert.Equal(foreign.InternalId,winner.AccountId);
    }
    private sealed class ExpireAtInsert(DateTimeOffset deadline):DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {if(command.CommandText.Contains("INSERT INTO cerberus.profile")){var remaining=deadline-DateTimeOffset.UtcNow;if(remaining>TimeSpan.Zero)await Task.Delay(remaining+TimeSpan.FromMilliseconds(100),ct);}return result;}
    }
    private ProfileCreateStore Store()=>new(fixture);
    private sealed class Signal(TaskCompletionSource reached,string locked):DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {if(command.CommandText.Contains("FOR UPDATE")&&command.CommandText.Contains(locked=="account"?"cerberus.account":"cerberus.vault_access_session"))reached.TrySetResult();return ValueTask.FromResult(result);}
    }
    private sealed class FailAfterInsert:DbCommandInterceptor
    {
        private bool inserted;
        public override ValueTask<int> NonQueryExecutedAsync(DbCommand command,CommandExecutedEventData data,int result,CancellationToken ct=default)
        {if(command.CommandText.Contains("INSERT INTO cerberus.profile"))inserted=true;return ValueTask.FromResult(result);}
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {if(inserted)throw new TimeoutException("synthetic post-insert failure");return ValueTask.FromResult(result);}
    }
}

internal static class ProfileSetup
{
    internal sealed record State(Guid Actor,Guid AccountId,long InternalId,string Verifier,byte[] AccountEnvelope);
    internal static EncryptedEnvelope Envelope(long epoch=1)=>new("cerberus-content-v1",epoch,ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32)),ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(12)),"AQID",ProtectionFixture.Encode(new byte[16]));
    internal static async Task<State> Create(PostgresFixture fixture,ProtectionFixture client)
    {
        await using var db=fixture.CreateContext();var a=new Account {PublicId=Guid.NewGuid(),HeimdallPublicId=Guid.NewGuid(),DetailsEnvelope=JsonSerializer.SerializeToUtf8Bytes(Envelope(),ProtectionFixture.Json)};
        db.Accounts.Add(a);await db.SaveChangesAsync();var verifier=Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
        db.VaultProtections.Add(new(){AccountId=a.Id,Material=JsonSerializer.SerializeToUtf8Bytes(client.Material,ProtectionFixture.Json),Revision=1,KeyEpoch=1,RecoveryGeneration=1});
        db.VaultAccessSessions.Add(new(){AccountId=a.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});
        await db.SaveChangesAsync();return new(a.HeimdallPublicId,a.PublicId,a.Id,verifier,a.DetailsEnvelope);
    }
    internal static ProfileCreateInput Input(State s,ProtectionFixture client,ProtectionFixture scoped,string mode="Master")
    {
        var id=Guid.NewGuid();return new(id,Envelope(),new(mode,scoped.Material.UnlockVerifier,client.Wrap(s.AccountId,"profile",id,s.Actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null),DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),[],[],[]);
    }
    internal sealed class Factory:IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options;
        internal Factory(DbContextOptions<AppDbContext> value)=>options=value;
        internal Factory(PostgresFixture fixture,IInterceptor interceptor)
        {using var db=fixture.CreateContext();options=new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(interceptor).Options;}
        public AppDbContext CreateDbContext()=>new(options);
    }
}
