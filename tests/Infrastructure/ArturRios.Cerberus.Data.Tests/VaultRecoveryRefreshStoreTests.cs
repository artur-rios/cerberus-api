using System.Data.Common;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Data.Protection;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class VaultRecoveryRefreshStoreTests(PostgresFixture fixture)
{
    [FunctionalFact]
    public async Task GivenFreshOwnerWithCurrentAccountSession_WhenRecovering_ThenReplaceCredentialAndPreserveContent()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);var r=await Request(s,old,Replacement(old,next));
        var result=await Store().RecoverAsync(r,default);Assert.Equal(new RecoveryDetails("committed",2,2,2),result.Data);Assert.Null(result.Error);
        await using var db=fixture.CreateContext();var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var p=await db.VaultProtections.SingleAsync(x=>x.AccountId==s.InternalId);
        Assert.Equal(s.Envelope,a.DetailsEnvelope);Assert.Equal(3,a.Revision);Assert.Equal(2,a.RevocationGeneration);Assert.Equal(1,a.PolicyRevision);
        Assert.Equal(2,p.Revision);Assert.Equal(2,p.KeyEpoch);Assert.Equal(2,p.RecoveryGeneration);Assert.Equal(r.Replacement.Replace(old.Material),JsonSerializer.Deserialize<ProtectionMaterial>(p.Material,ProtectionFixture.Json));
        Assert.True((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==r.ChallengeId)).Consumed);Assert.Single(await db.Set<VaultRecoveryOperation>().Where(x=>x.AccountId==s.InternalId).ToListAsync());
        Assert.Equal(1,Assert.Single(await db.VaultAccessSessions.Where(x=>x.AccountId==s.InternalId).ToListAsync()).RevocationGeneration);
    }

    [FunctionalFact]
    public async Task GivenCommittedRecovery_WhenRetryingAfterExpiryWithMalleatedProof_ThenReturnDurableHistoricalOutcome()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();using var third=new ProtectionFixture();var s=await Setup(old);var r=await Request(s,old,Replacement(old,next));
        var first=(await Store().RecoverAsync(r,default)).Data!;Assert.NotNull(first);
        await using(var db=fixture.CreateContext())
        {
            var row=await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==r.ChallengeId);var c=JsonSerializer.Deserialize<VaultProofChallenge>(row.Challenge,ProtectionFixture.Json)!;
            c=c with { IssuedAt=1,ExpiresAt=61 };row.Challenge=JsonSerializer.SerializeToUtf8Bytes(c,ProtectionFixture.Json);row.ExpiresAt=DateTimeOffset.FromUnixTimeSeconds(61);await db.SaveChangesAsync();
        }
        s=await CurrentAccess(s,old,2);var second=await Request(s,old,Replacement(old,third,3,3,2));var beforeSecond=await Snapshot(s);
        Assert.Equal("vault_proof_rejected",(await Store().RecoverAsync(second with { Proof=old.SignRecovery(await Challenge(second.ChallengeId)) },default)).Error);Assert.Equal(beforeSecond,await Snapshot(s));
        Assert.Null((await Store().RecoverAsync(second,default)).Error);
        await using(var db=fixture.CreateContext())Assert.False(await db.VaultUnlockChallenges.AnyAsync(x=>x.PublicId==r.ChallengeId));
        var before=await Snapshot(s);var replay=await new VaultRecoveryStore(fixture).RecoverAsync(r with { Proof=Malleate(r.Proof),IdentityIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds() },default);
        Assert.Equal(first,replay.Data);Assert.Null(replay.Error);Assert.Equal(before,await Snapshot(s));Assert.Equal(2,replay.Data!.Generation);
    }

    [FunctionalFact]
    public async Task GivenRefreshedCredential_WhenOldRecoveryThenNewRecoveryAttempted_ThenOnlyNewGenerationCanCommit()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();using var third=new ProtectionFixture();var s=await Setup(old);
        var oldInput=Replacement(old,third) with { Operation="recover" };var oldRequest=await Request(s,old,oldInput,"recover");
        var refresh=await Request(s,old,Replacement(old,next));Assert.Null((await Store().RecoverAsync(refresh,default)).Error);var before=await Snapshot(s);
        Assert.Equal("recovery_credential_consumed",(await Store().RecoverAsync(oldRequest,default)).Error);Assert.Equal(before,await Snapshot(s));
        var replacement=Replacement(old,third,3,3,2) with { Operation="recover" };var recovery=await Request(s,next,replacement,"recover");
        Assert.Equal(new RecoveryDetails("committed",3,3,3),(await Store().RecoverAsync(recovery,default)).Data);
        await using var db=fixture.CreateContext();var account=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);Assert.Equal(s.Envelope,account.DetailsEnvelope);Assert.Equal(3,account.Revision);
    }
    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenSessionExpiringDuringAccountOrSessionLockWait_WhenRefreshing_ThenRejectWithoutConsumption(bool sessionLock)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);var r=await Request(s,old,Replacement(old,next));var deadline=DateTimeOffset.UtcNow.AddSeconds(2);
        await using(var db=fixture.CreateContext())await db.VaultAccessSessions.Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(y=>y.ExpiresAt,deadline));var before=await Snapshot(s);
        await using var holder=fixture.CreateContext();await using var tx=await holder.Database.BeginTransactionAsync();
        if(sessionLock)await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE account_id={s.InternalId} FOR UPDATE");
        else await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var pending=new VaultRecoveryStore(new Factory(fixture,new LockSignal(reached,sessionLock?"vault_access_session":null))).RecoverAsync(r,default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);var delay=deadline-DateTimeOffset.UtcNow;if(delay>TimeSpan.Zero)await Task.Delay(delay+TimeSpan.FromMilliseconds(100));
        await tx.CommitAsync();Assert.Equal("vault_access_denied",(await pending).Error);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenCommittedRefreshKey_WhenReusedForRecoverPurpose_ThenDigestConflictWithoutTransition()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);var r=await Request(s,old,Replacement(old,next));Assert.Null((await Store().RecoverAsync(r,default)).Error);
        var recover=r.Replacement with { Operation="recover" };var before=await Snapshot(s);
        var result=await Store().RecoverAsync(r with { Replacement=recover,RawBody=JsonSerializer.SerializeToUtf8Bytes(recover,ProtectionFixture.Json) },default);
        Assert.Equal("revision_conflict",result.Error);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory]
    [InlineData(false)][InlineData(true)]
    public async Task GivenConcurrentRecoveryRequests_WhenCommitting_ThenOnlyOneGenerationTransition(bool exactRetry)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);var a=await Request(s,old,Replacement(old,next));var b=exactRetry?a:await Request(s,old,Replacement(old,next));
        var results=await Task.WhenAll(Store().RecoverAsync(a,default),Store().RecoverAsync(b,default));
        if(exactRetry){Assert.All(results,x=>Assert.Null(x.Error));Assert.Equal(results[0].Data,results[1].Data);}
        else{Assert.Single(results,x=>x.Error is null);Assert.Equal("revision_conflict",Assert.Single(results,x=>x.Error is not null).Error);}
        await using var db=fixture.CreateContext();Assert.Equal(2,(await db.VaultProtections.SingleAsync(x=>x.AccountId==s.InternalId)).RecoveryGeneration);
        Assert.Equal(1,await db.Set<VaultRecoveryOperation>().CountAsync(x=>x.AccountId==s.InternalId));Assert.Equal(1,await db.VaultUnlockChallenges.CountAsync(x=>x.AccountId==s.InternalId&&x.Consumed));
    }

    [FunctionalTheory]
    [InlineData("changedBytes","revision_conflict")][InlineData("changedPayload","revision_conflict")][InlineData("actor","not_found")]
    [InlineData("staleAuth","fresh_authentication_required")][InlineData("corruptResult","persistence_unavailable")]
    public async Task GivenStoredRecoveryResult_WhenRetryBindingOrCurrentAuthenticationWrong_ThenRejectUnchanged(string invalid,string error)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);var r=await Request(s,old,Replacement(old,next));Assert.Null((await Store().RecoverAsync(r,default)).Error);
        if(invalid=="changedBytes")r=r with { RawBody=[..r.RawBody,(byte)' '] };
        if(invalid=="changedPayload")r=r with { Replacement=r.Replacement with { PasswordWrapper=r.Replacement.PasswordWrapper with { Ciphertext="CgsM" } },RawBody=Encoding.UTF8.GetBytes("{\"changed\":true}") };
        if(invalid=="actor")r=r with { Actor=Guid.NewGuid() };if(invalid=="staleAuth")r=r with { IdentityIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-60 };
        if(invalid=="corruptResult"){await using var db=fixture.CreateContext();await db.Set<VaultRecoveryOperation>().Where(x=>x.AccountId==s.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(o=>o.Generation,0));}
        var before=await Snapshot(s);var result=await Store().RecoverAsync(r,default);Assert.Equal(error,result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("closing","not_found")][InlineData("erased","not_found")][InlineData("missingProtection","not_found")]
    [InlineData("oldAuth","fresh_authentication_required")][InlineData("futureAuth","fresh_authentication_required")]
    [InlineData("revision","revision_conflict")][InlineData("policy","vault_access_denied")][InlineData("challengePolicy","revision_conflict")][InlineData("consumed","vault_proof_rejected")]
    [InlineData("wrongKey","vault_proof_rejected")][InlineData("newKey","vault_proof_rejected")][InlineData("purpose","vault_proof_rejected")]
    [InlineData("body","vault_proof_rejected")][InlineData("parsedPayload","vault_proof_rejected")]
    [InlineData("generation","validation_failed")][InlineData("reusedKey","validation_failed")][InlineData("salt","validation_failed")]
    [InlineData("overflow","revision_conflict")][InlineData("expired","vault_proof_rejected")]
    [InlineData("missingSession","vault_access_denied")][InlineData("foreignSession","vault_access_denied")]
    [InlineData("revokedSession","vault_access_denied")][InlineData("profileSession","vault_access_denied")]
    [InlineData("expiredSession","vault_access_denied")][InlineData("futureSession","vault_access_denied")]
    [InlineData("sessionPolicy","vault_access_denied")][InlineData("sessionGeneration","vault_access_denied")]
    [InlineData("enabledNullExpiry","vault_access_denied")][InlineData("disabledExpiry","vault_access_denied")]
    public async Task GivenInvalidRecoveryStateOrProof_WhenRecovering_ThenPreserveEveryWrite(string invalid,string error)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);var p=Replacement(old,next);
        if(invalid=="revision")p=p with { ExpectedRevision=2 };
        if(invalid=="generation")p=p with { RecoveryWrapper=p.RecoveryWrapper with { Generation=3 } };
        if(invalid=="reusedKey")p=p with { NewRecoveryVerifier=old.Material.UnlockVerifier,RecoveryWrapper=p.RecoveryWrapper with { ProofKeyFingerprint=old.Material.UnlockVerifier.Fingerprint() } };
        if(invalid=="salt")p=p with { PasswordWrapper=p.PasswordWrapper with { Nonce=old.Material.PasswordWrapper.Nonce } };
        var r=await Request(s,old,p,invalid=="purpose"?"change-protection":"refresh-recovery");
        await using(var db=fixture.CreateContext())
        {
            var a=await db.Accounts.SingleAsync(x=>x.Id==s.InternalId);var c=await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==r.ChallengeId);
            if(invalid=="closing")a.State=AccountState.ClosurePending;if(invalid=="erased")db.TerminalErasures.Add(new TerminalErasure { ResourceId=s.Id,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow });
            if(invalid=="missingProtection")await db.VaultProtections.Where(x=>x.AccountId==a.Id).ExecuteDeleteAsync();
            if(invalid=="policy")a.PolicyRevision++;if(invalid=="challengePolicy")c.PolicyRevision++;if(invalid=="consumed")c.Consumed=true;if(invalid=="overflow")a.RevocationGeneration=9007199254740991;
            if(invalid=="expired")
            {
                var issued=JsonSerializer.Deserialize<VaultProofChallenge>(c.Challenge,ProtectionFixture.Json)! with { IssuedAt=1,ExpiresAt=61 };c.Challenge=JsonSerializer.SerializeToUtf8Bytes(issued,ProtectionFixture.Json);c.ExpiresAt=DateTimeOffset.FromUnixTimeSeconds(61);r=r with { Proof=old.Sign(issued) };
            }
            var session=await db.VaultAccessSessions.SingleAsync(x=>x.AccountId==a.Id);
            if(invalid=="missingSession")db.VaultAccessSessions.Remove(session);if(invalid=="revokedSession")session.Revoked=true;
            if(invalid=="profileSession")session.ProfileId=123;if(invalid=="expiredSession")session.ExpiresAt=DateTimeOffset.UtcNow.AddSeconds(-1);
            if(invalid=="futureSession")session.IssuedAt=DateTimeOffset.UtcNow.AddMinutes(1);if(invalid=="sessionPolicy")session.PolicyRevision++;
            if(invalid=="sessionGeneration")session.RevocationGeneration++;if(invalid=="enabledNullExpiry")session.ExpiresAt=null;if(invalid=="disabledExpiry")a.RenewalEnabled=false;
            await db.SaveChangesAsync();
        }
        if(invalid=="foreignSession")r=r with { AccessVerifier=new string('a',64) };
        if(invalid=="oldAuth")r=r with { IdentityIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()-60 };if(invalid=="futureAuth")r=r with { IdentityIssuedAt=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+60 };
        if(invalid=="wrongKey")r=r with { Proof=old.SignRecovery(await Challenge(r.ChallengeId)) };if(invalid=="newKey")r=r with { Proof=next.SignRecovery(await Challenge(r.ChallengeId)) };
        if(invalid=="body")r=r with { RawBody=Encoding.UTF8.GetBytes(" "+Encoding.UTF8.GetString(r.RawBody)) };
        if(invalid=="parsedPayload")r=r with { Replacement=r.Replacement with { PasswordWrapper=r.Replacement.PasswordWrapper with { Ciphertext="CgsM" } } };
        var before=await Snapshot(s);var result=await Store().RecoverAsync(r,default);Assert.Equal(error,result.Error);Assert.Null(result.Data);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("challenge","vault_proof_rejected")][InlineData("identity","fresh_authentication_required")]
    public async Task GivenNaturalDeadlineExpiringWhileAccountLocked_WhenRecovering_ThenRejectWithoutConsumption(string expires,string error)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);var r=await Request(s,old,Replacement(old,next));
        var deadline=DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds()+2);
        if(expires=="identity")r=r with { IdentityIssuedAt=deadline.ToUnixTimeSeconds()-60 };
        else
        {
            await using var db=fixture.CreateContext();var row=await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==r.ChallengeId);
            var c=JsonSerializer.Deserialize<VaultProofChallenge>(row.Challenge,ProtectionFixture.Json)! with { IssuedAt=deadline.ToUnixTimeSeconds()-60,ExpiresAt=deadline.ToUnixTimeSeconds() };
            row.Challenge=JsonSerializer.SerializeToUtf8Bytes(c,ProtectionFixture.Json);row.ExpiresAt=deadline;await db.SaveChangesAsync();r=r with { Proof=old.Sign(c) };
        }
        var before=await Snapshot(s);await using var holder=fixture.CreateContext();await using var tx=await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var pending=new VaultRecoveryStore(new Factory(fixture,new LockSignal(reached))).RecoverAsync(r,default);
        await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);var delay=deadline-DateTimeOffset.UtcNow;if(delay>TimeSpan.Zero)await Task.Delay(delay+TimeSpan.FromMilliseconds(100));
        await tx.CommitAsync();Assert.Equal(error,(await pending).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("closing","not_found")][InlineData("erased","not_found")][InlineData("policy","vault_access_denied")]
    public async Task GivenLifecycleChangingWhileQueued_WhenRecovering_ThenUseCurrentLockedState(string state,string error)
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);var r=await Request(s,old,Replacement(old,next));
        await using var holder=fixture.CreateContext();await using var tx=await holder.Database.BeginTransactionAsync();await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
        var reached=new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);var pending=new VaultRecoveryStore(new Factory(fixture,new LockSignal(reached))).RecoverAsync(r,default);await reached.Task.WaitAsync(TimeSpan.FromSeconds(10));Assert.False(pending.IsCompleted);
        var a=await holder.Accounts.SingleAsync(x=>x.Id==s.InternalId);if(state=="closing")a.State=AccountState.ClosurePending;if(state=="policy")a.PolicyRevision++;
        if(state=="erased")holder.TerminalErasures.Add(new TerminalErasure { ResourceId=s.Id,ResourceKind="account",DeletedAt=DateTimeOffset.UtcNow });await holder.SaveChangesAsync();await tx.CommitAsync();var before=await Snapshot(s);
        Assert.Equal(error,(await pending).Error);Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenFailureAfterConsumption_WhenRecovering_ThenRollbackAndAllowIdenticalProofRetry()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);var r=await Request(s,old,Replacement(old,next));var before=await Snapshot(s);
        Assert.Equal("persistence_unavailable",(await new VaultRecoveryStore(new Factory(fixture,new RejectWrite())).RecoverAsync(r,default)).Error);
        Assert.Equal(before,await Snapshot(s));Assert.Null((await Store().RecoverAsync(r,default)).Error);
    }

    [FunctionalFact]
    public async Task GivenUnavailableDatabaseOrCancelledCaller_WhenRecovering_ThenFailClosed()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);var r=await Request(s,old,Replacement(old,next));var before=await Snapshot(s);
        var down=new VaultRecoveryStore(new Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=unavailable;Username=fixture;Password=fixture;Timeout=1").Options));
        Assert.Equal("persistence_unavailable",(await down.RecoverAsync(r,default)).Error);using var cancel=new CancellationTokenSource();cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Store().RecoverAsync(r,cancel.Token));Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenRecoveredAccount_WhenDeleting_ThenCascadeNonsecretOutcome()
    {
        using var old=new ProtectionFixture();using var next=new ProtectionFixture();var s=await Setup(old);var r=await Request(s,old,Replacement(old,next));Assert.Null((await Store().RecoverAsync(r,default)).Error);
        await using var db=fixture.CreateContext();await db.Accounts.Where(x=>x.Id==s.InternalId).ExecuteDeleteAsync();Assert.False(await db.Set<VaultRecoveryOperation>().AnyAsync(x=>x.AccountId==s.InternalId));
    }

    private VaultRecoveryStore Store()=>new(fixture);
    private sealed record State(Guid Actor,Guid Id,long InternalId,byte[] Envelope,string Verifier);
    private async Task<State> Setup(ProtectionFixture old)
    {
        var actor=Guid.NewGuid();var id=Guid.NewGuid();byte[] envelope=[1,2,3];await using var db=fixture.CreateContext();var a=new Account { PublicId=id,HeimdallPublicId=actor,DetailsEnvelope=envelope,Revision=3 };
        db.Accounts.Add(a);await db.SaveChangesAsync();db.VaultProtections.Add(new VaultProtection { AccountId=a.Id,Material=JsonSerializer.SerializeToUtf8Bytes(old.Material,ProtectionFixture.Json) });await db.SaveChangesAsync();var access=ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(access,out var verifier);db.VaultAccessSessions.Add(new VaultAccessSession { AccountId=a.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddSeconds(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1 });await db.SaveChangesAsync();return new(actor,id,a.Id,envelope,verifier);
    }
    private static RecoveryReplacement Replacement(ProtectionFixture old,ProtectionFixture next,long epoch=2,long generation=2,long revision=1)
    {var m=old.Rewrap(epoch);return new("refresh-recovery",Guid.NewGuid(),revision,m.PasswordWrapper,m.RecoveryWrapper with { Generation=generation,ProofKeyFingerprint=next.Material.RecoveryVerifier.Fingerprint() },next.Material.RecoveryVerifier);}
    private async Task<RecoveryRequest> Request(State s,ProtectionFixture key,RecoveryReplacement input,string operation="refresh-recovery")
    {var raw=JsonSerializer.SerializeToUtf8Bytes(input,ProtectionFixture.Json);var issued=await new VaultProtectionStore(fixture).ChallengeAsync(s.Actor,operation,ProtocolBinary.Encode(SHA256.HashData(raw)),default);Assert.Null(issued.Error);var c=issued.Data!;return new(s.Actor,DateTimeOffset.UtcNow.ToUnixTimeSeconds(),c.ChallengeId,operation=="recover"?key.SignRecovery(c):key.Sign(c),raw,input,s.Verifier);}
    private async Task<State> CurrentAccess(State s,ProtectionFixture key,long revision)
    {
        var raw=JsonSerializer.SerializeToUtf8Bytes(new { expectedProtectionRevision=revision },ProtectionFixture.Json);var store=new VaultProtectionStore(fixture);
        var c=(await store.ChallengeAsync(s.Actor,ProtocolBinary.Encode(SHA256.HashData(raw)),default)).Data!;
        var unlocked=await store.UnlockAsync(s.Actor,revision,c.ChallengeId,key.Sign(c),raw,default);Assert.Null(unlocked.Error);
        Assert.True(OpaqueAccessHandle.TryHash(unlocked.Data!.Access,out var verifier));return s with { Verifier=verifier };
    }
    private async Task<VaultProofChallenge> Challenge(Guid id)
    {await using var db=fixture.CreateContext();return JsonSerializer.Deserialize<VaultProofChallenge>((await db.VaultUnlockChallenges.SingleAsync(x=>x.PublicId==id)).Challenge,ProtectionFixture.Json)!;}
    private async Task<string> Snapshot(State s)
    {await using var db=fixture.CreateContext();return JsonSerializer.Serialize(new { Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==s.InternalId),Challenges=await db.VaultUnlockChallenges.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToListAsync(),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToListAsync(),Outcomes=await db.Set<VaultRecoveryOperation>().AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToListAsync() });}
    private static string Malleate(string proof)
    {var bytes=ProtectionFixture.Decode(proof);var order=BigInteger.Parse("0FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551",System.Globalization.NumberStyles.HexNumber);var s=new BigInteger(bytes.AsSpan(32),true,true);var alternate=(order-s).ToByteArray(true,true);Array.Clear(bytes,32,32);alternate.CopyTo(bytes,64-alternate.Length);return ProtectionFixture.Encode(bytes);}
    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> options;public Factory(DbContextOptions<AppDbContext> value)=>options=value;
        public Factory(PostgresFixture f,IInterceptor interceptor){using var db=f.CreateContext();options=new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(db.Database.GetConnectionString()).AddInterceptors(interceptor).Options;}
        public AppDbContext CreateDbContext()=>new(options);
    }
    private sealed class LockSignal(TaskCompletionSource reached,string? table=null) : DbCommandInterceptor
    {public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<int> r,CancellationToken ct=default){if(c.CommandText.Contains("FOR UPDATE",StringComparison.Ordinal)&&(table is null||c.CommandText.Contains(table,StringComparison.Ordinal)))reached.TrySetResult();return ValueTask.FromResult(r);}}
    private sealed class RejectWrite : DbCommandInterceptor
    {public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand c,CommandEventData d,InterceptionResult<DbDataReader> r,CancellationToken ct=default){if(c.CommandText.Contains("INSERT INTO cerberus.vault_recovery_operation",StringComparison.Ordinal))throw new TimeoutException("synthetic recovery write failure");return ValueTask.FromResult(r);}}
}
