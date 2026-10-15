using System.Data.Common;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Accounts;

public sealed class AccountClosureRecoveryStore(IDbContextFactory<AppDbContext> factory):IAccountClosureRecoveryStore
{
    public async Task<AccountClosureRecoveryResult> ReadAsync(AccountClosureRecoveryRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);
            // One current statement captures pending ownership, typed erasure and job metadata.
            var rows=await db.Database.SqlQuery<Row>($"""
                SELECT a.public_id AS "account_id",a.revision AS "revision",a.revocation_generation AS "generation",a.policy_revision AS "policy",
                  a.state AS "state",a.closure_requested_at AS "start",a.closure_purge_at AS "deadline",statement_timestamp() AS "now",
                  EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id) AS "erased",
                  w.public_id AS "work_id",w.due_at AS "due",w.completed_at AS "completed"
                FROM cerberus.account a LEFT JOIN cerberus.retention_work_item w ON w.operation_key='account-purge/'||a.public_id::text
                WHERE a.heimdall_public_id={request.Actor}
                """).ToListAsync(ct);
            var a=rows.SingleOrDefault();if(a is null||a.Erased||a.State!=(int)AccountState.ClosurePending)return new(Error:"authentication_required");
            if(!FreshAuthentication.IsFresh(request.AuthenticationIssuedAt,a.Now))return new(Error:"fresh_authentication_required");
            if(a.Start is not{}start||a.Deadline is not{}deadline||a.Policy is <=0 or >ProtocolBinary.MaxInteger
                ||!new AccountClosureDetails(a.AccountId,a.Revision,a.Generation,start,deadline,a.Now,true).IsValid())return new(Error:"persistence_unavailable");
            if(deadline<=a.Now)return new(Error:"closure_expired");
            if(a.WorkId is null||a.WorkId==Guid.Empty||a.Due!=deadline||a.Completed is not null)return new(Error:"persistence_unavailable");
            var details=new AccountClosureRecoveryDetails(a.AccountId,a.Revision,deadline,a.Now);
            return details.IsValid()?new(details):new(Error:"persistence_unavailable");
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e)when(e is DbException or DbUpdateException or TimeoutException
            ||e is InvalidOperationException{InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
    }
    private sealed class Row
    {
        public Guid AccountId{get;set;}public long Revision{get;set;}public long Generation{get;set;}public long Policy{get;set;}
        public int State{get;set;}public DateTimeOffset? Start{get;set;}public DateTimeOffset? Deadline{get;set;}public DateTimeOffset Now{get;set;}
        public bool Erased{get;set;}public Guid? WorkId{get;set;}public DateTimeOffset? Due{get;set;}public DateTimeOffset? Completed{get;set;}
    }
}
