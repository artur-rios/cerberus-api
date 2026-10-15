using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Trash;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace ArturRios.Cerberus.Data.Trash;

public sealed class TrashListStore(IDbContextFactory<AppDbContext> factory):ITrashListStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {AllowDuplicateProperties=false,PropertyNameCaseInsensitive=false,NumberHandling=JsonNumberHandling.Strict};
    public async Task<VaultResult<TrashListPage>> ListAsync(TrashListRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(request.PageSize<=0 || request.After<0 || request.After>ProtocolBinary.MaxInteger
            || request.Boundary is <0 or >ProtocolBinary.MaxInteger || request.Boundary is {} boundary && boundary<request.After)
            return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            // Owner topology writers take NO KEY UPDATE/UPDATE before any session/resource.
            // SHARE permits concurrent readers and FK checks, but serializes those writers.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR SHARE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session s JOIN cerberus.account a ON a.id=s.account_id WHERE a.heimdall_public_id={request.Actor} AND s.handle_verifier={request.AccessVerifier} FOR SHARE OF s",ct);
            var histories=await db.Database.SqlQueryRaw<HistoryRow>(TrashListSql.Authority+"""
                SELECT o.id,e.resource_kind kind,e.association_snapshot bytes FROM cerberus.trash_operation o CROSS JOIN session s
                JOIN cerberus.trash_entry e ON e.operation_id=o.id AND e.resource_kind=o.root_resource_kind AND e.resource_id=o.root_resource_id
                WHERE o.account_id=s.account_id AND s.profile_id IS NOT NULL AND o.purge_at>statement_timestamp()
                  AND o.root_resource_kind IN ('record','folder','collection')
                  AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure t WHERE t.resource_kind=o.root_resource_kind AND t.resource_id=o.root_resource_id)
                """,Base(request)).ToListAsync(ct);
            var history=histories.Select(ParseHistory).Where(x=>x is not null).ToArray();
            var parameters=Base(request).Concat(new object[]{
                new NpgsqlParameter("history",NpgsqlDbType.Jsonb){Value=JsonSerializer.Serialize(history,Json)},
                new NpgsqlParameter("boundary",NpgsqlDbType.Bigint){Value=(object?)request.Boundary??DBNull.Value},
                new NpgsqlParameter("after",request.After),new NpgsqlParameter("size",request.PageSize),
                new NpgsqlParameter("take",(long)request.PageSize+1),new NpgsqlParameter("max",ProtocolBinary.MaxInteger)}).ToArray();
            // All waits precede this one current snapshot: authority, retained scope, terminal
            // identities, original deadlines, page ciphertext and independent owner pins.
            var snapshot=(await db.Database.SqlQueryRaw<Snapshot>(TrashListSql.Snapshot,parameters).ToListAsync(ct)).Single();
            if(!snapshot.ActorActive)return new(Error:"not_found");
            if(!snapshot.Allowed)return new(Error:"vault_access_denied");
            if(snapshot.CorruptAncestry || snapshot.CorruptMetadata)return new(Error:"persistence_unavailable");
            var rows=JsonSerializer.Deserialize<Item[]>(snapshot.Items,Json)!.Select(x=>new TrashListRow(x.Position,x.OperationId,x.Kind,x.ResourceId,
                x.Revision,x.ServerSequence,x.EditedAt,x.DeletedAt,x.PurgeAt,Convert.FromHexString(x.Envelope),x.KeyEpoch,
                x.KeyWrappers is null?null:Convert.FromHexString(x.KeyWrappers))).ToArray();
            foreach(var row in rows.Where(x=>x.Kind=="profile"))
            {
                var wrappers=JsonSerializer.Deserialize<ProfileKeyWrappers>(row.KeyWrappers!,Json);
                var envelope=JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope,Json);
                var protection=snapshot.Material is null?null:new VaultProtection{Material=Convert.FromHexString(snapshot.Material),
                    Revision=snapshot.ProtectionRevision??0,KeyEpoch=snapshot.ProtectionEpoch??0,RecoveryGeneration=snapshot.ProtectionGeneration??0};
                if(!CollectionGrantBinding.TryReadPins(protection,out var pins) || envelope?.IsValid()!=true
                    || wrappers?.IsValid()!=true || wrappers.MasterKeyWrapper.GrantRevision>row.Revision
                    || !wrappers.IsBound(snapshot.OwnerId!.Value,row.ResourceId,envelope.KeyEpoch,wrappers.MasterKeyWrapper.GrantRevision,request.Actor,pins!))
                    return new(Error:"persistence_unavailable");
            }
            return new(new(rows,snapshot.Boundary,snapshot.HasMore));
        }
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e) when(e is DbException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException {InnerException:DbException or TimeoutException}){return new(Error:"persistence_unavailable");}
    }
    private static object[] Base(TrashListRequest r)=>[new NpgsqlParameter("actor",r.Actor),new NpgsqlParameter("verifier",r.AccessVerifier)];
    private sealed class HistoryRow{public long Id{get;set;}public string Kind{get;set;}="";public byte[] Bytes{get;set;}=[];}
    private sealed record History(long Id,string Bytes,Guid? Parent,Guid[] Profiles,Guid[] Collections);
    private static History? ParseHistory(HistoryRow row)
    {
        try
        {
            using var doc=JsonDocument.Parse(row.Bytes);var root=doc.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)return null;
            var fields=root.EnumerateObject().ToArray();var parentName=row.Kind=="record"?"folderId":"parentFolderId";
            var names=row.Kind=="collection"?new[]{"profileIds","recordIds","folderIds"}:new[]{parentName,"profileIds","collectionIds"};
            if(fields.Length!=3 || fields.Select(x=>x.Name).Distinct().Count()!=3 || fields.Any(x=>!names.Contains(x.Name)))return null;
            static Guid Id(JsonElement e)=>e.ValueKind==JsonValueKind.String && Guid.TryParseExact(e.GetString(),"D",out var id)
                && id!=Guid.Empty && e.GetString()==id.ToString("D")?id:throw new JsonException();
            static Guid[] Ids(JsonElement e){if(e.ValueKind!=JsonValueKind.Array)throw new JsonException();var ids=e.EnumerateArray().Select(Id).ToArray();if(ids.Distinct().Count()!=ids.Length)throw new JsonException();return ids;}
            var profiles=Ids(root.GetProperty("profileIds"));
            Guid? parent=null;Guid[] collections=[];
            if(row.Kind=="collection"){_=Ids(root.GetProperty("recordIds"));_=Ids(root.GetProperty("folderIds"));}
            else {var value=root.GetProperty(parentName);parent=value.ValueKind==JsonValueKind.Null?null:Id(value);collections=Ids(root.GetProperty("collectionIds"));}
            return new(row.Id,Convert.ToHexStringLower(row.Bytes),parent,profiles,collections);
        }
        catch(JsonException){return null;}
    }
    private sealed record Item(long Position,Guid OperationId,string Kind,Guid ResourceId,long Revision,long ServerSequence,
        DateTimeOffset EditedAt,DateTimeOffset DeletedAt,DateTimeOffset PurgeAt,string Envelope,long? KeyEpoch,string? KeyWrappers);
    private sealed class Snapshot
    {
        public bool ActorActive{get;set;}public bool Allowed{get;set;}public bool CorruptAncestry{get;set;}public bool CorruptMetadata{get;set;}
        public long Boundary{get;set;}public bool HasMore{get;set;}public string Items{get;set;}="[]";
        public Guid? OwnerId{get;set;}public string? Material{get;set;}public long? ProtectionRevision{get;set;}
        public long? ProtectionEpoch{get;set;}public long? ProtectionGeneration{get;set;}
    }
}
