using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
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
            if(account is null || account.State!=AccountState.Active || await db.TerminalErasures.AnyAsync(x=>(x.ResourceKind == "account" && x.ResourceId == account.PublicId),cancellationToken))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",cancellationToken);
            async Task<bool> Permitted()
            {
                var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(cancellationToken);
                return await db.VaultAccessSessions.AnyAsync(x=>x.AccountId==account.Id && x.HandleVerifier==request.AccessVerifier && x.ProfileId==null && !x.Revoked
                    && x.PolicyRevision>0 && x.RevocationGeneration>0 && x.PolicyRevision==account.PolicyRevision && x.RevocationGeneration==account.RevocationGeneration
                    && x.IssuedAt<=now && (account.RenewalEnabled?x.ExpiresAt>now:x.ExpiresAt==null),cancellationToken);
            }
            if(!await Permitted())return new(Error:"vault_access_denied");
            var p=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==account.Id,cancellationToken);
            if(p is null)return new(Error:"not_found");
            var material=JsonSerializer.Deserialize<ProtectionMaterial>(p.Material,Json);
            if(material?.IsValid()!=true || p.KeyEpoch!=material.PasswordWrapper.KeyEpoch || p.RecoveryGeneration!=material.RecoveryWrapper.Generation
                || p.Revision is <=0 or >ProtocolBinary.MaxInteger)return new(Error:"persistence_unavailable");
            if(!input.KeyWrappers.IsBound(account.PublicId,input.ProfileId,1,1,request.Actor,material))return new(Error:"validation_failed");
            var resolved=await ProfileAssociationResolver.ResolveAsync(db,account.Id,request.Actor,input.RecordIds,input.FolderIds,input.CollectionIds,cancellationToken);
            if(!await Permitted())return new(Error:"vault_access_denied");
            if(resolved.Error is not null)return new(Error:resolved.Error);
            if(await db.Profiles.AnyAsync(x=>x.PublicId==input.ProfileId,cancellationToken)
                || await db.TerminalErasures.AnyAsync(x=>(x.ResourceKind == "profile" && x.ResourceId == input.ProfileId),cancellationToken))return new(Error:"revision_conflict");
            if(!await ProfileVerifierIsolation.IsUniqueAsync(db,account.Id,input.KeyWrappers.UnlockVerifier,null,cancellationToken))return new(Error:"validation_failed");
            var envelope=JsonSerializer.SerializeToUtf8Bytes(input.Envelope,Json);var wrappers=JsonSerializer.SerializeToUtf8Bytes(input.KeyWrappers,Json);
            // Normalize before binding: provider truncation is relative to its 2000 epoch.
            var editedAt=input.EditedAt.AddTicks(-(input.EditedAt.Ticks%TimeSpan.TicksPerMicrosecond));
            // Atomic permission check uses statement time after lock waits and validation.
            var inserted=await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cerberus.profile(public_id,account_id,envelope,key_wrappers,revision,edited_at,concurrency_stamp)
                SELECT {input.ProfileId},{account.Id},{envelope},{wrappers},1,{editedAt},{Guid.NewGuid()}
                FROM cerberus.vault_access_session
                WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier}
                AND profile_id IS NULL AND NOT revoked AND policy_revision={account.PolicyRevision}
                AND revocation_generation={account.RevocationGeneration} AND issued_at<=statement_timestamp()
                AND (({account.RenewalEnabled} AND expires_at>statement_timestamp())
                    OR (NOT {account.RenewalEnabled} AND expires_at IS NULL))
                AND EXISTS(SELECT 1 FROM cerberus.account a WHERE a.id={account.Id} AND a.state={AccountState.Active}
                    AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=a.public_id)))
                    AND (SELECT count(*) FROM cerberus.record r WHERE r.public_id=ANY({input.RecordIds}) AND r.account_id={account.Id} AND r.deleted_at IS NULL
                        AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE (e.resource_kind='record' AND e.resource_id=r.public_id)))=cardinality({input.RecordIds})
                    AND (SELECT count(*) FROM cerberus.folder f WHERE f.public_id=ANY({input.FolderIds}) AND f.account_id={account.Id} AND f.deleted_at IS NULL
                        AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE (e.resource_kind='folder' AND e.resource_id=f.public_id)))=cardinality({input.FolderIds})
                    AND (SELECT count(*) FROM cerberus.collection c JOIN cerberus.account o ON o.id=c.account_id
                        WHERE c.public_id=ANY({input.CollectionIds}) AND c.deleted_at IS NULL AND o.state={AccountState.Active}
                        AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE (e.resource_kind='collection' AND e.resource_id=c.public_id) OR (e.resource_kind='account' AND e.resource_id=o.public_id))
                        AND (c.account_id={account.Id} OR EXISTS(SELECT 1 FROM cerberus.collection_grant g WHERE g.collection_id=c.id
                            AND g.recipient_account_id={account.Id} AND g.state={CollectionGrantState.Active}
                            AND (g.access={CollectionGrantAccess.ReadOnly} OR g.access={CollectionGrantAccess.ReadWrite})
                            AND g.revision>0 AND g.revision<={ProtocolBinary.MaxInteger}
                            AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE (e.resource_kind='grant' AND e.resource_id=g.public_id)))))=cardinality({input.CollectionIds})
                """,cancellationToken);
            if(inserted!=1)return new(Error:await Permitted()?"not_found":"vault_access_denied");
            var profile=await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==input.ProfileId,cancellationToken);
            db.ProfileRecords.AddRange(resolved.Records.Select(id=>new ProfileRecord{AccountId=account.Id,ProfileId=profile.Id,RecordId=id}));
            db.ProfileFolders.AddRange(resolved.Folders.Select(id=>new ProfileFolder{AccountId=account.Id,ProfileId=profile.Id,FolderId=id}));
            db.ProfileCollections.AddRange(resolved.Collections.Select(id=>new ProfileCollection{ProfileId=profile.Id,CollectionId=id}));
            await db.SaveChangesAsync(cancellationToken);
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
