using System.Text.Json;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace ArturRios.Cerberus.Data.Trash;

internal sealed class TrashRestoreResource
{
    public string Kind{get;set;}="";public long Id{get;set;}public Guid PublicId{get;set;}public long AccountId{get;set;}
    public long Revision{get;set;}public long ServerSequence{get;set;}public DateTimeOffset EditedAt{get;set;}
    public DateTimeOffset? DeletedAt{get;set;}public DateTimeOffset? PurgeAt{get;set;}public long? Parent{get;set;}
    public byte[] Envelope{get;set;}=[];public byte[]? KeyWrappers{get;set;}public long? KeyEpoch{get;set;}public Guid ConcurrencyStamp{get;set;}
}
internal static class TrashRestoreInventory
{
    internal static bool Kind(string kind)=>kind is "record" or "folder" or "collection" or "profile";
    internal static bool Safe(long value)=>value is >0 and <=ProtocolBinary.MaxInteger;
    internal static bool Time(DateTimeOffset value)=>value.Offset==TimeSpan.Zero && value.Ticks>=TimeSpan.TicksPerMicrosecond && value.Ticks%TimeSpan.TicksPerMicrosecond==0;
    internal static async Task<TrashRestoreResource?> ReadAsync(AppDbContext db,string kind,long owner,Guid id,CancellationToken ct)
    {
        const string sql="""
            SELECT 'record' kind,r.id,r.public_id,r.account_id,r.revision,r.server_sequence,r.edited_at,r.deleted_at,r.purge_at,
              r.folder_id parent,r.envelope,NULL::bytea key_wrappers,NULL::bigint key_epoch,r.concurrency_stamp
            FROM cerberus.record r WHERE @kind='record' AND r.account_id=@owner AND r.public_id=@id
            UNION ALL
            SELECT 'folder',f.id,f.public_id,f.account_id,f.revision,f.server_sequence,f.edited_at,f.deleted_at,f.purge_at,
              f.parent_folder_id,f.envelope,NULL::bytea,NULL::bigint,f.concurrency_stamp
            FROM cerberus.folder f WHERE @kind='folder' AND f.account_id=@owner AND f.public_id=@id
            UNION ALL
            SELECT 'collection',c.id,c.public_id,c.account_id,c.revision,c.server_sequence,c.edited_at,c.deleted_at,c.purge_at,
              NULL::bigint,c.envelope,NULL::bytea,c.key_epoch,c.concurrency_stamp
            FROM cerberus.collection c WHERE @kind='collection' AND c.account_id=@owner AND c.public_id=@id
            UNION ALL
            SELECT 'profile',p.id,p.public_id,p.account_id,p.revision,p.server_sequence,p.edited_at,p.deleted_at,p.purge_at,
              NULL::bigint,p.envelope,p.key_wrappers,NULL::bigint,p.concurrency_stamp
            FROM cerberus.profile p WHERE @kind='profile' AND p.account_id=@owner AND p.public_id=@id
            """;
        return (await db.Database.SqlQueryRaw<TrashRestoreResource>(sql,new NpgsqlParameter("kind",kind),new NpgsqlParameter("owner",owner),new NpgsqlParameter("id",id)).ToListAsync(ct)).SingleOrDefault();
    }
    internal static async Task<bool> NativeAsync(AppDbContext db,TrashRestoreResource row,Guid owner,Guid actor,CancellationToken ct)
    {
        try
        {
            var envelope=JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,TrashJson.Options);
            if(envelope?.IsValid()!=true)return false;
            if(row.Kind=="collection" && row.KeyEpoch!=envelope.KeyEpoch)return false;
            if(row.Kind!="profile")return true;
            var wrappers=row.KeyWrappers is null?null:JsonSerializer.Deserialize<ProfileKeyWrappers>(row.KeyWrappers,TrashJson.Options);
            var protection=await db.VaultProtections.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==row.AccountId,ct);
            return CollectionGrantBinding.TryReadPins(protection,out var pins) && wrappers?.IsValid()==true
                && wrappers.MasterKeyWrapper.GrantRevision<=row.Revision
                && wrappers.IsBound(owner,row.PublicId,envelope.KeyEpoch,wrappers.MasterKeyWrapper.GrantRevision,actor,pins!);
        }
        catch(JsonException){return false;}
    }
    internal static bool Preserved(TrashRestoreResource before,TrashRestoreResource after,long? parent,Guid stamp)=>after.Kind==before.Kind
        && after.Id==before.Id && after.PublicId==before.PublicId && after.AccountId==before.AccountId
        && after.Revision==before.Revision+1 && Safe(after.ServerSequence) && after.ServerSequence>before.ServerSequence
        && after.EditedAt==before.EditedAt && after.Envelope.AsSpan().SequenceEqual(before.Envelope)
        && (after.KeyWrappers is null?before.KeyWrappers is null:before.KeyWrappers is not null&&after.KeyWrappers.AsSpan().SequenceEqual(before.KeyWrappers))
        && after.KeyEpoch==before.KeyEpoch && after.DeletedAt is null && after.PurgeAt is null && after.Parent==parent && after.ConcurrencyStamp==stamp;
    internal static object[] Parameters(TrashRestoreResource row,long? parent,Guid stamp)=>[
        new NpgsqlParameter("owner",row.AccountId),new NpgsqlParameter("id",row.PublicId),new NpgsqlParameter("revision",row.Revision),
        new NpgsqlParameter("sequence",row.ServerSequence),new NpgsqlParameter("envelope",row.Envelope),new NpgsqlParameter("stamp",stamp),
        new NpgsqlParameter("deleted",NpgsqlDbType.TimestampTz){Value=(object?)row.DeletedAt??DBNull.Value},
        new NpgsqlParameter("purge",NpgsqlDbType.TimestampTz){Value=(object?)row.PurgeAt??DBNull.Value},
        new NpgsqlParameter("parent",NpgsqlDbType.Bigint){Value=(object?)parent??DBNull.Value},
        new NpgsqlParameter("original_parent",NpgsqlDbType.Bigint){Value=(object?)row.Parent??DBNull.Value},
        new NpgsqlParameter("wrappers",NpgsqlDbType.Bytea){Value=(object?)row.KeyWrappers??DBNull.Value},
        new NpgsqlParameter("epoch",NpgsqlDbType.Bigint){Value=(object?)row.KeyEpoch??DBNull.Value}];
    internal static string RestoreSql(string kind,bool member=false)
    {
        // Only these fixed statement fragments are selected; every value is bound.
        var guard=member?MemberGuard:Guard;
        var update=kind switch{
        "record"=>"UPDATE cerberus.record r SET folder_id=@parent,"+Changes+guard+" AND r.folder_id IS NOT DISTINCT FROM @original_parent",
        "folder"=>"UPDATE cerberus.folder r SET parent_folder_id=@parent,"+Changes+guard+" AND r.parent_folder_id IS NOT DISTINCT FROM @original_parent",
        "collection"=>"UPDATE cerberus.collection r SET "+Changes+guard+" AND r.key_epoch=@epoch",
        "profile"=>"UPDATE cerberus.profile r SET "+Changes+guard+" AND r.key_wrappers=@wrappers",_=>throw new InvalidOperationException("Invalid trash resource.")};
        return (member?"WITH session AS (SELECT @owner::bigint account_id) ":TrashListSql.Authority)+update+" RETURNING statement_timestamp() AS \"Value\"";
    }
    private const string Changes="deleted_at=NULL,purge_at=NULL,revision=r.revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp";
    private const string Guard="""
         FROM session s WHERE r.account_id=s.account_id AND r.account_id=@owner AND r.public_id=@id
          AND r.revision=@revision AND r.server_sequence=@sequence AND r.envelope=@envelope
          AND r.deleted_at=@deleted AND r.purge_at=@purge
          AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure terminal WHERE terminal.resource_kind=@kind AND terminal.resource_id=r.public_id)
          AND EXISTS(SELECT FROM cerberus.trash_operation o JOIN cerberus.trash_entry e ON e.operation_id=o.id
            WHERE o.public_id=@operation AND o.account_id=@owner AND o.root_resource_kind=@kind AND o.root_resource_id=@id
              AND o.deleted_at=@deleted AND o.purge_at=@purge AND o.purge_at>statement_timestamp()
              AND e.resource_kind=@kind AND e.resource_id=@id AND e.association_snapshot=@snapshot)
        """;
    private const string MemberGuard="""
         FROM session s WHERE r.account_id=s.account_id AND r.account_id=@owner AND r.public_id=@id
          AND r.revision=@revision AND r.server_sequence=@sequence AND r.envelope=@envelope
          AND r.deleted_at=@deleted AND r.purge_at=@purge
          AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure terminal WHERE terminal.resource_kind=@kind AND terminal.resource_id=r.public_id)
          AND EXISTS(SELECT FROM cerberus.trash_operation o JOIN cerberus.trash_entry e ON e.operation_id=o.id
            WHERE o.public_id=@operation AND o.account_id=@owner AND o.deleted_at=@deleted AND o.purge_at=@purge
              AND e.resource_kind=@kind AND e.resource_id=@id AND e.association_snapshot=@snapshot)
        """;
    internal static async Task<bool> BumpAsync(AppDbContext db,TrashRestoreResource row,CancellationToken ct)
    {
        var stamp=Guid.NewGuid();var update=row.Kind switch{
            "record"=>"UPDATE cerberus.record r SET ","folder"=>"UPDATE cerberus.folder r SET ",
            "collection"=>"UPDATE cerberus.collection r SET ","profile"=>"UPDATE cerberus.profile r SET ",_=>throw new InvalidOperationException("Invalid active parent.")};
        var sql=update+"revision=r.revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp WHERE r.account_id=@owner AND r.public_id=@id AND r.revision=@revision AND r.server_sequence=@sequence AND r.envelope=@envelope AND r.deleted_at IS NULL AND r.purge_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure t WHERE t.resource_kind=@kind AND t.resource_id=r.public_id)";
        var args=Parameters(row,row.Parent,stamp).Append<object>(new NpgsqlParameter("kind",row.Kind)).ToArray();
        if(await db.Database.ExecuteSqlRawAsync(sql,args,ct)!=1)return false;
        var after=await ReadAsync(db,row.Kind,row.AccountId,row.PublicId,ct);
        return after is not null&&Preserved(row,after,row.Parent,stamp);
    }

}
internal static class TrashJson
{
    internal static readonly JsonSerializerOptions Options=new(JsonSerializerDefaults.Web)
    {AllowDuplicateProperties=false,PropertyNameCaseInsensitive=false,NumberHandling=System.Text.Json.Serialization.JsonNumberHandling.Strict};
}
