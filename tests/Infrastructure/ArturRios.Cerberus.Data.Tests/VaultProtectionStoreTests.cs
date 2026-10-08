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
using System.Data.Common;

namespace ArturRios.Cerberus.Data.Tests;

[Collection("PostgreSQL")]
public class VaultProtectionStoreTests(PostgresFixture fixture)
{
    private static readonly byte[] Body = Encoding.UTF8.GetBytes("{\"expectedProtectionRevision\":1}");
    private static string Hash => ProtocolBinary.Encode(SHA256.HashData(Body));

    [FunctionalTheory]
    [InlineData("active", null)][InlineData("missing", "not_found")][InlineData("foreign", "not_found")]
    [InlineData("closing", "not_found")][InlineData("erased", "not_found")][InlineData("stale", "revision_conflict")]
    [InlineData("already", "revision_conflict")][InlineData("wrongPublicId", "not_found")]
    [InlineData("invalidMaterial", "validation_failed")][InlineData("laterEpoch", "validation_failed")]
    public async Task GivenBootstrapAccountState_WhenInitializing_ThenOnlyCurrentOwnerAndRevisionCreateProtection(string state, string? error)
    {
        using var client = new ProtectionFixture(); var input = await Setup(state, state == "already", client.Material);
        var material = state == "invalidMaterial" ? client.Material with { UnlockVerifier = null! }
            : state == "laterEpoch" ? client.Material with { PasswordWrapper = client.Material.PasswordWrapper with { KeyEpoch = 2 }, RecoveryWrapper = client.Material.RecoveryWrapper with { KeyEpoch = 2 } } : client.Material;
        var result = await Store().InitializeAsync(state == "foreign" ? Guid.NewGuid() : input.Actor,
            state == "wrongPublicId" ? Guid.NewGuid() : input.Id, state == "stale" ? 2 : 3, material, default);
        Assert.Equal(error,result.Error);
        await using var after = fixture.CreateContext();
        var row = await after.Set<VaultProtection>().SingleOrDefaultAsync(x => x.AccountId == input.InternalId);
        if (error is null)
        {
            Assert.Equal(input.Id,result.Data!.AccountId); Assert.Equal(1,result.Data.ProtectionRevision);
            Assert.NotNull(row); Assert.Equal(client.Material,JsonSerializer.Deserialize<ProtectionMaterial>(row.Material,ProtectionFixture.Json));
        }
        else { Assert.Null(result.Data); Assert.Equal(state == "already",row is not null); }
        Assert.Equal(input.Before,await Snapshot(input.Actor));
        Assert.Empty(await after.VaultAccessSessions.Where(x => x.AccountId == input.InternalId).ToListAsync());
    }

    [FunctionalFact]
    public async Task GivenConcurrentInitialization_WhenCommitting_ThenOneCompleteMaterialWins()
    {
        using var first = new ProtectionFixture(); using var second = new ProtectionFixture();
        var input = await Setup("active",false,first.Material);
        var results = await Task.WhenAll(Store().InitializeAsync(input.Actor,input.Id,3,first.Material,default),Store().InitializeAsync(input.Actor,input.Id,3,second.Material,default));
        var winner = Assert.Single(results,x => x.Error is null).Data!;
        Assert.Equal("revision_conflict",Assert.Single(results,x => x.Error is not null).Error);
        var read = await Store().ReadAsync(input.Actor,default);
        Assert.Equal(winner,read.Data); Assert.Equal(input.Before,await Snapshot(input.Actor));
    }

    [FunctionalTheory]
    [InlineData("active",null)][InlineData("missing","not_found")][InlineData("closing","not_found")]
    [InlineData("erased","not_found")][InlineData("noProtection","not_found")]
    public async Task GivenOwnProtection_WhenReading_ThenReturnOnlyActiveOwnMaterial(string state,string? error)
    {
        using var client = new ProtectionFixture(); var input = await Setup(state,state != "noProtection",client.Material);
        var result = await Store().ReadAsync(input.Actor,default);
        Assert.Equal(error,result.Error);
        if (error is null) { Assert.Equal(client.Material,result.Data!.Material); Assert.Equal(input.Id,result.Data.AccountId); }
        else Assert.Null(result.Data);
        Assert.Equal(input.Before,await Snapshot(input.Actor));
    }

    [FunctionalFact]
    public async Task GivenCurrentProtection_WhenIssuingChallenge_ThenBindCurrentStateAndCreateNoSession()
    {
        using var client = new ProtectionFixture(); var input = await Setup("active",true,client.Material);
        var result = await Store().ChallengeAsync(input.Actor,Hash,default); var challenge = result.Data!;
        Assert.Null(result.Error); Assert.Equal("unlock-account",challenge.Operation);
        Assert.Equal(input.Actor,challenge.IdentityId); Assert.Equal(input.Id,challenge.AccountId); Assert.Equal(input.Id,challenge.ScopeId);
        Assert.Equal(60,challenge.ExpiresAt-challenge.IssuedAt); Assert.Equal(Hash,challenge.RequestHash);
        Assert.True(ProtocolBinary.TryDecode(challenge.Nonce,32,out _));
        await using var after = fixture.CreateContext();
        var stored = await after.Set<VaultUnlockChallenge>().SingleAsync(x => x.PublicId == challenge.ChallengeId);
        Assert.Equal(1,stored.PolicyRevision); Assert.Equal(1,stored.RevocationGeneration); Assert.False(stored.Consumed);
        Assert.Empty(await after.VaultAccessSessions.Where(x => x.AccountId == input.InternalId).ToListAsync());
        Assert.Equal(input.Before,await Snapshot(input.Actor));
    }

    [FunctionalTheory]
    [InlineData("active")][InlineData("noExpiry")]
    public async Task GivenValidProof_WhenUnlocking_ThenConsumeOnceAndStoreOnlyHashedAccountAccess(string state)
    {
        using var client = new ProtectionFixture(); var input = await Setup(state,true,client.Material);
        var challenge = (await Store().ChallengeAsync(input.Actor,Hash,default)).Data!;
        var result = await Store().UnlockAsync(input.Actor,1,challenge.ChallengeId,client.Sign(challenge),Body,default);
        Assert.Null(result.Error); Assert.Equal(input.Id,result.Data!.AccountId);
        Assert.True(OpaqueAccessHandle.TryHash(result.Data.Access,out var verifier));
        await using var after = fixture.CreateContext();
        var access = await after.VaultAccessSessions.SingleAsync(x => x.AccountId == input.InternalId);
        Assert.Equal(verifier,access.HandleVerifier); Assert.NotEqual(result.Data.Access,access.HandleVerifier);
        Assert.Null(access.ProfileId); Assert.False(access.Revoked); Assert.Equal(1,access.PolicyRevision); Assert.Equal(1,access.RevocationGeneration);
        Assert.Equal(result.Data.IssuedAt,access.IssuedAt); Assert.Equal(result.Data.ExpiresAt,access.ExpiresAt);
        if (state == "noExpiry") Assert.Null(access.ExpiresAt); else Assert.Equal(TimeSpan.FromHours(24),access.ExpiresAt-access.IssuedAt);
        Assert.True((await after.Set<VaultUnlockChallenge>().SingleAsync(x => x.PublicId == challenge.ChallengeId)).Consumed);
        Assert.Equal(input.Before,await Snapshot(input.Actor));
        var replay = await Store().UnlockAsync(input.Actor,1,challenge.ChallengeId,client.Sign(challenge),Body,default);
        Assert.Equal("vault_proof_rejected",replay.Error);
        Assert.Single(await after.VaultAccessSessions.Where(x => x.AccountId == input.InternalId).ToListAsync());
    }

    [FunctionalTheory]
    [InlineData("actor","not_found")][InlineData("body","vault_proof_rejected")][InlineData("proof","vault_proof_rejected")]
    [InlineData("missing","vault_proof_rejected")][InlineData("consumed","vault_proof_rejected")]
    [InlineData("expired","vault_proof_rejected")][InlineData("future","vault_proof_rejected")]
    [InlineData("revision","revision_conflict")][InlineData("policy","revision_conflict")][InlineData("generation","revision_conflict")]
    [InlineData("closing","not_found")][InlineData("erased","not_found")]
    public async Task GivenInvalidOrStaleChallenge_WhenUnlocking_ThenIssueNoAccessAndPreserveState(string invalid,string error)
    {
        using var client = new ProtectionFixture(); var input = await Setup("active",true,client.Material);
        var challenge = (await Store().ChallengeAsync(input.Actor,Hash,default)).Data!;
        await using (var edit = fixture.CreateContext())
        {
            var stored = await edit.Set<VaultUnlockChallenge>().SingleAsync(x => x.PublicId == challenge.ChallengeId);
            if (invalid == "consumed") stored.Consumed = true;
            if (invalid is "expired" or "future")
            {
                var issued = DateTimeOffset.UtcNow.ToUnixTimeSeconds()+(invalid == "expired" ? -60 : 60);
                challenge = challenge with { IssuedAt = issued, ExpiresAt = issued+60 };
                stored.Challenge = JsonSerializer.SerializeToUtf8Bytes(challenge,ProtectionFixture.Json);
                stored.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(challenge.ExpiresAt);
            }
            var account = await edit.Accounts.SingleAsync(x => x.Id == input.InternalId);
            if (invalid == "policy") account.PolicyRevision++;
            if (invalid == "generation") account.RevocationGeneration++;
            if (invalid == "closing") account.State = AccountState.ClosurePending;
            if (invalid == "erased") edit.TerminalErasures.Add(new TerminalErasure { ResourceId = input.Id,ResourceKind = "account",DeletedAt = DateTimeOffset.UtcNow });
            await edit.SaveChangesAsync();
        }
        var before = await Snapshot(input.Actor);
        var result = await Store().UnlockAsync(invalid == "actor" ? Guid.NewGuid() : input.Actor,invalid == "revision" ? 2 : 1,
            invalid == "missing" ? Guid.NewGuid() : challenge.ChallengeId,
            invalid == "proof" ? ProtocolBinary.Encode(new byte[64]) : client.Sign(challenge),invalid == "body" ? Encoding.UTF8.GetBytes("{ \"expectedProtectionRevision\":1}") : Body,default);
        Assert.Equal(error,result.Error); Assert.Null(result.Data);
        await using var after = fixture.CreateContext();
        Assert.Empty(await after.VaultAccessSessions.Where(x => x.AccountId == input.InternalId).ToListAsync());
        Assert.Equal(before,await Snapshot(input.Actor));
        Assert.Equal(invalid == "consumed",(await after.Set<VaultUnlockChallenge>().SingleAsync(x => x.PublicId == challenge.ChallengeId)).Consumed);
    }

    [FunctionalFact]
    public async Task GivenConcurrentProofUses_WhenUnlocking_ThenExactlyOneSessionCommits()
    {
        using var client = new ProtectionFixture(); var input = await Setup("active",true,client.Material);
        var c = (await Store().ChallengeAsync(input.Actor,Hash,default)).Data!; var proof = client.Sign(c);
        var results = await Task.WhenAll(Store().UnlockAsync(input.Actor,1,c.ChallengeId,proof,Body,default),Store().UnlockAsync(input.Actor,1,c.ChallengeId,proof,Body,default));
        Assert.Single(results,x => x.Data is not null); Assert.Single(results,x => x.Error == "vault_proof_rejected");
        await using var after = fixture.CreateContext();
        Assert.Single(await after.VaultAccessSessions.Where(x => x.AccountId == input.InternalId).ToListAsync());
    }

    [FunctionalFact]
    public async Task GivenChallengeExpiresWhileWaitingForAccountLock_WhenUnlocking_ThenUseFreshStatementTime()
    {
        using var client = new ProtectionFixture(); var input = await Setup("active",true,client.Material);
        var c = (await Store().ChallengeAsync(input.Actor,Hash,default)).Data!;
        var expires = DateTimeOffset.UtcNow.ToUnixTimeSeconds()+2;
        c = c with { IssuedAt = expires-60, ExpiresAt = expires };
        await using (var edit = fixture.CreateContext())
        {
            var row = await edit.Set<VaultUnlockChallenge>().SingleAsync(x => x.PublicId == c.ChallengeId);
            row.Challenge = JsonSerializer.SerializeToUtf8Bytes(c,ProtectionFixture.Json); row.ExpiresAt = DateTimeOffset.FromUnixTimeSeconds(expires);
            await edit.SaveChangesAsync();
        }
        await using var holder = fixture.CreateContext(); await using var held = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id = {input.InternalId} FOR UPDATE");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new VaultProtectionStore(new Factory(fixture,new LockSignal(started)));
        var task = waiting.UnlockAsync(input.Actor,1,c.ChallengeId,client.Sign(c),Body,default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        // Let the unchanged expiry pass while the real account lock blocks the query.
        var delay = DateTimeOffset.FromUnixTimeSeconds(expires).AddMilliseconds(20)-DateTimeOffset.UtcNow;
        if (delay > TimeSpan.Zero) await Task.Delay(delay);
        await held.CommitAsync();
        Assert.Equal("vault_proof_rejected",(await task).Error);
        await using var after = fixture.CreateContext();
        Assert.Empty(await after.VaultAccessSessions.Where(x => x.AccountId == input.InternalId).ToListAsync());
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(expires),(await after.Set<VaultUnlockChallenge>().SingleAsync(x => x.PublicId == c.ChallengeId)).ExpiresAt);
    }

    [FunctionalTheory]
    [InlineData("closing","not_found")][InlineData("erased","not_found")][InlineData("policy","revision_conflict")]
    public async Task GivenLifecycleOrPolicyChangesWhileQueued_WhenUnlocking_ThenRecheckAfterAccountLock(string change,string error)
    {
        using var client = new ProtectionFixture(); var input = await Setup("active",true,client.Material);
        var c = (await Store().ChallengeAsync(input.Actor,Hash,default)).Data!;
        await using var holder = fixture.CreateContext(); await using var held = await holder.Database.BeginTransactionAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id = {input.InternalId} FOR UPDATE");
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var waiting = new VaultProtectionStore(new Factory(fixture,new LockSignal(started)));
        var task = waiting.UnlockAsync(input.Actor,1,c.ChallengeId,client.Sign(c),Body,default);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var account = await holder.Accounts.SingleAsync(x => x.Id == input.InternalId);
        if (change == "closing") account.State = AccountState.ClosurePending;
        if (change == "policy") account.PolicyRevision++;
        if (change == "erased") holder.TerminalErasures.Add(new TerminalErasure { ResourceId = input.Id,ResourceKind = "account",DeletedAt = DateTimeOffset.UtcNow });
        await holder.SaveChangesAsync(); await held.CommitAsync();
        Assert.Equal(error,(await task).Error);
        await using var after = fixture.CreateContext();
        Assert.Empty(await after.VaultAccessSessions.Where(x => x.AccountId == input.InternalId).ToListAsync());
        Assert.False((await after.Set<VaultUnlockChallenge>().SingleAsync(x => x.PublicId == c.ChallengeId)).Consumed);
    }

    [FunctionalFact]
    public async Task GivenProtectionAndChallenge_WhenDeletingAccount_ThenCascadeBothWithoutOrphans()
    {
        using var client = new ProtectionFixture(); var input = await Setup("active",true,client.Material);
        var c = (await Store().ChallengeAsync(input.Actor,Hash,default)).Data!;
        await using var db = fixture.CreateContext();
        await db.Accounts.Where(x => x.Id == input.InternalId).ExecuteDeleteAsync();
        Assert.False(await db.VaultProtections.AnyAsync(x => x.AccountId == input.InternalId));
        Assert.False(await db.VaultUnlockChallenges.AnyAsync(x => x.PublicId == c.ChallengeId));
    }

    [FunctionalTheory]
    [InlineData("init")][InlineData("read")][InlineData("challenge")][InlineData("unlock")]
    public async Task GivenUnavailablePersistence_WhenOperating_ThenFailClosed(string operation)
    {
        using var client = new ProtectionFixture();
        var factory = new Factory(new DbContextOptionsBuilder<AppDbContext>().UseNpgsql("Host=127.0.0.1;Port=1;Database=unavailable;Username=fixture;Password=fixture;Timeout=1").Options);
        var store = new VaultProtectionStore(factory); var id = Guid.NewGuid();
        var error = operation switch
        {
            "init" => (await store.InitializeAsync(id,Guid.NewGuid(),3,client.Material,default)).Error,
            "read" => (await store.ReadAsync(id,default)).Error,
            "challenge" => (await store.ChallengeAsync(id,Hash,default)).Error,
            _ => (await store.UnlockAsync(id,1,Guid.NewGuid(),"proof",Body,default)).Error
        };
        Assert.Equal("persistence_unavailable",error);
    }

    [FunctionalFact]
    public async Task GivenCancelledCaller_WhenInitializing_ThenPropagateWithoutWriting()
    {
        using var client = new ProtectionFixture(); var input = await Setup("active",false,client.Material);
        using var source = new CancellationTokenSource(); source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Store().InitializeAsync(input.Actor,input.Id,3,client.Material,source.Token));
        await using var after = fixture.CreateContext();
        Assert.Empty(await after.Set<VaultProtection>().Where(x => x.AccountId == input.InternalId).ToListAsync());
    }

    [FunctionalFact]
    public async Task GivenSessionInsertFailure_WhenUnlocking_ThenRollBackChallengeConsumption()
    {
        using var client = new ProtectionFixture(); var input = await Setup("active",true,client.Material);
        var c = (await Store().ChallengeAsync(input.Actor,Hash,default)).Data!;
        var failure = new VaultProtectionStore(new Factory(fixture,new RejectInsert()));
        var result = await failure.UnlockAsync(input.Actor,1,c.ChallengeId,client.Sign(c),Body,default);
        Assert.Equal("persistence_unavailable",result.Error);
        await using var after = fixture.CreateContext();
        Assert.False((await after.Set<VaultUnlockChallenge>().SingleAsync(x => x.PublicId == c.ChallengeId)).Consumed);
        Assert.Empty(await after.VaultAccessSessions.Where(x => x.AccountId == input.InternalId).ToListAsync());
        Assert.Null((await Store().UnlockAsync(input.Actor,1,c.ChallengeId,client.Sign(c),Body,default)).Error);
    }

    private VaultProtectionStore Store() => new(fixture);
    private async Task<(Guid Actor,Guid Id,long InternalId,string Before)> Setup(string state,bool protection,ProtectionMaterial material)
    {
        var actor = Guid.NewGuid(); var id = Guid.NewGuid(); long internalId = -1;
        if (state != "missing")
        {
            await using var context = fixture.CreateContext();
            var account = new Account { PublicId = id, HeimdallPublicId = actor, DetailsEnvelope = [1,2,3], Revision = 3,
                State = state == "closing" ? AccountState.ClosurePending : AccountState.Active, RenewalEnabled = state != "noExpiry" };
            context.Accounts.Add(account); await context.SaveChangesAsync(); internalId = account.Id;
            if (protection) context.Set<VaultProtection>().Add(new VaultProtection { AccountId = account.Id,Material = JsonSerializer.SerializeToUtf8Bytes(material,ProtectionFixture.Json) });
            if (state == "erased") context.TerminalErasures.Add(new TerminalErasure { ResourceId = id,ResourceKind = "account",DeletedAt = DateTimeOffset.UtcNow });
            await context.SaveChangesAsync();
        }
        return (actor,id,internalId,await Snapshot(actor));
    }
    private async Task<string> Snapshot(Guid actor)
    {
        await using var context = fixture.CreateContext();
        return JsonSerializer.Serialize(await context.Accounts.AsNoTracking().SingleOrDefaultAsync(x => x.HeimdallPublicId == actor));
    }
    private sealed class Factory : IDbContextFactory<AppDbContext>
    {
        private readonly DbContextOptions<AppDbContext> _options;
        public Factory(DbContextOptions<AppDbContext> options) => _options = options;
        public Factory(PostgresFixture fixture,IInterceptor interceptor)
        { using var context = fixture.CreateContext(); _options = new DbContextOptionsBuilder<AppDbContext>().UseNpgsql(context.Database.GetConnectionString()).AddInterceptors(interceptor).Options; }
        public AppDbContext CreateDbContext() => new(_options);
    }
    private sealed class LockSignal(TaskCompletionSource signal) : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<int> result,CancellationToken cancellationToken = default)
        { if (command.CommandText.Contains("FOR UPDATE",StringComparison.Ordinal)) signal.TrySetResult(); return ValueTask.FromResult(result); }
    }
    private sealed class RejectInsert : DbCommandInterceptor
    {
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,CommandEventData data,InterceptionResult<DbDataReader> result,CancellationToken cancellationToken = default)
        { if (command.CommandText.Contains("INSERT INTO cerberus.vault_access_session",StringComparison.Ordinal)) throw new TimeoutException("synthetic session write failure"); return ValueTask.FromResult(result); }
    }
}
