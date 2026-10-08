using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class VaultProtectionChangeStoreTests(PostgresFixture fixture)
{
    [FunctionalTheory]
    [InlineData("rewrap")][InlineData("rotate-content")]
    public async Task GivenCompleteCurrentProofAndAccess_WhenChanging_ThenCommitAllAndInvalidateOldAccess(string mode)
    {
        using var client=new ProtectionFixture();var s=await Setup(client);var c=Change(s,client,mode);var request=await Request(s,client,c);
        var before=await Snapshot(s);var result=await Store().ChangeAsync(request,default);
        Assert.Null(result.Error);Assert.Equal(new ProtectionChangeDetails(s.Id,2,2,1,mode=="rewrap"?3:4),result.Data);
        await using var db=fixture.CreateContext();var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var p=await db.VaultProtections.SingleAsync(x=>x.AccountId==s.InternalId);
        Assert.Equal(2,a.RevocationGeneration);Assert.Equal(1,a.PolicyRevision);Assert.Equal(2,p.Revision);Assert.Equal(2,p.KeyEpoch);Assert.Equal(1,p.RecoveryGeneration);
        Assert.Equal(c.Material,JsonSerializer.Deserialize<ProtectionMaterial>(p.Material,ProtectionFixture.Json));
        Assert.Equal(mode=="rewrap"?s.Envelope:JsonSerializer.SerializeToUtf8Bytes(c.ContentReplacements[0].Envelope,ProtectionFixture.Json),a.DetailsEnvelope);
        Assert.True((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==request.ChallengeId)).Consumed);
        Assert.Equal(1,(await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier)).RevocationGeneration);
        Assert.Equal("revision_conflict",(await Store().ChangeAsync(request,default)).Error);Assert.NotEqual(before,await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("missing","not_found")][InlineData("foreign","not_found")][InlineData("closing","not_found")][InlineData("erased","not_found")]
    [InlineData("noProtection","not_found")][InlineData("staleProtection","revision_conflict")][InlineData("staleAccount","revision_conflict")]
    [InlineData("revoked","vault_access_denied")][InlineData("profile","vault_access_denied")][InlineData("expired","vault_access_denied")]
    [InlineData("future","vault_access_denied")][InlineData("policy","vault_access_denied")][InlineData("generation","vault_access_denied")]
    [InlineData("missingAccess","vault_access_denied")][InlineData("noExpiry",null)][InlineData("badNullExpiry","vault_access_denied")]
    [InlineData("overflow","revision_conflict")][InlineData("corruptMaterial","persistence_unavailable")]
    public async Task GivenInvalidOwnerStateOrPermission_WhenChanging_ThenPreserveCompleteState(string state,string? error)
    {
        using var client=new ProtectionFixture();var s=await Setup(client);var c=Change(s,client);var request=await Request(s,client,c);
        await using(var db=fixture.CreateContext())
        {
            var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var v=await db.VaultAccessSessions.SingleAsync(x=>x.HandleVerifier==s.Verifier);
            if(state=="closing")a.State=AccountState.ClosurePending;
            if(state=="erased")db.TerminalErasures.Add(new TerminalErasure { ResourceId=s.Id,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow });
            if(state=="noProtection")await db.VaultProtections.Where(x=>x.AccountId==s.InternalId).ExecuteDeleteAsync();
            if(state=="corruptMaterial")await db.VaultProtections.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(p=>p.Material,new byte[]{1,2,3}));
            if(state=="overflow")a.RevocationGeneration=9007199254740991;
            if(state=="revoked")v.Revoked=true;if(state=="profile")v.ProfileId=123;
            if(state=="expired")v.ExpiresAt=DateTimeOffset.UtcNow.AddMinutes(-1);if(state=="future")v.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);
            if(state=="policy")v.PolicyRevision=2;if(state=="generation")v.RevocationGeneration=2;
            if(state=="noExpiry"){a.RenewalEnabled=false;v.ExpiresAt=null;}if(state=="badNullExpiry")v.ExpiresAt=null;
            await db.SaveChangesAsync();
        }
        if(state=="missing")request=request with { Actor=Guid.NewGuid() };
        if(state=="foreign")request=request with { Change=c with { AccountId=Guid.NewGuid() } };
        if(state=="staleProtection")request=request with { Change=c with { ExpectedProtectionRevision=2 } };
        if(state=="staleAccount")request=request with { Change=c with { ExpectedAccountRevision=2 } };
        if(state=="missingAccess")request=request with { AccessVerifier=new string('a',64) };
        var before=await Snapshot(s);var result=await Store().ChangeAsync(request,default);Assert.Equal(error,result.Error);
        if(error is not null){Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));await Unconsumed(request.ChallengeId);}
        else Assert.Equal(2,result.Data!.ProtectionRevision);
    }

    [FunctionalTheory]
    [InlineData("missingContent")][InlineData("duplicateContent")][InlineData("foreignContent")][InlineData("wrongEpoch")]
    [InlineData("keySubstitution")][InlineData("generation")][InlineData("saltReuse")]
    public async Task GivenIncompleteRotationOrUnauthorizedKeyChange_WhenChanging_ThenRejectWithoutConsumption(string invalid)
    {
        using var client=new ProtectionFixture();using var other=new ProtectionFixture();var s=await Setup(client);var c=Change(s,client,"rotate-content");var item=c.ContentReplacements[0];
        c=invalid switch
        {
            "missingContent"=>c with { ContentReplacements=[] },"duplicateContent"=>c with { ContentReplacements=[item,item] },
            "foreignContent"=>c with { ContentReplacements=[item with { ResourceId=Guid.NewGuid() }] },
            "wrongEpoch"=>c with { ContentReplacements=[item with { Envelope=item.Envelope with { KeyEpoch=5 } }] },
            "keySubstitution"=>c with { Material=c.Material with { AuthorKey=other.Material.AuthorKey } },
            "generation"=>c with { Material=c.Material with { RecoveryWrapper=c.Material.RecoveryWrapper with { Generation=2 } } },
            _=>c with { Material=c.Material with { PasswordWrapper=c.Material.PasswordWrapper with { Nonce=client.Material.PasswordWrapper.Nonce } } }
        };
        var request=await Request(s,client,c);var before=await Snapshot(s);
        Assert.Equal("validation_failed",(await Store().ChangeAsync(request,default)).Error);Assert.Equal(before,await Snapshot(s));await Unconsumed(request.ChallengeId);
    }

    [FunctionalTheory]
    [InlineData("wrongKey","vault_proof_rejected")][InlineData("body","vault_proof_rejected")][InlineData("purpose","vault_proof_rejected")]
    [InlineData("consumed","vault_proof_rejected")][InlineData("expired","vault_proof_rejected")][InlineData("missing","vault_proof_rejected")]
    [InlineData("staleChallenge","revision_conflict")]
    public async Task GivenUnboundOrStaleProof_WhenChanging_ThenNoPartialWrites(string invalid,string error)
    {
        using var client=new ProtectionFixture();using var other=new ProtectionFixture();var s=await Setup(client);var request=await Request(s,client,Change(s,client),invalid=="purpose"?"unlock-account":"change-protection");
        await using(var db=fixture.CreateContext())
        {
            var row=await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==request.ChallengeId);
            if(invalid=="consumed")row.Consumed=true;
            if(invalid=="staleChallenge")row.PolicyRevision=2;
            if(invalid=="expired")
            {
                var challenge=JsonSerializer.Deserialize<VaultProofChallenge>(row.Challenge,ProtectionFixture.Json)!;
                challenge=challenge with { IssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-60,ExpiresAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                row.Challenge=JsonSerializer.SerializeToUtf8Bytes(challenge,ProtectionFixture.Json);row.ExpiresAt=DateTimeOffset.FromUnixTimeSeconds(challenge.ExpiresAt);request=request with { Proof=client.Sign(challenge) };
            }
            await db.SaveChangesAsync();
        }
        if(invalid=="wrongKey")request=request with { Proof=other.Sign((await Challenge(request.ChallengeId))) };
        if(invalid=="body")request=request with { RawBody=Encoding.UTF8.GetBytes(" "+Encoding.UTF8.GetString(request.RawBody)) };
        if(invalid=="missing")request=request with { ChallengeId=Guid.NewGuid() };
        var before=await Snapshot(s);Assert.Equal(error,(await Store().ChangeAsync(request,default)).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenChangePurposeChallenge_WhenUnlocking_ThenNeverIssueSession()
    {
        using var client=new ProtectionFixture();var s=await Setup(client);var raw=Encoding.UTF8.GetBytes("{\"expectedProtectionRevision\":1}");
        var c=(await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor,"change-protection",ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;
        var result=await new VaultProtectionStore(fixture).UnlockAsync(s.Actor,1,c.ChallengeId,client.Sign(c),raw,default);
        Assert.Equal("vault_proof_rejected",result.Error);await Unconsumed(c.ChallengeId);
        await using var db=fixture.CreateContext();Assert.Single(await db.VaultAccessSessions.Where(x=>x.AccountId==s.InternalId).ToListAsync());
    }

    [FunctionalFact]
    public async Task GivenConcurrentProtectionChanges_WhenCommitting_ThenOneCompleteWinner()
    {
        using var client=new ProtectionFixture();var s=await Setup(client);var a=await Request(s,client,Change(s,client));var b=await Request(s,client,Change(s,client));
        var results=await Task.WhenAll(Store().ChangeAsync(a,default),Store().ChangeAsync(b,default));
        Assert.Single(results,x=>x.Error is null);Assert.Equal("revision_conflict",Assert.Single(results,x=>x.Error is not null).Error);
        await using var db=fixture.CreateContext();var p=await db.VaultProtections.SingleAsync(x=>x.AccountId==s.InternalId);
        var winner=results[0].Error is null?a:b;Assert.Equal(winner.Change.Material,JsonSerializer.Deserialize<ProtectionMaterial>(p.Material,ProtectionFixture.Json));
        Assert.Equal(1,await db.VaultUnlockChallenges.CountAsync(x=>x.AccountId==s.InternalId&&x.Consumed));
    }

    [FunctionalTheory]
    [InlineData("account","session")][InlineData("session","session")][InlineData("account","challenge")]
    public async Task GivenUnchangedDeadlineExpiringDuringLockWait_WhenChanging_ThenWriteNothing(string locked,string expiry)
    {
        using var client=new ProtectionFixture();var s=await Setup(client);var request=await Request(s,client,Change(s,client));
        var deadline=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+2);
        await using(var db=fixture.CreateContext())
        {
            if(expiry=="session")await db.VaultAccessSessions.Where(x=>x.HandleVerifier==s.Verifier).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,deadline));
            else
            {
                var row=await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==request.ChallengeId);var c=JsonSerializer.Deserialize<VaultProofChallenge>(row.Challenge,ProtectionFixture.Json)! with { IssuedAt=deadline.ToUnixTimeSeconds()-60,ExpiresAt=deadline.ToUnixTimeSeconds() };
                row.Challenge=JsonSerializer.SerializeToUtf8Bytes(c,ProtectionFixture.Json);row.ExpiresAt=deadline;await db.SaveChangesAsync();request=request with { Proof=client.Sign(c) };
            }
        }
        var before=await Snapshot(s);await using var holder=fixture.CreateContext();await using var tx=await holder.Database.BeginTransactionAsync();
        if(locked=="account")await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
        else await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE handle_verifier={s.Verifier} FOR UPDATE");
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var store=new VaultProtectionChangeStore(new Factory(fixture,new LockSignal(reached,locked)));
        var pending=store.ChangeAsync(request,default);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);
        var remaining=deadline-DateTimeOffset.UtcNow;if(remaining>TimeSpan.Zero)await Task.Delay(remaining+TimeSpan.FromMilliseconds(100));
        await tx.CommitAsync();var result=await pending;Assert.Equal(expiry=="session"?"vault_access_denied":"vault_proof_rejected",result.Error);
        Assert.Equal(before,await Snapshot(s));await Unconsumed(request.ChallengeId);
    }

    [FunctionalTheory]
    [InlineData("closing","not_found")][InlineData("erased","not_found")][InlineData("policy","vault_access_denied")][InlineData("accountEdit","revision_conflict")]
    public async Task GivenLifecycleOrRevisionChangesWhileQueued_WhenChanging_ThenRecheckAfterAccountLock(string state,string error)
    {
        using var client=new ProtectionFixture();var s=await Setup(client);var request=await Request(s,client,Change(s,client));
        await using var holder=fixture.CreateContext();await using var tx=await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var pending=new VaultProtectionChangeStore(new Factory(fixture,new LockSignal(reached,"account"))).ChangeAsync(request,default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);var a=await holder.Accounts.SingleAsync(x=>x.Id==s.InternalId);
        if(state=="closing")a.State=AccountState.ClosurePending;if(state=="policy")a.PolicyRevision++;
        if(state=="accountEdit"){a.Revision++;a.DetailsEnvelope=[7,8,9];}
        if(state=="erased")holder.TerminalErasures.Add(new TerminalErasure { ResourceId=s.Id,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow });
        await holder.SaveChangesAsync();await tx.CommitAsync();var before=await Snapshot(s);
        Assert.Equal(error,(await pending).Error);Assert.Equal(before,await Snapshot(s));await Unconsumed(request.ChallengeId);
    }

    [FunctionalFact]
    public async Task GivenPostConsumptionSaveFailure_WhenChanging_ThenRollbackEveryWriteAndPermitSameProofRetry()
    {
        using var client=new ProtectionFixture();var s=await Setup(client);var request=await Request(s,client,Change(s,client,"rotate-content"));var before=await Snapshot(s);
        var result=await new VaultProtectionChangeStore(new Factory(fixture,new RejectWrite())).ChangeAsync(request,default);
        Assert.Equal("persistence_unavailable",result.Error);Assert.Equal(before,await Snapshot(s));await Unconsumed(request.ChallengeId);
        Assert.Null((await Store().ChangeAsync(request,default)).Error);
    }

    [FunctionalFact]
    public async Task GivenUnavailableDatabaseOrCancelledCaller_WhenChanging_ThenFailClosed()
    {
        using var client=new ProtectionFixture();var s=await Setup(client);var r=await Request(s,client,Change(s,client));var before=await Snapshot(s);
        var unavailable=new VaultProtectionChangeStore(new Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=unavailable;Username=fixture;Password=fixture;Timeout=1").Options));
        Assert.Equal("persistence_unavailable",(await unavailable.ChangeAsync(r,default)).Error);
        using var source=new CancellationTokenSource();source.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().ChangeAsync(r,source.Token));
        Assert.Equal(before,await Snapshot(s));await Unconsumed(r.ChallengeId);
    }

    private VaultProtectionChangeStore Store()=>new(fixture);
    private sealed record State(Guid Actor,Guid Id,long InternalId,string Verifier,byte[] Envelope);
    private async Task<State> Setup(ProtectionFixture client)
    {
        var actor=Guid.NewGuid();var id=Guid.NewGuid();var e=new EncryptedEnvelope("cerberus-content-v1",5,ProtectionFixture.Encode(new byte[32]),ProtectionFixture.Encode(new byte[12]),"AQID",ProtectionFixture.Encode(new byte[16]));
        var raw=JsonSerializer.SerializeToUtf8Bytes(e,ProtectionFixture.Json);await using var db=fixture.CreateContext();
        var a=new Account { PublicId=id,HeimdallPublicId=actor,DetailsEnvelope=raw,Revision=3 };db.Accounts.Add(a);await db.SaveChangesAsync();
        OpaqueAccessHandle.TryHash(ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32)),out var verifier);
        db.VaultProtections.Add(new VaultProtection { AccountId=a.Id,Material=JsonSerializer.SerializeToUtf8Bytes(client.Material,ProtectionFixture.Json) });
        db.VaultAccessSessions.Add(new VaultAccessSession { AccountId=a.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1 });
        await db.SaveChangesAsync();return new(actor,id,a.Id,verifier,raw);
    }
    private static ProtectionChange Change(State s,ProtectionFixture client,string mode="rewrap")=>new(s.Id,1,3,mode,client.Rewrap(),mode=="rewrap"?[]:
        [new("account",s.Id,3,new("cerberus-content-v1",6,ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32)),ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(12)),"BAUG",ProtectionFixture.Encode(new byte[16])))]);
    private async Task<ProtectionChangeRequest> Request(State s,ProtectionFixture client,ProtectionChange change,string operation="change-protection")
    {
        var raw=JsonSerializer.SerializeToUtf8Bytes(change,ProtectionFixture.Json);var c=(await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor,operation,ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;
        return new(s.Actor,s.Verifier,c.ChallengeId,client.Sign(c),raw,change);
    }
    private async Task<VaultProofChallenge> Challenge(Guid id)
    { await using var db=fixture.CreateContext();return JsonSerializer.Deserialize<VaultProofChallenge>((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==id)).Challenge,ProtectionFixture.Json)!; }
    private async Task Unconsumed(Guid id)
    { await using var db=fixture.CreateContext();Assert.False((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==id)).Consumed); }
    private async Task<string> Snapshot(State s)
    {
        await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new { Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).ToListAsync() });
    }
    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options;
        public Factory(DbContextOptions<AppDbContext> value)=>options=value;
        public Factory(PostgresFixture f,IInterceptor interceptor)
        {using var db=f.CreateContext();options=new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(interceptor).Options;}
        public AppDbContext CreateDbContext()=>new(options);
    }
    private sealed class LockSignal(TaskCompletionSource reached,string locked) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken ct=default)
        {if(command.CommandText.Contains("FOR UPDATE",StringComparison.Ordinal)&&command.CommandText.Contains(locked=="account"?"cerberus.account":"cerberus.vault_access_session",StringComparison.Ordinal))reached.TrySetResult();return ValueTask.FromResult(result);}
    }
    private sealed class RejectWrite : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken ct=default)
        {if(command.CommandText.Contains("UPDATE cerberus.vault_protection",StringComparison.Ordinal))throw new TimeoutException("synthetic protection write failure");return ValueTask.FromResult(result);}
    }
}
