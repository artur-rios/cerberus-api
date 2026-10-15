using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace ArturRios.Cerberus.Data.Trash;

internal sealed class TrashErasureTarget
{
    public string Kind{get;set;}="";public long Id{get;set;}public Guid ResourceId{get;set;}public long AccountId{get;set;}
    public long Revision{get;set;}public long ServerSequence{get;set;}public DateTimeOffset? DeletedAt{get;set;}public DateTimeOffset? PurgeAt{get;set;}public DateTimeOffset? MarkerAt{get;set;}
    internal ErasureEntry Entry()=>MarkerAt is {} at?new(ResourceId,Kind,at):throw new InvalidOperationException("Typed terminal intent is required.");
}
internal static class TrashErasureInventory
{
    internal static async Task<List<TrashErasureTarget>> ReadAsync(AppDbContext db,long owner,CancellationToken ct)
    {
        const string sql="""
            SELECT 'record' kind,r.id,r.public_id resource_id,r.account_id,r.revision,r.server_sequence,r.deleted_at,r.purge_at,t.deleted_at marker_at
            FROM cerberus.record r LEFT JOIN cerberus.terminal_erasure t ON t.resource_kind='record' AND t.resource_id=r.public_id WHERE r.account_id=@owner AND r.deleted_at IS NOT NULL
            UNION ALL SELECT 'folder',r.id,r.public_id,r.account_id,r.revision,r.server_sequence,r.deleted_at,r.purge_at,t.deleted_at
            FROM cerberus.folder r LEFT JOIN cerberus.terminal_erasure t ON t.resource_kind='folder' AND t.resource_id=r.public_id WHERE r.account_id=@owner AND r.deleted_at IS NOT NULL
            UNION ALL SELECT 'collection',r.id,r.public_id,r.account_id,r.revision,r.server_sequence,r.deleted_at,r.purge_at,t.deleted_at
            FROM cerberus.collection r LEFT JOIN cerberus.terminal_erasure t ON t.resource_kind='collection' AND t.resource_id=r.public_id WHERE r.account_id=@owner AND r.deleted_at IS NOT NULL
            UNION ALL SELECT 'profile',r.id,r.public_id,r.account_id,r.revision,r.server_sequence,r.deleted_at,r.purge_at,t.deleted_at
            FROM cerberus.profile r LEFT JOIN cerberus.terminal_erasure t ON t.resource_kind='profile' AND t.resource_id=r.public_id WHERE r.account_id=@owner AND r.deleted_at IS NOT NULL
            """;
        return await db.Database.SqlQueryRaw<TrashErasureTarget>(sql,new NpgsqlParameter("owner",owner)).ToListAsync(ct);
    }
    internal static void Validate(IEnumerable<TrashErasureTarget> targets)
    {
        foreach(var r in targets)if(r.Kind is not("record" or "folder" or "collection" or "profile")||r.ResourceId==Guid.Empty||r.Id<=0||r.AccountId<=0
            ||r.Revision is <=0 or >ProtocolBinary.MaxInteger||r.ServerSequence is <=0 or >ProtocolBinary.MaxInteger||r.DeletedAt is null
            ||r.MarkerAt is {} at&&(at.Offset!=TimeSpan.Zero||at<DateTimeOffset.UnixEpoch||at.Ticks%TimeSpan.TicksPerMicrosecond!=0))throw new InvalidOperationException("Invalid retained erasure state.");
    }
    internal static async Task LockAsync(AppDbContext db,long owner,CancellationToken ct)
    {
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE account_id={owner} ORDER BY public_id FOR NO KEY UPDATE",ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE account_id={owner} ORDER BY public_id FOR NO KEY UPDATE",ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.folder WHERE account_id={owner} ORDER BY public_id FOR UPDATE",ct);
        await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE account_id={owner} ORDER BY public_id FOR UPDATE",ct);
    }
    internal static TrashPurgedCount[] Counts(IEnumerable<TrashErasureTarget> targets)=>new[]{"record","folder","collection","profile"}.Select(k=>new TrashPurgedCount(k,targets.LongCount(x=>x.Kind==k))).ToArray();
    internal static Task<bool> ActiveEntryAsync(AppDbContext db,long owner,CancellationToken ct)=>db.TrashEntries.AnyAsync(e=>
        db.TrashOperations.Any(o=>o.Id==e.OperationId&&o.AccountId==owner)
        &&(e.ResourceKind=="record"&&db.Records.Any(r=>r.AccountId==owner&&r.PublicId==e.ResourceId&&r.DeletedAt==null)
          ||e.ResourceKind=="folder"&&db.Folders.Any(r=>r.AccountId==owner&&r.PublicId==e.ResourceId&&r.DeletedAt==null)
          ||e.ResourceKind=="collection"&&db.Collections.Any(r=>r.AccountId==owner&&r.PublicId==e.ResourceId&&r.DeletedAt==null)
          ||e.ResourceKind=="profile"&&db.Profiles.Any(r=>r.AccountId==owner&&r.PublicId==e.ResourceId&&r.DeletedAt==null)),ct);
    internal static async Task<bool> AnyTargetsAsync(AppDbContext db,long owner,IReadOnlyList<ErasureEntry> targets,bool activeOnly,CancellationToken ct)
    {
        var records=targets.Where(x=>x.ResourceKind=="record").Select(x=>x.ResourceId).ToArray();var folders=targets.Where(x=>x.ResourceKind=="folder").Select(x=>x.ResourceId).ToArray();
        var collections=targets.Where(x=>x.ResourceKind=="collection").Select(x=>x.ResourceId).ToArray();var profiles=targets.Where(x=>x.ResourceKind=="profile").Select(x=>x.ResourceId).ToArray();
        return await db.Records.AnyAsync(r=>r.AccountId==owner&&records.Contains(r.PublicId)&&(!activeOnly||r.DeletedAt==null),ct)
            ||await db.Folders.AnyAsync(r=>r.AccountId==owner&&folders.Contains(r.PublicId)&&(!activeOnly||r.DeletedAt==null),ct)
            ||await db.Collections.AnyAsync(r=>r.AccountId==owner&&collections.Contains(r.PublicId)&&(!activeOnly||r.DeletedAt==null),ct)
            ||await db.Profiles.AnyAsync(r=>r.AccountId==owner&&profiles.Contains(r.PublicId)&&(!activeOnly||r.DeletedAt==null),ct);
    }
    internal static async Task AddMarkerAsync(AppDbContext db,TrashErasureTarget row,DateTimeOffset at,CancellationToken ct)
    {
        var table=row.Kind switch{"record"=>" FROM cerberus.record r ","folder"=>" FROM cerberus.folder r ","collection"=>" FROM cerberus.collection r ","profile"=>" FROM cerberus.profile r ",_=>throw new InvalidOperationException("Invalid erasure kind.")};
        var sql="INSERT INTO cerberus.terminal_erasure AS t(resource_kind,resource_id,deleted_at) SELECT @kind,@id,@at"+table+
            "WHERE r.account_id=@owner AND r.public_id=@id AND r.revision=@revision AND r.server_sequence=@sequence AND r.deleted_at=@deleted AND r.purge_at IS NOT DISTINCT FROM @purge ON CONFLICT(resource_kind,resource_id) DO UPDATE SET deleted_at=LEAST(t.deleted_at,EXCLUDED.deleted_at) RETURNING deleted_at AS \"Value\"";
        var args=new object[]{new NpgsqlParameter("kind",row.Kind),new NpgsqlParameter("id",row.ResourceId),new NpgsqlParameter("at",at),new NpgsqlParameter("owner",row.AccountId),new NpgsqlParameter("revision",row.Revision),new NpgsqlParameter("sequence",row.ServerSequence),new NpgsqlParameter("deleted",row.DeletedAt!.Value),new NpgsqlParameter("purge",NpgsqlDbType.TimestampTz){Value=(object?)row.PurgeAt??DBNull.Value}};
        var result=await db.Database.SqlQueryRaw<DateTimeOffset>(sql,args).ToListAsync(ct);
        if(result.Count!=1)throw new TrashEmptyConflictException();
        if(result[0]!=(row.MarkerAt??at))throw new InvalidOperationException("Terminal intent changed.");
    }
}
internal sealed class TrashEmptyConflictException:Exception;
