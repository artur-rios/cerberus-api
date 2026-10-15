using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Erasure;

public sealed class AccountExpiryHandler(IDbContextFactory<AppDbContext> factory,IErasureLedger ledger):IRetentionHandler
{
    public string Kind=>"account-purge";
    public async Task ExecuteAsync(RetentionClaim claim,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var id=RetentionExpiry.Identity(claim,Kind);
        await using(var db=await factory.CreateDbContextAsync(ct))
        {
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            // All account roots precede the original job, including foreign grant owners.
            await AccountErasure.LockAsync(db,id,ct);await RecordErasure.RequireClaimAsync(db,claim,ct);
            var owner=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.PublicId==id,ct);
            if(owner is null)
            {
                if(!await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==id,ct))throw new InvalidOperationException("Account expiry owner is missing.");
            }
            else
            {
                var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
                if(owner.State!=AccountState.ClosurePending||!RetentionExpiry.Due(owner.ClosureRequestedAt,owner.ClosurePurgeAt,now)
                    ||owner.Revision is <=0 or >ProtocolBinary.MaxInteger||owner.RevocationGeneration is <=0 or >ProtocolBinary.MaxInteger
                    ||owner.PolicyRevision is <=0 or >ProtocolBinary.MaxInteger)throw new InvalidOperationException("Invalid account expiry state.");
                var intent=await db.Database.SqlQuery<DateTimeOffset>($"""
                    INSERT INTO cerberus.terminal_erasure AS t(resource_kind,resource_id,deleted_at)
                    SELECT 'account',a.public_id,statement_timestamp() FROM cerberus.account a
                    WHERE a.id={owner.Id} AND a.public_id={id} AND a.state={AccountState.ClosurePending}
                      AND a.revision={owner.Revision} AND a.revocation_generation={owner.RevocationGeneration} AND a.policy_revision={owner.PolicyRevision}
                      AND a.closure_requested_at={owner.ClosureRequestedAt!.Value} AND a.closure_purge_at={owner.ClosurePurgeAt!.Value}
                      AND a.closure_purge_at<=statement_timestamp() AND a.closure_purge_at-a.closure_requested_at=interval '720 hours'
                      AND EXISTS(SELECT FROM cerberus.retention_work_item w WHERE w.public_id={claim.WorkId} AND w.operation_key={claim.OperationKey}
                        AND w.claim_token={claim.Token} AND w.completed_at IS NULL AND w.claim_expires_at>statement_timestamp())
                      AND NOT EXISTS(SELECT FROM cerberus.vault_access_session v WHERE v.account_id=a.id AND NOT v.revoked)
                      AND NOT EXISTS(SELECT FROM cerberus.vault_unlock_challenge v WHERE v.account_id=a.id AND NOT v.consumed)
                      AND NOT EXISTS(SELECT FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id
                        WHERE (c.account_id=a.id OR g.recipient_account_id=a.id)
                          AND (g.state<>1 OR octet_length(g.recipient_key_envelope)<>0 OR g.public_id='00000000-0000-0000-0000-000000000000'::uuid
                            OR g.revision NOT BETWEEN 1 AND 9007199254740991))
                      AND NOT EXISTS(SELECT FROM cerberus.software_grant g WHERE g.account_id=a.id
                        AND (g.state<>1 OR octet_length(g.recipient_key)<>0 OR g.public_id='00000000-0000-0000-0000-000000000000'::uuid
                          OR g.revision NOT BETWEEN 1 AND 9007199254740991))
                      AND NOT EXISTS(SELECT FROM cerberus.software_grant_record g WHERE g.account_id=a.id)
                      AND NOT EXISTS(SELECT FROM cerberus.software_grant_collection g WHERE g.account_id=a.id)
                    ON CONFLICT(resource_kind,resource_id) DO UPDATE SET deleted_at=LEAST(t.deleted_at,EXCLUDED.deleted_at)
                    RETURNING deleted_at AS "Value"
                    """).ToListAsync(ct);
                if(intent.Count!=1)throw new InvalidOperationException("Account expiry admission changed.");
                await RecordErasure.RequireClaimAsync(db,claim,ct);
                await AccountErasure.AddIntentAsync(db,id,intent[0],ct,claim);
                await AccountErasure.EntriesAsync(db,id,ct);
            }
            await RecordErasure.RequireClaimAsync(db,claim,ct);await tx.CommitAsync(ct);
        }
        await AccountErasureExecution.ExecuteAsync(factory,ledger,claim,id,ct);
    }
}
