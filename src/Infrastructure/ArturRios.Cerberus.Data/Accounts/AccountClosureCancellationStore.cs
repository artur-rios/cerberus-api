using System.Data.Common;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ArturRios.Cerberus.Data.Accounts;

public sealed class AccountClosureCancellationStore(IDbContextFactory<AppDbContext> factory):IAccountClosureCancellationStore
{
    public async Task<AccountClosureCancellationResult> CancelAsync(AccountClosureCancellationRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(!new AccountClosureCancellationInput(request.ExpectedRevision).IsValid())return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
            var initial=await Actor(db,request.Actor,ct);if(initial is null||await Erased(db,initial,ct))return new(Error:"not_found");
            var ids=(await Connections(db,initial.Id,ct)).Append(initial.Id).Distinct().ToArray();
            var roots=await db.Accounts.AsNoTracking().Where(x=>ids.Contains(x.Id)).Select(x=>x.PublicId).ToArrayAsync(ct);
            if(roots.Length!=ids.Length||roots.Any(x=>x==Guid.Empty))return new(Error:"persistence_unavailable");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE public_id=ANY({roots}) ORDER BY public_id FOR UPDATE",ct);
            var owner=await Actor(db,request.Actor,ct);var now=await Now(db,ct);
            if(owner is null||owner.Id!=initial.Id||await Erased(db,owner,ct))return new(Error:"not_found");
            var error=Authority(owner,request,now);if(error is not null)return new(Error:error);
            if(!(await Connections(db,owner.Id,ct)).Append(owner.Id).ToHashSet().SetEquals(ids))return new(Error:"revision_conflict");
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                SELECT FROM cerberus.collection c WHERE c.account_id={owner.Id}
                  OR EXISTS(SELECT FROM cerberus.collection_grant g WHERE g.collection_id=c.id AND g.recipient_account_id={owner.Id})
                ORDER BY c.public_id FOR NO KEY UPDATE OF c
                """,ct);
            now=await Now(db,ct);error=Authority(owner,request,now);if(error is not null)return new(Error:error);
            var key="account-purge/"+owner.PublicId;
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.retention_work_item WHERE operation_key={key} FOR UPDATE",ct);
            var work=await db.RetentionWorkItems.AsNoTracking().SingleOrDefaultAsync(x=>x.OperationKey==key,ct);
            if(work is null||work.PublicId==Guid.Empty||work.DueAt!=owner.ClosurePurgeAt||work.CompletedAt is not null
                ||work.Attempts is <0 or int.MaxValue||(work.ClaimToken is null)!=(work.ClaimExpiresAt is null)
                ||work.ClaimToken==Guid.Empty||work.ClaimExpiresAt is{}expiry&&!Utc(expiry))return new(Error:"persistence_unavailable");
            if(!await Reduced(db,owner.Id,ct))return new(Error:"persistence_unavailable");
            var collateral=await AccountLifecycleSnapshot.ReadAsync(db,owner.Id,ids,[],false,ct);
            var workJson=await db.Database.SqlQuery<string>($"SELECT to_jsonb(w)::text AS \"Value\" FROM cerberus.retention_work_item w WHERE w.id={work.Id}").SingleAsync(ct);
            var stamp=Guid.NewGuid();
            var parameters=new object[]{new NpgsqlParameter("actor",request.Actor),new NpgsqlParameter("owner",owner.Id),
                new NpgsqlParameter("public",owner.PublicId),new NpgsqlParameter("revision",owner.Revision),new NpgsqlParameter("generation",owner.RevocationGeneration),
                new NpgsqlParameter("policy",owner.PolicyRevision),new NpgsqlParameter("start",owner.ClosureRequestedAt!.Value),new NpgsqlParameter("deadline",owner.ClosurePurgeAt!.Value),
                new NpgsqlParameter("iat",request.AuthenticationIssuedAt),new NpgsqlParameter("stamp",stamp),new NpgsqlParameter("work_id",work.Id),
                new NpgsqlParameter("pending",(int)AccountState.ClosurePending),new NpgsqlParameter("active",(object)(int)AccountState.Active),
                new NpgsqlParameter("work",NpgsqlDbType.Jsonb){Value=workJson}};
            // The single database-time authorization point also fences the due job.
            // A worker with an old claim cannot purge the restored active account.
            var writes=await db.Database.SqlQueryRaw<Written>("""
                WITH authorized AS MATERIALIZED(SELECT statement_timestamp() at), cancelled AS(
                  UPDATE cerberus.account a SET state=@active,revision=a.revision+1,revocation_generation=a.revocation_generation+1,
                    closure_requested_at=NULL,closure_purge_at=NULL,concurrency_stamp=@stamp
                  FROM authorized t WHERE a.id=@owner AND a.public_id=@public AND a.heimdall_public_id=@actor
                    AND a.state=@pending AND a.revision=@revision AND a.revocation_generation=@generation AND a.policy_revision=@policy
                    AND a.closure_requested_at=@start AND a.closure_purge_at=@deadline AND t.at<a.closure_purge_at
                    AND @iat>0 AND @iat<=floor(extract(epoch FROM t.at))::bigint AND floor(extract(epoch FROM t.at))::bigint-@iat<60
                    AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)
                  RETURNING a.id,t.at authorized_at)
                UPDATE cerberus.retention_work_item w SET completed_at=c.authorized_at,claim_token=NULL,claim_expires_at=NULL,concurrency_stamp=@stamp
                FROM cancelled c WHERE w.id=@work_id AND to_jsonb(w)=@work
                RETURNING c.authorized_at AS "cancelled_at",w.completed_at AS "completed_at"
                """,parameters).ToListAsync(ct);
            if(writes.Count!=1)
            {
                var current=await Actor(db,request.Actor,ct);
                if(current?.ConcurrencyStamp==stamp)return new(Error:"persistence_unavailable");
                return new(Error:current is null?"not_found":Authority(current,request,await Now(db,ct))??"persistence_unavailable");
            }
            var written=writes[0];var post=await Actor(db,request.Actor,ct);var postWork=await db.RetentionWorkItems.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==work.Id,ct);
            now=await Now(db,ct);
            if(written.CancelledAt!=written.CompletedAt||post is null||post.Id!=owner.Id||post.PublicId!=owner.PublicId||post.HeimdallPublicId!=owner.HeimdallPublicId
                ||post.State!=AccountState.Active||post.Revision!=owner.Revision+1||post.RevocationGeneration!=owner.RevocationGeneration+1
                ||post.ClosureRequestedAt is not null||post.ClosurePurgeAt is not null||post.ConcurrencyStamp!=stamp||await Erased(db,post,ct)
                ||postWork is null||postWork.PublicId!=work.PublicId||postWork.OperationKey!=key||postWork.DueAt!=work.DueAt||postWork.Attempts!=work.Attempts
                ||postWork.CompletedAt!=written.CancelledAt||postWork.ClaimToken is not null||postWork.ClaimExpiresAt is not null||postWork.ConcurrencyStamp!=stamp
                ||await AccountLifecycleSnapshot.ReadAsync(db,owner.Id,ids,[],false,ct)!=collateral||!await Reduced(db,owner.Id,ct))return new(Error:"persistence_unavailable");
            var details=new AccountClosureCancellationDetails(post.PublicId,post.Revision,post.RevocationGeneration,written.CancelledAt,now);
            if(!details.IsValid())return new(Error:"persistence_unavailable");
            await tx.CommitAsync(ct);return new(details);
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(DbUpdateConcurrencyException){return new(Error:"revision_conflict");}
        catch(Exception e)when(e is DbException or DbUpdateException or TimeoutException
            ||e is InvalidOperationException{InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
    }
    private static string? Authority(Account a,AccountClosureCancellationRequest r,DateTimeOffset now)
    {
        if(!FreshAuthentication.IsFresh(r.AuthenticationIssuedAt,now))return "fresh_authentication_required";
        if(a.PublicId==Guid.Empty||!Safe(a.Revision)||!Safe(a.RevocationGeneration)||!Safe(a.PolicyRevision))return "persistence_unavailable";
        if(a.State==AccountState.Active)return a.ClosureRequestedAt is null&&a.ClosurePurgeAt is null?"revision_conflict":"persistence_unavailable";
        if(a.State!=AccountState.ClosurePending||a.ClosureRequestedAt is not{}start||a.ClosurePurgeAt is not{}end
            ||!new AccountClosureDetails(a.PublicId,a.Revision,a.RevocationGeneration,start,end,now,true).IsValid())return "persistence_unavailable";
        if(end<=now)return "closure_expired";
        return a.Revision!=r.ExpectedRevision||a.Revision==ProtocolBinary.MaxInteger||a.RevocationGeneration==ProtocolBinary.MaxInteger?"revision_conflict":null;
    }
    private static async Task<bool> Reduced(AppDbContext db,long owner,CancellationToken ct)=>
        !await db.VaultAccessSessions.AnyAsync(x=>x.AccountId==owner&&!x.Revoked,ct)
        &&!await db.VaultUnlockChallenges.AnyAsync(x=>x.AccountId==owner&&!x.Consumed,ct)
        &&!await (from g in db.CollectionGrants join c in db.Collections on g.CollectionId equals c.Id
                 where (c.AccountId==owner||g.RecipientAccountId==owner)&&(g.State!=CollectionGrantState.Revoked||g.RecipientKeyEnvelope.Length!=0||g.PublicId==Guid.Empty||g.Revision<=0||g.Revision>ProtocolBinary.MaxInteger)select g.Id).AnyAsync(ct)
        &&!await db.SoftwareGrants.AnyAsync(x=>x.AccountId==owner&&(x.State!=SoftwareGrantState.Revoked||x.RecipientKey.Length!=0||x.PublicId==Guid.Empty||x.Revision<=0||x.Revision>ProtocolBinary.MaxInteger),ct)
        &&!await db.SoftwareGrantRecords.AnyAsync(x=>x.AccountId==owner,ct)&&!await db.SoftwareGrantCollections.AnyAsync(x=>x.AccountId==owner,ct);
    private static bool Safe(long x)=>x is >0 and <=ProtocolBinary.MaxInteger;
    private static bool Utc(DateTimeOffset x)=>x.Offset==TimeSpan.Zero&&x.Ticks>=10&&x.Ticks%10==0;
    private static Task<Account?> Actor(AppDbContext db,Guid actor,CancellationToken ct)=>db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.HeimdallPublicId==actor,ct);
    private static Task<bool> Erased(AppDbContext db,Account a,CancellationToken ct)=>db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==a.PublicId,ct);
    private static Task<DateTimeOffset> Now(AppDbContext db,CancellationToken ct)=>db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
    private static Task<long[]> Connections(AppDbContext db,long owner,CancellationToken ct)=>(from g in db.CollectionGrants join c in db.Collections on g.CollectionId equals c.Id
        where c.AccountId==owner||g.RecipientAccountId==owner select c.AccountId==owner?g.RecipientAccountId:c.AccountId).Distinct().ToArrayAsync(ct);
    private sealed class Written{public DateTimeOffset CancelledAt{get;set;}public DateTimeOffset CompletedAt{get;set;}}
}
