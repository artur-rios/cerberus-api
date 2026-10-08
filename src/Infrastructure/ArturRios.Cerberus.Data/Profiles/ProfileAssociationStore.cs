using System.Data.Common;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace ArturRios.Cerberus.Data.Profiles;

public sealed class ProfileAssociationStore(IDbContextFactory<AppDbContext> factory):IProfileAssociationStore
{
    public async Task<VaultResult<ProfileAssociationDetails>> SetAsync(ProfileAssociationRequest request,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        var input=request.Input;
        if(request.ProfileId==Guid.Empty || input?.IsValid()!=true)return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(cancellationToken);
            await using var tx=await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE",cancellationToken);
            var account=await db.Accounts.AsNoTracking().Where(x=>x.HeimdallPublicId==request.Actor)
                .Select(x=>new{x.Id,x.PublicId,x.State,x.PolicyRevision,x.RevocationGeneration,x.RenewalEnabled}).SingleOrDefaultAsync(cancellationToken);
            if(account is null || account.State!=AccountState.Active || await db.TerminalErasures.AnyAsync(x=>x.ResourceId==account.PublicId,cancellationToken))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",cancellationToken);
            var session=await db.VaultAccessSessions.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==account.Id && x.HandleVerifier==request.AccessVerifier,cancellationToken);
            bool Permitted(DateTimeOffset now)=>session is not null && !session.Revoked && session.PolicyRevision>0 && session.RevocationGeneration>0
                && session.PolicyRevision==account.PolicyRevision && session.RevocationGeneration==account.RevocationGeneration
                && session.IssuedAt<=now && (account.RenewalEnabled?session.ExpiresAt>now:session.ExpiresAt is null);
            if(!Permitted(await Now(db,cancellationToken)))return new(Error:"vault_access_denied");
            if(session!.ProfileId is not null && !await db.Profiles.AnyAsync(p=>p.Id==session.ProfileId && p.AccountId==account.Id && p.DeletedAt==null
                && !db.TerminalErasures.Any(e=>e.ResourceId==p.PublicId),cancellationToken))return new(Error:"vault_access_denied");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.profile WHERE account_id={account.Id} AND public_id={request.ProfileId} FOR UPDATE",cancellationToken);
            if(!Permitted(await Now(db,cancellationToken)))return new(Error:"vault_access_denied");
            var profile=await db.Profiles.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==account.Id && x.PublicId==request.ProfileId,cancellationToken);
            if(profile is null || profile.DeletedAt is not null || session.ProfileId is not null && session.ProfileId!=profile.Id
                || await db.TerminalErasures.AnyAsync(x=>x.ResourceId==profile.PublicId,cancellationToken))return new(Error:"not_found");
            if(profile.Revision is <=0 or >ProtocolBinary.MaxInteger || profile.ServerSequence is <=0 or >ProtocolBinary.MaxInteger)return new(Error:"persistence_unavailable");
            if(profile.Revision!=input.ExpectedRevision || profile.Revision==ProtocolBinary.MaxInteger)return new(Error:"revision_conflict");
            var resolved=await ProfileAssociationResolver.ResolveAsync(db,account.Id,request.Actor,input.RecordIds,input.FolderIds,input.CollectionIds,cancellationToken);
            if(!Permitted(await Now(db,cancellationToken)))return new(Error:"vault_access_denied");
            if(resolved.Error is not null)return new(Error:resolved.Error);
            if(session.ProfileId is not null)
            {
                if(await db.ProfileRecords.CountAsync(x=>x.ProfileId==profile.Id && resolved.Records.Contains(x.RecordId),cancellationToken)!=resolved.Records.Length
                    || await db.ProfileFolders.CountAsync(x=>x.ProfileId==profile.Id && resolved.Folders.Contains(x.FolderId),cancellationToken)!=resolved.Folders.Length
                    || await db.ProfileCollections.CountAsync(x=>x.ProfileId==profile.Id && resolved.Collections.Contains(x.CollectionId),cancellationToken)!=resolved.Collections.Length)return new(Error:"not_found");
            }
            var changed=await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cerberus.profile AS p SET revision=p.revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={Guid.NewGuid()}
                WHERE p.id={profile.Id} AND p.account_id={account.Id} AND p.revision={input.ExpectedRevision} AND p.deleted_at IS NULL
                    AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE e.resource_id=p.public_id)
                    AND EXISTS(SELECT 1 FROM cerberus.account a JOIN cerberus.vault_access_session s ON s.account_id=a.id
                        WHERE a.id=p.account_id AND a.heimdall_public_id={request.Actor} AND a.state={AccountState.Active}
                        AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE e.resource_id=a.public_id)
                        AND s.handle_verifier={request.AccessVerifier} AND NOT s.revoked AND s.policy_revision>0 AND s.revocation_generation>0
                        AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation AND s.issued_at<=statement_timestamp()
                        AND ((a.renewal_enabled AND s.expires_at>statement_timestamp()) OR (NOT a.renewal_enabled AND s.expires_at IS NULL))
                        AND (s.profile_id IS NULL OR (s.profile_id=p.id
                            AND NOT EXISTS(SELECT 1 FROM unnest({input.RecordIds}) requested(id) WHERE NOT EXISTS(
                                SELECT 1 FROM cerberus.profile_record pr JOIN cerberus.record r ON r.id=pr.record_id WHERE pr.profile_id=p.id AND r.public_id=requested.id))
                            AND NOT EXISTS(SELECT 1 FROM unnest({input.FolderIds}) requested(id) WHERE NOT EXISTS(
                                SELECT 1 FROM cerberus.profile_folder pf JOIN cerberus.folder f ON f.id=pf.folder_id WHERE pf.profile_id=p.id AND f.public_id=requested.id))
                            AND NOT EXISTS(SELECT 1 FROM unnest({input.CollectionIds}) requested(id) WHERE NOT EXISTS(
                                SELECT 1 FROM cerberus.profile_collection pc JOIN cerberus.collection c ON c.id=pc.collection_id WHERE pc.profile_id=p.id AND c.public_id=requested.id)))))
                    AND (SELECT count(*) FROM cerberus.record r WHERE r.public_id=ANY({input.RecordIds}) AND r.account_id=p.account_id AND r.deleted_at IS NULL
                        AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE e.resource_id=r.public_id))=cardinality({input.RecordIds})
                    AND (SELECT count(*) FROM cerberus.folder f WHERE f.public_id=ANY({input.FolderIds}) AND f.account_id=p.account_id AND f.deleted_at IS NULL
                        AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE e.resource_id=f.public_id))=cardinality({input.FolderIds})
                    AND (SELECT count(*) FROM cerberus.collection c JOIN cerberus.account o ON o.id=c.account_id
                        WHERE c.public_id=ANY({input.CollectionIds}) AND c.deleted_at IS NULL AND o.state={AccountState.Active}
                        AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE e.resource_id=c.public_id OR e.resource_id=o.public_id)
                        AND (c.account_id=p.account_id OR EXISTS(SELECT 1 FROM cerberus.collection_grant g WHERE g.collection_id=c.id
                            AND g.recipient_account_id=p.account_id AND g.state={CollectionGrantState.Active}
                            AND (g.access={CollectionGrantAccess.ReadOnly} OR g.access={CollectionGrantAccess.ReadWrite})
                            AND g.revision>0 AND g.revision<={ProtocolBinary.MaxInteger}
                            AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE e.resource_id=g.public_id))))=cardinality({input.CollectionIds})
                """,cancellationToken);
            if(changed!=1)return new(Error:Permitted(await Now(db,cancellationToken))?"not_found":"vault_access_denied");
            var metadata=await db.Profiles.AsNoTracking().Where(x=>x.Id==profile.Id).Select(x=>new{x.Revision,x.ServerSequence}).SingleAsync(cancellationToken);
            if(metadata.Revision!=profile.Revision+1 || metadata.ServerSequence<=profile.ServerSequence || metadata.ServerSequence>ProtocolBinary.MaxInteger)return new(Error:"persistence_unavailable");
            await db.ProfileRecords.Where(x=>x.ProfileId==profile.Id).ExecuteDeleteAsync(cancellationToken);
            await db.ProfileFolders.Where(x=>x.ProfileId==profile.Id).ExecuteDeleteAsync(cancellationToken);
            await db.ProfileCollections.Where(x=>x.ProfileId==profile.Id).ExecuteDeleteAsync(cancellationToken);
            db.ProfileRecords.AddRange(resolved.Records.Select(id=>new ProfileRecord{AccountId=account.Id,ProfileId=profile.Id,RecordId=id}));
            db.ProfileFolders.AddRange(resolved.Folders.Select(id=>new ProfileFolder{AccountId=account.Id,ProfileId=profile.Id,FolderId=id}));
            db.ProfileCollections.AddRange(resolved.Collections.Select(id=>new ProfileCollection{ProfileId=profile.Id,CollectionId=id}));
            await db.SaveChangesAsync(cancellationToken);await tx.CommitAsync(cancellationToken);
            return new(new(profile.PublicId,metadata.Revision,metadata.ServerSequence,input.RecordIds.Order().ToArray(),input.FolderIds.Order().ToArray(),input.CollectionIds.Order().ToArray()));
        }
        catch(PostgresException e) when(e.SqlState=="2200H"){return new(Error:"revision_conflict");}
        catch(Exception e) when(e is DbException or DbUpdateException or TimeoutException
            || e is InvalidOperationException {InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested){return new(Error:"persistence_unavailable");}
    }
    private static Task<DateTimeOffset> Now(AppDbContext db,CancellationToken ct)=>db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
}
