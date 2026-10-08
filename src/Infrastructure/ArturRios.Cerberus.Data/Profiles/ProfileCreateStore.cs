using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Profiles;

public sealed class ProfileCreateStore(IDbContextFactory<AppDbContext> factory) : IProfileCreateStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false};
    public async Task<VaultResult<ProfileCreateDetails>> CreateAsync(ProfileCreateRequest request,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        var input=request.Input;if(input?.IsValid()!=true)return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(cancellationToken);
            await using var tx=await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE",cancellationToken);
            var account=await db.Accounts.AsNoTracking().Where(x=>x.HeimdallPublicId==request.Actor)
                .Select(x=>new{x.Id,x.PublicId,x.State,x.PolicyRevision,x.RevocationGeneration,x.RenewalEnabled}).SingleOrDefaultAsync(cancellationToken);
            if(account is null || account.State!=AccountState.Active || await db.TerminalErasures.AnyAsync(x=>x.ResourceId==account.PublicId,cancellationToken))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",cancellationToken);
            var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
            if(!await db.VaultAccessSessions.AnyAsync(x=>x.AccountId==account.Id && x.HandleVerifier==request.AccessVerifier && x.ProfileId==null && !x.Revoked
                && x.PolicyRevision>0 && x.RevocationGeneration>0 && x.PolicyRevision==account.PolicyRevision && x.RevocationGeneration==account.RevocationGeneration
                && x.IssuedAt<=now && (account.RenewalEnabled?x.ExpiresAt>now:x.ExpiresAt==null),cancellationToken))return new(Error:"vault_access_denied");
            var p=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==account.Id,cancellationToken);
            if(p is null)return new(Error:"not_found");
            var material=JsonSerializer.Deserialize<ProtectionMaterial>(p.Material,Json);
            if(material?.IsValid()!=true || p.KeyEpoch!=material.PasswordWrapper.KeyEpoch || p.RecoveryGeneration!=material.RecoveryWrapper.Generation
                || p.Revision is <=0 or >ProtocolBinary.MaxInteger)return new(Error:"persistence_unavailable");
            if(!input.KeyWrappers.IsBound(account.PublicId,input.ProfileId,1,1,request.Actor,material))return new(Error:"validation_failed");
            // No relationship entity exists yet; well-formed unresolved IDs remain hidden.
            if(input.RecordIds.Length+input.FolderIds.Length+input.CollectionIds.Length>0)return new(Error:"not_found");
            if(await db.Profiles.AnyAsync(x=>x.PublicId==input.ProfileId,cancellationToken)
                || await db.TerminalErasures.AnyAsync(x=>x.ResourceId==input.ProfileId,cancellationToken))return new(Error:"revision_conflict");
            var envelope=JsonSerializer.SerializeToUtf8Bytes(input.Envelope,Json);var wrappers=JsonSerializer.SerializeToUtf8Bytes(input.KeyWrappers,Json);
            // Atomic permission check uses statement time after lock waits and validation.
            var inserted=await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cerberus.profile(public_id,account_id,envelope,key_wrappers,revision,edited_at,concurrency_stamp)
                SELECT {input.ProfileId},{account.Id},{envelope},{wrappers},1,{input.EditedAt},{Guid.NewGuid()}
                FROM cerberus.vault_access_session
                WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier}
                AND profile_id IS NULL AND NOT revoked AND policy_revision={account.PolicyRevision}
                AND revocation_generation={account.RevocationGeneration} AND issued_at<=statement_timestamp()
                AND (({account.RenewalEnabled} AND expires_at>statement_timestamp())
                    OR (NOT {account.RenewalEnabled} AND expires_at IS NULL))
                """,cancellationToken);
            if(inserted!=1)return new(Error:"vault_access_denied");
            var result=await db.Profiles.AsNoTracking().Where(x=>x.PublicId==input.ProfileId)
                .Select(x=>new ProfileCreateDetails(x.PublicId,x.Revision,x.ServerSequence,x.EditedAt)).SingleAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);return new(result);
        }
        catch(PostgresException exception) when(exception.SqlState is PostgresErrorCodes.UniqueViolation or "2200H") {return new(Error:"revision_conflict");}
        catch(Exception exception) when(exception is DbException or DbUpdateException or TimeoutException or JsonException
            || exception is InvalidOperationException {InnerException:DbException or DbUpdateException or TimeoutException}) {return new(Error:"persistence_unavailable");}
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested) {return new(Error:"persistence_unavailable");}
    }
}
