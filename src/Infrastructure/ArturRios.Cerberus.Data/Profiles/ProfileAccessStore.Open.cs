using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.Data.Profiles;

public sealed partial class ProfileAccessStore:IProfileAccessStore
{
    public Task<VaultResult<ProfileAccessDetails>> OpenAsync(ProfileAccessRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return Task.FromResult(new VaultResult<ProfileAccessDetails>(Error:"authentication_required"));
        if(!request.IsValid() || !MatchesBody(request))return Task.FromResult(new VaultResult<ProfileAccessDetails>(Error:"validation_failed"));
        return Run<ProfileAccessDetails>(request.Actor,request.ProfileId,request.ExpectedRevision,ct,async(db,account,profile,material,token)=>
        {
            var row=await db.VaultUnlockChallenges.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==account.Id && x.PublicId==request.ChallengeId,token);
            if(row is null || row.Consumed)return new(Error:"vault_proof_rejected");
            var challenge=JsonSerializer.Deserialize<VaultProofChallenge>(row.Challenge,Json) ?? throw new JsonException();
            if(challenge.ChallengeId!=row.PublicId || row.ExpiresAt!=DateTimeOffset.FromUnixTimeSeconds(challenge.ExpiresAt))return new(Error:"persistence_unavailable");
            if(row.PolicyRevision!=account.PolicyRevision || row.RevocationGeneration!=account.RevocationGeneration
                || challenge.ProtectionRevision!=profile.Revision || challenge.KeyEpoch!=material.Envelope.KeyEpoch)return new(Error:"revision_conflict");
            var now=await Now(db,token);
            var binding=new VaultProofBinding("unlock-profile",request.Actor,account.PublicId,"profile",profile.PublicId,material.Envelope.KeyEpoch,profile.Revision,null);
            if(!VaultProof.Verify(challenge,binding,request.RawBody,material.KeyWrappers.UnlockVerifier,request.Proof,now.ToUnixTimeSeconds()))return new(Error:"vault_proof_rejected");
            DateTimeOffset? expires=account.RenewalEnabled?now.Add(account.RenewalInterval!.Value):null;
            var access=ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32));
            if(!OpaqueAccessHandle.TryHash(access,out var verifier))return new(Error:"persistence_unavailable");
            // The one-use decision checks current authority and exclusive expiry at statement time.
            var consumed=await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cerberus.vault_unlock_challenge AS c SET consumed=TRUE
                WHERE c.id={row.Id} AND c.account_id={account.Id} AND c.public_id={request.ChallengeId}
                    AND NOT c.consumed AND c.expires_at>statement_timestamp()
                    AND c.policy_revision={account.PolicyRevision} AND c.revocation_generation={account.RevocationGeneration}
                    AND EXISTS(SELECT 1 FROM cerberus.account a WHERE a.id=c.account_id AND a.heimdall_public_id={request.Actor}
                        AND a.state={AccountState.Active} AND a.policy_revision=c.policy_revision AND a.revocation_generation=c.revocation_generation
                        AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=a.public_id)))
                    AND EXISTS(SELECT 1 FROM cerberus.profile p WHERE p.id={profile.Id} AND p.account_id=c.account_id
                        AND p.public_id={request.ProfileId} AND p.revision={request.ExpectedRevision} AND p.deleted_at IS NULL
                        AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE (e.resource_kind='profile' AND e.resource_id=p.public_id)))
                """,token);
            if(consumed!=1)
            {
                if(await db.TerminalErasures.AnyAsync(x=>(x.ResourceKind == "account" && x.ResourceId == account.PublicId) || (x.ResourceKind == "profile" && x.ResourceId == profile.PublicId),token))return new(Error:"not_found");
                return new(Error:"vault_proof_rejected");
            }
            var visible=db.Profiles.AsNoTracking().Where(p=>p.Id==profile.Id && p.AccountId==account.Id && p.DeletedAt==null
                && p.Revision==request.ExpectedRevision && !db.TerminalErasures.Any(e=>(e.ResourceKind == "profile" && e.ResourceId == p.PublicId) || (e.ResourceKind == "account" && e.ResourceId == account.PublicId)));
            var projected=await ProfileProjectionQuery.Select(db,visible).SingleOrDefaultAsync(token);
            if(projected is null)return new(Error:"not_found");
            var context=new ProfileAccessContext(projected.ProfileId,projected.Revision,projected.ServerSequence,projected.EditedAt,
                material.Envelope,material.KeyWrappers,projected.RecordIds,projected.FolderIds,projected.CollectionIds);
            if(!context.IsValid(account.PublicId,request.Actor,request.ProfileId,request.ExpectedRevision))return new(Error:"persistence_unavailable");
            db.VaultAccessSessions.Add(new(){AccountId=account.Id,ProfileId=profile.Id,HandleVerifier=verifier,IssuedAt=now,ExpiresAt=expires,
                PolicyRevision=account.PolicyRevision,RevocationGeneration=account.RevocationGeneration});
            return new(new(account.PublicId,access,now,expires,context));
        });
    }
    private static bool MatchesBody(ProfileAccessRequest request)
    {
        try{return JsonSerializer.Deserialize<ProfileAccessInput>(request.RawBody,Json) is { } input && input.IsValid() && input.ExpectedRevision==request.ExpectedRevision;}
        catch(JsonException){return false;}
    }
}
