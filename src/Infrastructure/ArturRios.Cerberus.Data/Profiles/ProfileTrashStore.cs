using System.Data.Common;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace ArturRios.Cerberus.Data.Profiles;

public sealed class ProfileTrashStore(IDbContextFactory<AppDbContext> factory) : IProfileTrashStore
{
    public async Task<VaultResult<ProfileTrashDetails>> TrashAsync(ProfileTrashRequest request,CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(request.ProfileId==Guid.Empty || request.ExpectedRevision is <=0 or >ProtocolBinary.MaxInteger)return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(cancellationToken);
            await using var tx=await db.Database.BeginTransactionAsync(cancellationToken);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE",cancellationToken);
            var account=await db.Accounts.AsNoTracking().Where(x=>x.HeimdallPublicId==request.Actor)
                .Select(x=>new{x.Id,x.PublicId,x.State,x.PolicyRevision,x.RevocationGeneration,x.RenewalEnabled}).SingleOrDefaultAsync(cancellationToken);
            if(account is null || account.State!=AccountState.Active || await db.TerminalErasures.AnyAsync(x=>(x.ResourceKind == "account" && x.ResourceId == account.PublicId),cancellationToken))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",cancellationToken);
            var session=await db.VaultAccessSessions.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==account.Id && x.HandleVerifier==request.AccessVerifier,cancellationToken);
            var now=await Now(db,cancellationToken);
            if(session is null || session.Revoked || session.PolicyRevision<=0 || session.RevocationGeneration<=0
                || session.PolicyRevision!=account.PolicyRevision || session.RevocationGeneration!=account.RevocationGeneration
                || session.IssuedAt>now || (account.RenewalEnabled?session.ExpiresAt is null || session.ExpiresAt<=now:session.ExpiresAt is not null))return new(Error:"vault_access_denied");
            if(session.ProfileId is not null && !await db.Profiles.AnyAsync(p=>p.Id==session.ProfileId && p.AccountId==account.Id && p.DeletedAt==null
                && !db.TerminalErasures.Any(e=>(e.ResourceKind == "profile" && e.ResourceId == p.PublicId)),cancellationToken))return new(Error:"vault_access_denied");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.profile WHERE account_id={account.Id} AND public_id={request.ProfileId} FOR UPDATE",cancellationToken);
            // A profile lock can outlive the access window. Recheck before disclosing revision state.
            now=await Now(db,cancellationToken);
            if(session.IssuedAt>now || account.RenewalEnabled && session.ExpiresAt<=now)return new(Error:"vault_access_denied");
            var profile=await db.Profiles.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==account.Id && x.PublicId==request.ProfileId,cancellationToken);
            if(profile is null || profile.DeletedAt is not null || session.ProfileId is not null && session.ProfileId!=profile.Id
                || await db.TerminalErasures.AnyAsync(x=>(x.ResourceKind == "profile" && x.ResourceId == profile.PublicId),cancellationToken))return new(Error:"not_found");
            if(profile.Revision is <=0 or >ProtocolBinary.MaxInteger || profile.ServerSequence is <=0 or >ProtocolBinary.MaxInteger)return new(Error:"persistence_unavailable");
            if(profile.Revision!=request.ExpectedRevision || profile.Revision==ProtocolBinary.MaxInteger)return new(Error:"revision_conflict");
            var changed=await db.Database.ExecuteSqlInterpolatedAsync($"""
                UPDATE cerberus.profile AS p SET deleted_at=statement_timestamp(),purge_at=statement_timestamp()+interval '720 hours',
                    revision=p.revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp={Guid.NewGuid()}
                WHERE p.id={profile.Id} AND p.account_id={account.Id} AND p.revision={request.ExpectedRevision}
                    AND p.deleted_at IS NULL AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE (e.resource_kind='profile' AND e.resource_id=p.public_id))
                    AND EXISTS(SELECT 1 FROM cerberus.account a JOIN cerberus.vault_access_session s ON s.account_id=a.id
                        WHERE a.id=p.account_id AND a.heimdall_public_id={request.Actor} AND a.state={AccountState.Active}
                        AND NOT EXISTS(SELECT 1 FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=a.public_id))
                        AND s.handle_verifier={request.AccessVerifier} AND NOT s.revoked
                        AND s.policy_revision>0 AND s.revocation_generation>0
                        AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
                        AND s.issued_at<=statement_timestamp()
                        AND ((a.renewal_enabled AND s.expires_at>statement_timestamp()) OR (NOT a.renewal_enabled AND s.expires_at IS NULL))
                        AND (s.profile_id IS NULL OR s.profile_id=p.id))
                """,cancellationToken);
            if(changed!=1)return new(Error:"vault_access_denied");
            var row=await db.Profiles.AsNoTracking().SingleAsync(x=>x.Id==profile.Id,cancellationToken);
            if(row.ServerSequence<=profile.ServerSequence || row.ServerSequence>ProtocolBinary.MaxInteger
                || row.DeletedAt is null || row.PurgeAt-row.DeletedAt!=TimeSpan.FromDays(30))return new(Error:"persistence_unavailable");
            var operation=new TrashOperation{AccountId=account.Id,RootResourceKind="profile",RootResourceId=profile.PublicId,DeletedAt=row.DeletedAt.Value,PurgeAt=row.PurgeAt!.Value};
            db.TrashOperations.Add(operation);
            await db.SaveChangesAsync(cancellationToken);
            // Capture stored memberships, including links currently hidden by a revoked grant.
            var records=await (from link in db.ProfileRecords join r in db.Records on link.RecordId equals r.Id
                where link.ProfileId==profile.Id orderby r.PublicId select r.PublicId).ToArrayAsync(cancellationToken);
            var folders=await (from link in db.ProfileFolders join f in db.Folders on link.FolderId equals f.Id
                where link.ProfileId==profile.Id orderby f.PublicId select f.PublicId).ToArrayAsync(cancellationToken);
            var collections=await (from link in db.ProfileCollections join c in db.Collections on link.CollectionId equals c.Id
                where link.ProfileId==profile.Id orderby c.PublicId select c.PublicId).ToArrayAsync(cancellationToken);
            await db.ProfileRecords.Where(x=>x.ProfileId==profile.Id).ExecuteDeleteAsync(cancellationToken);
            await db.ProfileFolders.Where(x=>x.ProfileId==profile.Id).ExecuteDeleteAsync(cancellationToken);
            await db.ProfileCollections.Where(x=>x.ProfileId==profile.Id).ExecuteDeleteAsync(cancellationToken);
            db.TrashEntries.Add(new(){OperationId=operation.Id,ResourceKind="profile",ResourceId=profile.PublicId,
                AssociationSnapshot=JsonSerializer.SerializeToUtf8Bytes(new ProfileAssociationSnapshot(records,folders,collections),new JsonSerializerOptions(JsonSerializerDefaults.Web))});
            db.RetentionWorkItems.Add(new RetentionWorkItem{OperationKey="trash/"+operation.PublicId,DueAt=operation.PurgeAt});
            await db.VaultAccessSessions.Where(x=>x.AccountId==account.Id && x.ProfileId==profile.Id && !x.Revoked)
                .ExecuteUpdateAsync(x=>x.SetProperty(s=>s.Revoked,true),cancellationToken);
            await db.SaveChangesAsync(cancellationToken);
            await tx.CommitAsync(cancellationToken);
            return new(new(profile.PublicId,operation.PublicId,row.Revision,row.ServerSequence,operation.DeletedAt,operation.PurgeAt));
        }
        catch(PostgresException e) when(e.SqlState=="2200H"){return new(Error:"revision_conflict");}
        catch(Exception e) when(e is DbException or DbUpdateException or TimeoutException
            || e is InvalidOperationException {InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
        catch(OperationCanceledException) when(!cancellationToken.IsCancellationRequested){return new(Error:"persistence_unavailable");}
    }
    private static Task<DateTimeOffset> Now(AppDbContext db,CancellationToken ct)=>db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
}
