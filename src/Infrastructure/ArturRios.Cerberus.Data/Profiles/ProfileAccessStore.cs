using System.Data.Common;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Profiles;

public sealed partial class ProfileAccessStore(IDbContextFactory<AppDbContext> factory)
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false};
    public Task<VaultResult<ProfileChallengeDetails>> ChallengeAsync(ProfileChallengeRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return Task.FromResult(new VaultResult<ProfileChallengeDetails>(Error:"authentication_required"));
        if(!request.IsValid())return Task.FromResult(new VaultResult<ProfileChallengeDetails>(Error:"validation_failed"));
        return Run<ProfileChallengeDetails>(request.Actor,request.ProfileId,request.ExpectedRevision,ct,async(db,account,profile,material,token)=>
        {
            var now=await Now(db,token);
            await db.VaultUnlockChallenges.Where(x=>x.AccountId==account.Id && (x.Consumed || x.ExpiresAt<=now)).ExecuteDeleteAsync(token);
            var issued=now.ToUnixTimeSeconds();
            var challenge=new VaultProofChallenge("cerberus-challenge-v1",Guid.NewGuid(),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)),
                "unlock-profile",request.Actor,account.PublicId,"profile",profile.PublicId,material.Envelope.KeyEpoch,profile.Revision,null,request.RequestHash,issued,issued+60);
            db.VaultUnlockChallenges.Add(new(){AccountId=account.Id,PublicId=challenge.ChallengeId,Challenge=JsonSerializer.SerializeToUtf8Bytes(challenge,Json),
                PolicyRevision=account.PolicyRevision,RevocationGeneration=account.RevocationGeneration,ExpiresAt=DateTimeOffset.FromUnixTimeSeconds(challenge.ExpiresAt)});
            return new(new(challenge,material));
        });
    }
    private async Task<VaultResult<T>> Run<T>(Guid actor,Guid profileId,long expectedRevision,CancellationToken ct,
        Func<AppDbContext,AccountView,Profile,ProfileAccessMaterial,CancellationToken,Task<VaultResult<T>>> execute) where T:class
    {
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE heimdall_public_id={actor} FOR UPDATE",ct);
            var account=await db.Accounts.AsNoTracking().Where(x=>x.HeimdallPublicId==actor)
                .Select(x=>new AccountView(x.Id,x.PublicId,x.State,x.PolicyRevision,x.RevocationGeneration,x.RenewalEnabled,x.RenewalInterval)).SingleOrDefaultAsync(ct);
            if(account is null || account.State!=AccountState.Active || await db.TerminalErasures.AnyAsync(x=>(x.ResourceKind == "account" && x.ResourceId == account.PublicId),ct))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.profile WHERE account_id={account.Id} AND public_id={profileId} FOR UPDATE",ct);
            var profile=await db.Profiles.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==account.Id && x.PublicId==profileId,ct);
            if(profile is null || profile.DeletedAt is not null || await db.TerminalErasures.AnyAsync(x=>(x.ResourceKind == "profile" && x.ResourceId == profile.PublicId),ct))return new(Error:"not_found");
            if(profile.Revision is <=0 or >ProtocolBinary.MaxInteger || profile.ServerSequence is <=0 or >ProtocolBinary.MaxInteger)return new(Error:"persistence_unavailable");
            if(profile.Revision!=expectedRevision)return new(Error:"revision_conflict");
            if(account.PolicyRevision is <=0 or >ProtocolBinary.MaxInteger || account.RevocationGeneration is <=0 or >ProtocolBinary.MaxInteger
                || account.RenewalEnabled && account.RenewalInterval is not {Ticks:>0})return new(Error:"persistence_unavailable");
            var material=await ProfileUnlockMaterial.ReadAsync(db,account.Id,account.PublicId,actor,profile,ct);
            var result=await execute(db,account,profile,material,ct);
            if(result.Error is not null)return result;
            await db.SaveChangesAsync(ct);await tx.CommitAsync(ct);return result;
        }
        catch(Exception e) when(e is DbException or DbUpdateException or TimeoutException or JsonException or ArgumentOutOfRangeException
            || e is InvalidOperationException {InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
    }
    private static Task<DateTimeOffset> Now(AppDbContext db,CancellationToken ct)=>db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
    private sealed record AccountView(long Id,Guid PublicId,AccountState State,long PolicyRevision,long RevocationGeneration,bool RenewalEnabled,TimeSpan? RenewalInterval);
}
