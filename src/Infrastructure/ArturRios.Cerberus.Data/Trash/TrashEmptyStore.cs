using System.Data.Common;
using ArturRios.Cerberus.Data.Erasure;
using ArturRios.Cerberus.Data.Operations;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Cerberus.Shared.Configuration;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace ArturRios.Cerberus.Data.Trash;

public sealed class TrashEmptyStore(IDbContextFactory<AppDbContext> factory,IErasureLedger ledger,CerberusOptions options,TimeProvider clock):ITrashEmptyStore
{
    public async Task<VaultResult<TrashEmptyDetails>> EmptyAsync(TrashEmptyRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(request.ExpectedRevision is <=0 or >ProtocolBinary.MaxInteger)return new(Error:"validation_failed");
        if(!TimeSpan.TryParse(options.RetentionInterval,out var lease)||lease<=TimeSpan.Zero)return new(Error:"persistence_unavailable");
        try
        {
            var admitted=await AdmitAsync(request,ct);if(admitted.Error is not null)return new(Error:admitted.Error);
            var intent=admitted.Data!;if(intent.WorkId is null)return new(intent.Details);
            var jobs=new RetentionWorkStore(factory);await using var time=await factory.CreateDbContextAsync(ct);
            var now=await time.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \"Value\"").SingleAsync(ct);
            var claim=await jobs.TryClaimAsync(intent.WorkId.Value,now,lease,ct);if(claim is null)return new(Error:"persistence_unavailable");
            await new TrashPurgeHandler(factory,ledger).ExecuteAsync(claim,ct);
            if(!await jobs.TryCompleteAsync(claim,clock.GetUtcNow(),ct))return new(Error:"persistence_unavailable");
            var completed=await time.RetentionWorkItems.AsNoTracking().Where(x=>x.PublicId==claim.WorkId).Select(x=>x.CompletedAt).SingleOrDefaultAsync(ct);
            return completed is {} at?new(intent.Details with{PurgedAt=at}):new(Error:"persistence_unavailable");
        }
        catch(TrashEmptyConflictException){return new(Error:"revision_conflict");}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e) when(e is DbException or DbUpdateException or IOException or TimeoutException or FormatException or InvalidOperationException){return new(Error:"persistence_unavailable");}
    }
    private sealed record Intent(TrashEmptyDetails Details,Guid? WorkId);
    private async Task<VaultResult<Intent>> AdmitAsync(TrashEmptyRequest request,CancellationToken ct)
    {
        await using var db=await factory.CreateDbContextAsync(ct);await using var tx=await db.Database.BeginTransactionAsync(ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE",ct);
        var authority=await AuthorityAsync(db,request,ct);if(!authority.Active)return new(Error:"not_found");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={authority.OwnerId} AND handle_verifier={request.AccessVerifier} FOR UPDATE",ct);
        authority=await AuthorityAsync(db,request,ct);var error=Error(authority,request);if(error is not null)return new(Error:error);
        var key="trash-erase/"+authority.AccountId;
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.retention_work_item WHERE operation_key={key} FOR UPDATE",ct);
        await TrashErasureInventory.LockAsync(db,authority.OwnerId,ct);
        authority=await AuthorityAsync(db,request,ct);error=Error(authority,request);if(error is not null)return new(Error:error);
        var rows=await TrashErasureInventory.ReadAsync(db,authority.OwnerId,ct);var targets=rows.Where(x=>x.DeletedAt is not null).ToArray();TrashErasureInventory.Validate(targets);
        if(await TrashErasureInventory.ActiveEntryAsync(db,authority.OwnerId,ct))return new(Error:"revision_conflict");
        var counts=TrashErasureInventory.Counts(targets);var details=new TrashEmptyDetails(authority.AccountId,authority.Now,counts,targets.LongLength);
        if(targets.Length==0&&!await db.TrashOperations.AnyAsync(x=>x.AccountId==authority.OwnerId,ct))return new(new(details,null));
        var parameters=Base(request).Concat(new object[]{new NpgsqlParameter("public",Guid.NewGuid()),new NpgsqlParameter("key",key),new NpgsqlParameter("stamp",Guid.NewGuid()),new NpgsqlParameter("owner",authority.OwnerId),new NpgsqlParameter("expected",request.ExpectedRevision),new NpgsqlParameter("invalidate",targets.Any(x=>x.MarkerAt is null))}).ToArray();
        var admission=(await db.Database.SqlQueryRaw<Admission>(TrashEmptySql.Admit,parameters).ToListAsync(ct)).SingleOrDefault();
        if(admission is null){authority=await AuthorityAsync(db,request,ct);return new(Error:Error(authority,request)??"persistence_unavailable");}
        if(admission.WorkId==Guid.Empty||admission.AdmittedAt.Offset!=TimeSpan.Zero||admission.AdmittedAt<DateTimeOffset.UnixEpoch||admission.AdmittedAt.Ticks%TimeSpan.TicksPerMicrosecond!=0)throw new InvalidOperationException("Invalid erasure admission.");
        foreach(var row in targets.OrderBy(x=>x.Kind,StringComparer.Ordinal).ThenBy(x=>x.ResourceId))await TrashErasureInventory.AddMarkerAsync(db,row,admission.AdmittedAt,ct);
        var after=await TrashErasureInventory.ReadAsync(db,authority.OwnerId,ct);
        if(targets.Any(t=>!after.Any(x=>x.Kind==t.Kind&&x.ResourceId==t.ResourceId&&x.DeletedAt==t.DeletedAt&&x.PurgeAt==t.PurgeAt&&x.Revision==t.Revision&&x.ServerSequence==t.ServerSequence&&x.MarkerAt==(t.MarkerAt??admission.AdmittedAt))))throw new InvalidOperationException("Incomplete erasure intent.");
        if(!await db.RetentionWorkItems.AnyAsync(x=>x.PublicId==admission.WorkId&&x.OperationKey==key&&x.CompletedAt==null,ct))throw new InvalidOperationException("Erasure retry work is missing.");
        await tx.CommitAsync(ct);return new(new(details with{PurgedAt=admission.AdmittedAt},admission.WorkId));
    }
    private static object[] Base(TrashEmptyRequest r)=>[new NpgsqlParameter("actor",r.Actor),new NpgsqlParameter("verifier",r.AccessVerifier)];
    private static Task<Authority> AuthorityAsync(AppDbContext db,TrashEmptyRequest r,CancellationToken ct)=>db.Database.SqlQueryRaw<Authority>(TrashEmptySql.Authority,Base(r)).SingleAsync(ct);
    private static string? Error(Authority a,TrashEmptyRequest r)=>!a.Active?"not_found":!a.Allowed||!a.Wide?"vault_access_denied":a.OwnerId<=0||a.AccountId==Guid.Empty||a.Revision is <=0 or >ProtocolBinary.MaxInteger?"persistence_unavailable":a.Revision!=r.ExpectedRevision?"revision_conflict":null;
    private sealed class Authority{public bool Active{get;set;}public bool Allowed{get;set;}public bool Wide{get;set;}public long OwnerId{get;set;}public Guid AccountId{get;set;}public long Revision{get;set;}public DateTimeOffset Now{get;set;}}
    private sealed class Admission{public Guid WorkId{get;set;}public DateTimeOffset AdmittedAt{get;set;}}
}
