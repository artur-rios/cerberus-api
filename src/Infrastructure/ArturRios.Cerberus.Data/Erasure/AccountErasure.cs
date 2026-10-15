using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Erasure;

internal static class AccountErasure
{
    internal static async Task<long[]> LockAsync(AppDbContext db,Guid id,CancellationToken ct)
    {
        var owner=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.PublicId==id,ct);if(owner is null)return [];
        var ids=(await Connections(db,owner.Id,ct)).Append(owner.Id).Distinct().ToArray();
        var roots=await db.Accounts.Where(x=>ids.Contains(x.Id)).Select(x=>x.PublicId).ToArrayAsync(ct);
        if(roots.Length!=ids.Length||roots.Any(x=>x==Guid.Empty))throw new InvalidOperationException("Invalid account roots.");
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE public_id=ANY({roots}) ORDER BY public_id FOR UPDATE",ct);
        if(!(await Connections(db,owner.Id,ct)).Append(owner.Id).ToHashSet().SetEquals(ids))throw new InvalidOperationException("Account graph changed.");
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            SELECT FROM cerberus.collection c WHERE c.account_id={owner.Id}
              OR EXISTS(SELECT FROM cerberus.collection_grant g WHERE g.collection_id=c.id AND g.recipient_account_id={owner.Id})
            ORDER BY c.public_id FOR NO KEY UPDATE OF c
            """,ct);
        return ids;
    }
    private static Task<long[]> Connections(AppDbContext db,long owner,CancellationToken ct)=>(from g in db.CollectionGrants join c in db.Collections on g.CollectionId equals c.Id
        where c.AccountId==owner||g.RecipientAccountId==owner select c.AccountId==owner?g.RecipientAccountId:c.AccountId).Distinct().ToArrayAsync(ct);

    private static async Task<List<Target>> TargetsAsync(AppDbContext db,Guid id,CancellationToken ct)=>await db.Database.SqlQuery<Target>($"""
        WITH owner AS(SELECT id,public_id FROM cerberus.account WHERE public_id={id})
        SELECT 'account' AS resource_kind,public_id AS resource_id FROM owner
        UNION SELECT 'profile',public_id FROM cerberus.profile WHERE account_id IN(SELECT id FROM owner)
        UNION SELECT 'record',public_id FROM cerberus.record WHERE account_id IN(SELECT id FROM owner)
        UNION SELECT 'folder',public_id FROM cerberus.folder WHERE account_id IN(SELECT id FROM owner)
        UNION SELECT 'collection',public_id FROM cerberus.collection WHERE account_id IN(SELECT id FROM owner)
        UNION SELECT 'grant',g.public_id FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id
          WHERE c.account_id IN(SELECT id FROM owner) OR g.recipient_account_id IN(SELECT id FROM owner)
        UNION SELECT 'grant',public_id FROM cerberus.software_grant WHERE account_id IN(SELECT id FROM owner)
        """).ToListAsync(ct);
    private sealed class Target{public string ResourceKind{get;set;}="";public Guid ResourceId{get;set;}}
    internal static async Task AddIntentAsync(AppDbContext db,Guid id,DateTimeOffset at,CancellationToken ct)
    {
        var targets=await TargetsAsync(db,id,ct);
        if(targets.Count==0||targets.Any(x=>!TerminalResourceIdentity.IsValid(x.ResourceKind,x.ResourceId))||at.Offset!=TimeSpan.Zero||at<DateTimeOffset.UnixEpoch||at.Ticks%10!=0)
            throw new InvalidOperationException("Invalid account erasure intent.");
        foreach(var t in targets)
        {
            await db.Database.ExecuteSqlInterpolatedAsync($"""
                INSERT INTO cerberus.terminal_erasure(resource_kind,resource_id,deleted_at)
                VALUES({t.ResourceKind},{t.ResourceId},{at}) ON CONFLICT(resource_kind,resource_id)
                DO UPDATE SET deleted_at=LEAST(cerberus.terminal_erasure.deleted_at,EXCLUDED.deleted_at)
                """,ct);
            if(!await db.TerminalErasures.AnyAsync(x=>x.ResourceKind==t.ResourceKind&&x.ResourceId==t.ResourceId&&x.DeletedAt<=at,ct))throw new InvalidOperationException("Erasure intent was not persisted.");
        }
    }
    internal static async Task<IReadOnlyList<ErasureEntry>> EntriesAsync(AppDbContext db,Guid id,CancellationToken ct)
    {
        var targets=await TargetsAsync(db,id,ct);
        if(!targets.Any(x=>x.ResourceKind=="account"&&x.ResourceId==id))targets.Add(new(){ResourceKind="account",ResourceId=id});
        var entries=new List<ErasureEntry>();
        foreach(var t in targets.OrderBy(x=>x.ResourceKind=="account"?0:1).ThenBy(x=>x.ResourceKind).ThenBy(x=>x.ResourceId))
        {
            var marker=await db.TerminalErasures.AsNoTracking().SingleOrDefaultAsync(x=>x.ResourceKind==t.ResourceKind&&x.ResourceId==t.ResourceId,ct);
            if(marker is null||!TerminalResourceIdentity.IsValid(marker.ResourceKind,marker.ResourceId)||marker.DeletedAt.Offset!=TimeSpan.Zero||marker.DeletedAt<DateTimeOffset.UnixEpoch||marker.DeletedAt.Ticks%10!=0)
                throw new InvalidOperationException("Typed account erasure intent is required.");
            entries.Add(new(marker.ResourceId,marker.ResourceKind,marker.DeletedAt));
        }
        return entries;
    }
    internal static async Task RemoveAsync(AppDbContext db,Guid id,RetentionClaim? claim,CancellationToken ct,IReadOnlyList<ErasureEntry>? durable=null)
    {
        var roots=await LockAsync(db,id,ct);
        async Task Fence()
        {
            if(claim is not null)await RecordErasure.RequireClaimAsync(db,claim,ct);
            if(!await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="account"&&x.ResourceId==id,ct))throw new InvalidOperationException("Account erasure intent is required.");
        }
        async Task<int> Delete(string sql,params object[] values)
        {
            await Fence();var args=values.Append<object>(new NpgsqlParameter("erase_id",id)).ToList();
            var guard=" AND EXISTS(SELECT FROM cerberus.terminal_erasure terminal WHERE terminal.resource_kind='account' AND terminal.resource_id=@erase_id)";
            if(claim is not null)
            {
                guard+=" AND EXISTS(SELECT FROM cerberus.retention_work_item authority WHERE authority.public_id=@erase_work AND authority.claim_token=@erase_token AND authority.operation_key=@erase_key AND authority.completed_at IS NULL AND authority.claim_expires_at>statement_timestamp())";
                args.AddRange([new NpgsqlParameter("erase_work",claim.WorkId),new NpgsqlParameter("erase_token",claim.Token),new NpgsqlParameter("erase_key",claim.OperationKey)]);
            }
            var statement=sql+guard;var changed=await db.Database.ExecuteSqlRawAsync(statement,args,ct);await Fence();return changed;
        }
        await Fence();var marker=await db.TerminalErasures.AsNoTracking().SingleAsync(x=>x.ResourceKind=="account"&&x.ResourceId==id,ct);
        var owner=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.PublicId==id,ct);
        var workKeys=new List<string>{"account-purge/"+id};
        string? collateral=null;long[] recipients=[];Dictionary<long,long> generations=[];
        if(owner is not null)
        {
            if(owner.Revision is <=0 or >ProtocolBinary.MaxInteger||owner.RevocationGeneration is <=0 or >ProtocolBinary.MaxInteger
                ||owner.State is not(AccountState.Active or AccountState.ClosurePending))throw new InvalidOperationException("Invalid retained account.");
            var grants=await(from g in db.CollectionGrants.AsNoTracking() join c in db.Collections on g.CollectionId equals c.Id
                where c.AccountId==owner.Id||g.RecipientAccountId==owner.Id select new{Grant=g,Owner=c.AccountId}).ToListAsync(ct);
            if(claim is not null&&(owner.State!=AccountState.ClosurePending
                ||grants.Any(x=>x.Grant.State!=CollectionGrantState.Revoked||x.Grant.RecipientKeyEnvelope.Length!=0)
                ||await db.VaultAccessSessions.AnyAsync(x=>x.AccountId==owner.Id&&!x.Revoked,ct)
                ||await db.SoftwareGrants.AnyAsync(x=>x.AccountId==owner.Id&&(x.State!=SoftwareGrantState.Revoked||x.RecipientKey.Length!=0),ct)))
                throw new InvalidOperationException("Account erasure reductions are incomplete.");
            recipients=grants.Where(x=>x.Owner==owner.Id&&x.Grant.RecipientAccountId!=owner.Id&&x.Grant.State==CollectionGrantState.Active).Select(x=>x.Grant.RecipientAccountId).Distinct().ToArray();
            generations=await db.Accounts.Where(x=>recipients.Contains(x.Id)).ToDictionaryAsync(x=>x.Id,x=>x.RevocationGeneration,ct);
            if(generations.Count!=recipients.Length||generations.Values.Any(x=>x is <=0 or >=ProtocolBinary.MaxInteger))throw new InvalidOperationException("Invalid recipient generation.");
            collateral=await AccountErasureCollateral.ReadAsync(db,owner.Id,roots,recipients,ct);
            if(claim is null)await AddIntentAsync(db,id,marker.DeletedAt,ct);
            else if(durable is null||(await EntriesAsync(db,id,ct)).Except(durable).Any())throw new InvalidOperationException("Account erasure ledger is incomplete.");
            workKeys.AddRange(await db.TrashOperations.Where(x=>x.AccountId==owner.Id).Select(x=>"trash/"+x.PublicId).ToListAsync(ct));
            workKeys.AddRange(await db.Records.Where(x=>x.AccountId==owner.Id).Select(x=>"record-purge/"+x.PublicId).ToListAsync(ct));
            foreach(var generation in generations)
                if(await db.Accounts.Where(x=>x.Id==generation.Key&&x.RevocationGeneration==generation.Value)
                    .ExecuteUpdateAsync(x=>x.SetProperty(a=>a.RevocationGeneration,generation.Value+1).SetProperty(a=>a.ConcurrencyStamp,Guid.NewGuid()),ct)!=1)
                    throw new InvalidOperationException("Recipient revocation was not persisted.");
            if(await Delete("DELETE FROM cerberus.account WHERE public_id=@id",new NpgsqlParameter("id",id))!=1)throw new InvalidOperationException("Account purge was not persisted.");
        }
        await Delete("DELETE FROM cerberus.registration_operation WHERE account_public_id=@id",new NpgsqlParameter("id",id));
        await Delete("DELETE FROM cerberus.retention_work_item WHERE operation_key=ANY(@keys)",new NpgsqlParameter("keys",workKeys.ToArray()));
        if(await db.Accounts.AnyAsync(x=>x.PublicId==id,ct)||await db.RegistrationOperations.AnyAsync(x=>x.AccountPublicId==id,ct)
            ||await db.RetentionWorkItems.AnyAsync(x=>workKeys.Contains(x.OperationKey),ct))throw new InvalidOperationException("Account purge is incomplete.");
        if(owner is not null)
        {
            if(await AccountErasureCollateral.ReadAsync(db,owner.Id,roots,recipients,ct)!=collateral)throw new InvalidOperationException("Account purge changed unrelated data.");
            foreach(var g in generations)
                if(!await db.Accounts.AnyAsync(x=>x.Id==g.Key&&x.RevocationGeneration==g.Value+1,ct))throw new InvalidOperationException("Recipient revocation is incomplete.");
        }
        await Fence();
    }
}
