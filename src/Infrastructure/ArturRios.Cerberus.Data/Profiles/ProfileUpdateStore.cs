using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace ArturRios.Cerberus.Data.Profiles;

public sealed class ProfileUpdateStore(IDbContextFactory<AppDbContext> factory):IProfileUpdateStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web){PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false};
    public async Task<VaultResult<ProfileCreateDetails>> UpdateAsync(ProfileUpdateRequest request,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(request.ProfileId==Guid.Empty || request.Input?.IsValid()!=true)return new(Error:"validation_failed");
        var input=request.Input;
        try
        {
            await using var db=await factory.CreateDbContextAsync(cancellationToken);
            await using var tx=await db.Database.BeginTransactionAsync(cancellationToken);
            // Same lock order as account protection rotation. Never use transaction-start time.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE",cancellationToken);
            var account=await db.Accounts.AsNoTracking().Where(x=>x.HeimdallPublicId==request.Actor)
                .Select(x=>new{x.Id,x.PublicId,x.State,x.PolicyRevision,x.RevocationGeneration,x.RenewalEnabled}).SingleOrDefaultAsync(cancellationToken);
            if(account is null || account.State!=AccountState.Active || await db.TerminalErasures.AnyAsync(x=>x.ResourceId==account.PublicId,cancellationToken))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",cancellationToken);
            var session=await db.VaultAccessSessions.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==account.Id && x.HandleVerifier==request.AccessVerifier,cancellationToken);
            var now=await Now(db,cancellationToken);
            if(session is null || session.Revoked || session.PolicyRevision<=0 || session.RevocationGeneration<=0
                || session.PolicyRevision!=account.PolicyRevision || session.RevocationGeneration!=account.RevocationGeneration
                || session.IssuedAt>now || (account.RenewalEnabled?session.ExpiresAt is null || session.ExpiresAt<=now:session.ExpiresAt is not null))return new(Error:"vault_access_denied");
            if(session.ProfileId is not null && !await db.Profiles.AnyAsync(p=>p.Id==session.ProfileId && p.AccountId==account.Id && p.DeletedAt==null
                && !db.TerminalErasures.Any(e=>e.ResourceId==p.PublicId),cancellationToken))return new(Error:"vault_access_denied");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.profile WHERE account_id={account.Id} AND public_id={request.ProfileId} FOR UPDATE",cancellationToken);
            var profile=await db.Profiles.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==account.Id && x.PublicId==request.ProfileId,cancellationToken);
            if(profile is null || profile.DeletedAt is not null || session.ProfileId is not null && session.ProfileId!=profile.Id
                || await db.TerminalErasures.AnyAsync(x=>x.ResourceId==profile.PublicId,cancellationToken))return new(Error:"not_found");
            if(profile.Revision is <=0 or >ProtocolBinary.MaxInteger || profile.ServerSequence is <=0 or >ProtocolBinary.MaxInteger)return new(Error:"persistence_unavailable");
            if(profile.Revision!=input.ExpectedRevision || profile.Revision==ProtocolBinary.MaxInteger)return new(Error:"revision_conflict");
            var protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==account.Id,cancellationToken);
            if(protection is null)return new(Error:"persistence_unavailable");
            var pins=JsonSerializer.Deserialize<ProtectionMaterial>(protection.Material,Json);
            var envelope=JsonSerializer.Deserialize<EncryptedEnvelope>(profile.Envelope,Json);
            var wrappers=JsonSerializer.Deserialize<ProfileKeyWrappers>(profile.KeyWrappers,Json);
            if(pins?.IsValid()!=true || protection.KeyEpoch!=pins.PasswordWrapper.KeyEpoch || protection.RecoveryGeneration!=pins.RecoveryWrapper.Generation
                || protection.Revision is <=0 or >ProtocolBinary.MaxInteger || envelope?.IsValid()!=true || wrappers?.IsValid()!=true
                || wrappers.MasterKeyWrapper.GrantRevision>profile.Revision
                || !wrappers.IsBound(account.PublicId,profile.PublicId,envelope.KeyEpoch,wrappers.MasterKeyWrapper.GrantRevision,request.Actor,pins))return new(Error:"persistence_unavailable");
            if(input.Envelope.KeyEpoch!=envelope.KeyEpoch || input.Envelope!=envelope
                && (input.Envelope.KeySalt==envelope.KeySalt || input.Envelope.Nonce==envelope.Nonce))return new(Error:"validation_failed");
            var bytes=JsonSerializer.SerializeToUtf8Bytes(input.Envelope,Json);
            var editedAt=input.EditedAt.AddTicks(-(input.EditedAt.Ticks%TimeSpan.TicksPerMicrosecond));
            var changed=await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cerberus.profile AS p SET envelope={bytes},edited_at={editedAt},revision=p.revision+1,
                    server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={Guid.NewGuid()}
                WHERE p.id={profile.Id} AND p.account_id={account.Id} AND p.revision={input.ExpectedRevision}
                    AND p.deleted_at IS NULL AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE e.resource_id=p.public_id)
                    AND EXISTS(SELECT 1 FROM cerberus.account a JOIN cerberus.vault_access_session s ON s.account_id=a.id
                        WHERE a.id=p.account_id AND a.heimdall_public_id={request.Actor} AND a.state={AccountState.Active}
                        AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE e.resource_id=a.public_id)
                        AND s.handle_verifier={request.AccessVerifier} AND NOT s.revoked
                        AND s.policy_revision>0 AND s.revocation_generation>0
                        AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
                        AND s.issued_at<=statement_timestamp()
                        AND ((a.renewal_enabled AND s.expires_at>statement_timestamp()) OR (NOT a.renewal_enabled AND s.expires_at IS NULL))
                        AND (s.profile_id IS NULL OR s.profile_id=p.id))
                """,cancellationToken);
            if(changed!=1)return new(Error:"vault_access_denied");
            var result=await db.Profiles.AsNoTracking().Where(x=>x.Id==profile.Id).Select(x=>new ProfileCreateDetails(x.PublicId,x.Revision,x.ServerSequence,x.EditedAt)).SingleAsync(cancellationToken);
            if(result.ServerSequence<=profile.ServerSequence || result.ServerSequence>ProtocolBinary.MaxInteger)return new(Error:"persistence_unavailable");
            await tx.CommitAsync(cancellationToken);return new(result);
        }
        catch(PostgresException e) when(e.SqlState=="2200H"){return new(Error:"revision_conflict");}
        catch(Exception e) when(e is DbException or DbUpdateException or TimeoutException or JsonException
            || e is InvalidOperationException {InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested){return new(Error:"persistence_unavailable");}
    }
    private static Task<DateTimeOffset> Now(AppDbContext db,CancellationToken ct)=>db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
}
