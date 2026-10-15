using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Operations;

public sealed class RetentionMaintenance(IDbContextFactory<AppDbContext> factory):IRetentionMaintenance
{
    public async Task<RetentionPreparationResult> PrepareAsync(int pageSize,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();ArgumentOutOfRangeException.ThrowIfNegativeOrZero(pageSize);
        await using var read=await factory.CreateDbContextAsync(ct);
        await RetentionMaintenanceRights.RequireAsync(read,ct);
        var trash=await read.Database.SqlQuery<Candidate>($"""
            SELECT o.account_id,o.public_id FROM cerberus.trash_operation o JOIN cerberus.account a ON a.id=o.account_id
            WHERE a.state IN(0,1) AND o.root_resource_kind IN('record','folder','collection','profile')
              AND o.root_resource_id<>'00000000-0000-0000-0000-000000000000'::uuid AND o.deleted_at>='epoch'::timestamptz
              AND CASE WHEN isfinite(o.deleted_at) AND isfinite(o.purge_at) THEN o.purge_at-o.deleted_at=interval '720 hours' ELSE FALSE END
              AND o.purge_at<=statement_timestamp()
              AND NOT EXISTS(SELECT FROM cerberus.retention_work_item w WHERE w.operation_key='trash/'||o.public_id::text)
            ORDER BY o.purge_at,o.public_id LIMIT {pageSize}
            """).ToListAsync(ct);
        var scheduled=0;
        foreach(var candidate in trash)scheduled+=await ScheduleTrashAsync(candidate,ct);
        if(scheduled<pageSize)
        {
            var accounts=await read.Database.SqlQuery<Candidate>($"""
                SELECT a.id account_id,a.public_id FROM cerberus.account a WHERE a.state={AccountState.ClosurePending}
                  AND a.revision BETWEEN 1 AND 9007199254740991 AND a.revocation_generation BETWEEN 1 AND 9007199254740991
                  AND a.policy_revision BETWEEN 1 AND 9007199254740991 AND a.closure_requested_at>='epoch'::timestamptz
                  AND CASE WHEN isfinite(a.closure_requested_at) AND isfinite(a.closure_purge_at)
                    THEN a.closure_purge_at-a.closure_requested_at=interval '720 hours' ELSE FALSE END
                  AND a.closure_purge_at<=statement_timestamp()
                  AND NOT EXISTS(SELECT FROM cerberus.retention_work_item w WHERE w.operation_key='account-purge/'||a.public_id::text)
                ORDER BY a.closure_purge_at,a.public_id LIMIT {pageSize-scheduled}
                """).ToListAsync(ct);
            foreach(var candidate in accounts)scheduled+=await ScheduleAccountAsync(candidate,ct);
        }
        // Busy downloads keep their stream lock; cleanup skips them and retries
        // later. Upload idempotency rows are never an age-based cleanup target.
        var streams=await read.Database.ExecuteSqlInterpolatedAsync($"""
            WITH expired AS MATERIALIZED(
              SELECT id FROM cerberus.sync_stream WHERE expires_at<=statement_timestamp()
              ORDER BY expires_at,public_id LIMIT {pageSize} FOR UPDATE SKIP LOCKED)
            DELETE FROM cerberus.sync_stream s WHERE s.id IN(SELECT id FROM expired) AND s.expires_at<=statement_timestamp()
            """,ct);
        return new(scheduled,streams);
    }
    private async Task<int> ScheduleTrashAsync(Candidate candidate,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={candidate.AccountId} FOR SHARE",ct);
        var key="trash/"+candidate.PublicId;
        var changed=await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cerberus.retention_work_item(public_id,operation_key,due_at,claim_token,claim_expires_at,completed_at,attempts,concurrency_stamp)
            SELECT {Guid.NewGuid()},{key},o.purge_at,NULL,NULL,NULL,0,{Guid.NewGuid()}
            FROM cerberus.trash_operation o JOIN cerberus.account a ON a.id=o.account_id
            WHERE o.public_id={candidate.PublicId} AND o.account_id={candidate.AccountId} AND a.state IN(0,1)
              AND o.root_resource_kind IN('record','folder','collection','profile')
              AND o.root_resource_id<>'00000000-0000-0000-0000-000000000000'::uuid AND o.deleted_at>='epoch'::timestamptz
              AND CASE WHEN isfinite(o.deleted_at) AND isfinite(o.purge_at) THEN o.purge_at-o.deleted_at=interval '720 hours' ELSE FALSE END
              AND o.purge_at<=statement_timestamp()
            ON CONFLICT(operation_key) DO NOTHING
            """,ct);
        if(!await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey==key,ct))
        {
            var op=await db.TrashOperations.AsNoTracking().SingleOrDefaultAsync(x=>x.PublicId==candidate.PublicId&&x.AccountId==candidate.AccountId,ct);
            var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
            if(op is not null&&RetentionExpiry.Due(op.DeletedAt,op.PurgeAt,now)&&op.RootResourceId!=Guid.Empty
                &&op.RootResourceKind is "record" or "folder" or "collection" or "profile"
                &&await db.Accounts.AnyAsync(x=>x.Id==candidate.AccountId&&(x.State==AccountState.Active||x.State==AccountState.ClosurePending),ct))
                throw new InvalidOperationException("Trash expiry scheduling was not persisted.");
        }
        await tx.CommitAsync(ct);return changed;
    }
    private async Task<int> ScheduleAccountAsync(Candidate candidate,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={candidate.AccountId} FOR SHARE",ct);
        var key="account-purge/"+candidate.PublicId;
        var changed=await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO cerberus.retention_work_item(public_id,operation_key,due_at,claim_token,claim_expires_at,completed_at,attempts,concurrency_stamp)
            SELECT {Guid.NewGuid()},{key},a.closure_purge_at,NULL,NULL,NULL,0,{Guid.NewGuid()}
            FROM cerberus.account a WHERE a.id={candidate.AccountId} AND a.public_id={candidate.PublicId} AND a.state={AccountState.ClosurePending}
              AND a.revision BETWEEN 1 AND 9007199254740991 AND a.revocation_generation BETWEEN 1 AND 9007199254740991
              AND a.policy_revision BETWEEN 1 AND 9007199254740991 AND a.closure_requested_at>='epoch'::timestamptz
              AND CASE WHEN isfinite(a.closure_requested_at) AND isfinite(a.closure_purge_at)
                THEN a.closure_purge_at-a.closure_requested_at=interval '720 hours' ELSE FALSE END
              AND a.closure_purge_at<=statement_timestamp()
            ON CONFLICT(operation_key) DO NOTHING
            """,ct);
        if(!await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey==key,ct))
        {
            var owner=await db.Accounts.AsNoTracking().Where(x=>x.Id==candidate.AccountId&&x.PublicId==candidate.PublicId)
                .Select(x=>new{x.State,x.Revision,x.RevocationGeneration,x.PolicyRevision,x.ClosureRequestedAt,x.ClosurePurgeAt}).SingleOrDefaultAsync(ct);
            var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
            if(owner is not null&&owner.State==AccountState.ClosurePending&&RetentionExpiry.Due(owner.ClosureRequestedAt,owner.ClosurePurgeAt,now)
                &&owner.Revision is >0 and <=ProtocolBinary.MaxInteger&&owner.RevocationGeneration is >0 and <=ProtocolBinary.MaxInteger
                &&owner.PolicyRevision is >0 and <=ProtocolBinary.MaxInteger)throw new InvalidOperationException("Account expiry scheduling was not persisted.");
        }
        await tx.CommitAsync(ct);return changed;
    }
    private sealed class Candidate{public long AccountId{get;set;}public Guid PublicId{get;set;}}
}
