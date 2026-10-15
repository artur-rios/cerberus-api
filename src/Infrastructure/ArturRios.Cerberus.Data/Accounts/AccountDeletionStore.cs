using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Shared.Configuration;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Accounts;

public sealed class AccountDeletionStore(IDbContextFactory<AppDbContext> factory,IErasureLedger ledger,CerberusOptions options,TimeProvider clock):IAccountDeletionStore
{
    public async Task<AccountDeletionResult> DeleteAsync(AccountDeletionRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(!new AccountDeletionInput(request.ExpectedRevision).IsValid())return new(Error:"validation_failed");
        try
        {
            var intent=await PrepareAsync(request,ct);if(intent.Error is not null)return new(Error:intent.Error);
            var store=new RetentionWorkStore(factory);var claim=await store.TryClaimAsync(intent.WorkId,clock.GetUtcNow(),TimeSpan.Parse(options.RetentionInterval??throw new InvalidOperationException("Retention lease is not configured.")),ct);
            if(claim is null)return new(intent.Details);
            await new AccountPurgeHandler(factory,ledger).ExecuteAsync(claim,ct);
            if(!await store.TryCompleteAsync(claim,clock.GetUtcNow(),ct))return new(Error:"persistence_unavailable");
            return new(intent.Details! with{State="completed"});
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(DbUpdateConcurrencyException){return new(Error:"revision_conflict");}
        catch(Exception e)when(e is DbException or DbUpdateException or TimeoutException or IOException or InvalidOperationException or FormatException)
        {return new(Error:"persistence_unavailable");}
    }
    private sealed record Intent(AccountDeletionDetails? Details=null,Guid WorkId=default,string? Error=null);
    private async Task<Intent> PrepareAsync(AccountDeletionRequest request,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
        var owner=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.HeimdallPublicId==request.Actor,ct);
        if(owner is null||await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==owner.PublicId,ct))return new(Error:"not_found");
        var roots=await AccountErasure.LockAsync(db,owner.PublicId,ct);
        owner=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.HeimdallPublicId==request.Actor,ct);
        if(owner is null||await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==owner.PublicId,ct))return new(Error:"not_found");
        var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
        if(!FreshAuthentication.IsFresh(request.AuthenticationIssuedAt,now))return new(Error:"fresh_authentication_required");
        if(owner.State!=AccountState.Active||owner.Revision!=request.ExpectedRevision)return new(Error:"revision_conflict");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={owner.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",ct);
        var stamp=Guid.NewGuid();
        // This guard admits the account-wide session for the outer transaction.
        // The nested closure CAS separately rechecks fresh identity before reduction.
        var changed=await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE cerberus.account a SET concurrency_stamp={stamp}
            WHERE a.id={owner.Id} AND a.public_id={owner.PublicId} AND a.heimdall_public_id={request.Actor}
              AND a.state={AccountState.Active} AND a.revision={request.ExpectedRevision}
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)
              AND {request.AuthenticationIssuedAt}>0 AND {request.AuthenticationIssuedAt}<=floor(extract(epoch FROM statement_timestamp()))::bigint
              AND floor(extract(epoch FROM statement_timestamp()))::bigint-{request.AuthenticationIssuedAt}<60
              AND EXISTS(SELECT FROM cerberus.vault_access_session s WHERE s.account_id=a.id AND s.handle_verifier={request.AccessVerifier}
                AND s.profile_id IS NULL AND NOT s.revoked AND s.issued_at<=statement_timestamp()
                AND s.policy_revision>0 AND s.policy_revision=a.policy_revision AND s.revocation_generation>0 AND s.revocation_generation=a.revocation_generation
                AND ((a.renewal_enabled AND s.expires_at>statement_timestamp()) OR (NOT a.renewal_enabled AND s.expires_at IS NULL)))
            """,ct);
        if(changed!=1)
        {
            now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
            return new(Error:FreshAuthentication.IsFresh(request.AuthenticationIssuedAt,now)?"vault_access_denied":"fresh_authentication_required");
        }
        var closed=await new AccountClosureStore(new TransactionContextFactory(factory,db)).RequestAsync(new(request.Actor,request.AuthenticationIssuedAt,request.ExpectedRevision),ct);
        if(closed.Error is not null)return new(Error:closed.Error);
        var before=JsonNode.Parse(await AccountLifecycleSnapshot.ReadAsync(db,owner.Id,roots,[],false,ct))!;
        var closedOwner=JsonSerializer.Serialize(await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==owner.Id,ct));
        var at=closed.Details!.ClosureRequestedAt;
        await AccountErasure.AddIntentAsync(db,owner.PublicId,at,ct);
        var expectedEntries=await AccountErasure.EntriesAsync(db,owner.PublicId,ct);
        var key="account-erase/"+owner.PublicId;
        if(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey==key,ct))return new(Error:"persistence_unavailable");
        var job=new RetentionWorkItem{OperationKey=key,DueAt=at};var workId=job.PublicId;db.RetentionWorkItems.Add(job);
        var recoverable=await db.RetentionWorkItems.SingleAsync(x=>x.OperationKey=="account-purge/"+owner.PublicId,ct);
        recoverable.CompletedAt=at;recoverable.ClaimToken=null;recoverable.ClaimExpiresAt=null;
        var recoverableId=recoverable.PublicId;var recoverableKey=recoverable.OperationKey;
        await db.SaveChangesAsync(ct);
        var after=JsonNode.Parse(await AccountLifecycleSnapshot.ReadAsync(db,owner.Id,roots,[],false,ct))!;
        // Exclude only the intended typed markers; unrelated terminal state is collateral.
        var intended=expectedEntries.Select(x=>(x.ResourceKind,x.ResourceId)).ToHashSet();
        void Normalize(JsonNode snapshot)
        {
            var retained=snapshot["terminal"]?.AsArray().Where(x=>!intended.Contains((x!["resource_kind"]!.GetValue<string>(),x["resource_id"]!.GetValue<Guid>())))
                .Select(x=>x!.DeepClone()).ToArray()??[];
            snapshot["terminal"]=retained.Length==0?null:new JsonArray(retained);
        }
        Normalize(before);Normalize(after);
        var persistedJob=await db.RetentionWorkItems.AsNoTracking().SingleOrDefaultAsync(x=>x.OperationKey==key,ct);
        var persistedRecoverable=await db.RetentionWorkItems.AsNoTracking().SingleOrDefaultAsync(x=>x.OperationKey==recoverableKey,ct);
        if(!JsonNode.DeepEquals(before,after)
            ||closedOwner!=JsonSerializer.Serialize(await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==owner.Id,ct))
            ||!(await AccountErasure.EntriesAsync(db,owner.PublicId,ct)).ToHashSet().SetEquals(expectedEntries)
            ||persistedJob is null||persistedJob.Id!=job.Id||persistedJob.PublicId!=workId||workId==Guid.Empty
            ||persistedJob.DueAt!=at||persistedJob.Attempts!=0||persistedJob.ClaimToken is not null||persistedJob.ClaimExpiresAt is not null||persistedJob.CompletedAt is not null
            ||persistedRecoverable is null||persistedRecoverable.Id!=recoverable.Id||persistedRecoverable.PublicId!=recoverableId||recoverableId==Guid.Empty
            ||persistedRecoverable.DueAt!=closed.Details.ClosureDeadline||persistedRecoverable.Attempts!=0||persistedRecoverable.CompletedAt!=at
            ||persistedRecoverable.ClaimToken is not null||persistedRecoverable.ClaimExpiresAt is not null)
            return new(Error:"persistence_unavailable");
        var details=new AccountDeletionDetails(owner.PublicId,at,"pending");if(!details.IsValid())return new(Error:"persistence_unavailable");
        await tx.CommitAsync(ct);return new(details,workId);
    }
}
