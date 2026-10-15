using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Software;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;

namespace ArturRios.Cerberus.Data.Software;

public sealed class SoftwareSecretStore(IDbContextFactory<AppDbContext> factory):ISoftwareSecretStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false,NumberHandling=JsonNumberHandling.Strict};

    public async Task<VaultResult<SoftwareSecretDetails>> ReadAsync(SoftwareSecretRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Identity?.IsValid()!=true)return new(Error:"authentication_required");
        if(request.Input?.IsValid()!=true)return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            var first=await Read(db,request,ct);
            if(Error(first) is {} error)return new(Error:error);
            // Native writers serialize under the owner account before resource/grant locks.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={first.OwnerId} FOR UPDATE",ct);
            if(request.Input.Kind=="collection"||request.Input.CollectionId is not null)
            {
                var collection=request.Input.CollectionId??request.Input.Id;
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection WHERE account_id={first.OwnerId} AND public_id={collection} FOR UPDATE",ct);
            }
            if(request.Input.Kind=="record")
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE account_id={first.OwnerId} AND public_id={request.Input.Id} FOR UPDATE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.software_grant WHERE account_id={first.OwnerId} AND public_id={request.Input.GrantId} FOR UPDATE",ct);
            var current=await Read(db,request,ct);
            if(Error(current) is {} denied)return new(Error:denied);
            if(current.OwnerId!=first.OwnerId)return new(Error:"not_found");
            var evidence=JsonSerializer.Deserialize<Evidence>(current.Payload!,Json);
            if(evidence is null||!CollectionGrantBinding.TryReadPins(new()
            {
                Material=Convert.FromHexString(evidence.Material??""),Revision=evidence.ProtectionRevision,
                KeyEpoch=evidence.ProtectionEpoch,RecoveryGeneration=evidence.ProtectionGeneration
            },out var pins))return new(Error:"persistence_unavailable");
            var target=Resource(evidence.Target);var provisioning=Resource(evidence.Provisioning);
            var key=JsonSerializer.Deserialize<SoftwareRecipientKey>(Convert.FromHexString(evidence.RecipientKey),Json);
            var wrap=JsonSerializer.Deserialize<RecipientEnvelope>(Convert.FromHexString(evidence.KeyEnvelope),Json);
            if(target is null||provisioning is null||key is null||wrap is null)return new(Error:"persistence_unavailable");
            var details=new SoftwareSecretDetails(evidence.OwnerAccountId,request.Input.GrantId,evidence.GrantRevision,target,new(provisioning,wrap),key,pins!.AuthorKey);
            if(!details.IsValid(request.Identity,request.Input))return new(Error:"persistence_unavailable");
            // Final database-time check follows every wait and cryptographic read.
            var final=await Read(db,request,ct);
            if(Error(final) is {} finalError)return new(Error:finalError);
            if(final.OwnerId!=current.OwnerId||final.Stable!=current.Stable||final.Payload!=current.Payload)
                return new(Error:"persistence_unavailable");
            await tx.CommitAsync(ct);
            return new(details);
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e)when(e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            ||e is InvalidOperationException{InnerException:DbException or DbUpdateException or TimeoutException})
        {return new(Error:"persistence_unavailable");}
    }
    private static string? Error(Snapshot s)=>s.Expired?"authentication_required":!s.Allowed?"not_found":s.CorruptAncestry?"persistence_unavailable":null;
    private static SoftwareSecretResource? Resource(ResourceEvidence e)
    {
        var envelope=JsonSerializer.Deserialize<EncryptedEnvelope>(Convert.FromHexString(e.Envelope),Json);
        if(envelope is null||e.Sequence is <=0 or >ProtocolBinary.MaxInteger||e.PurgeAt is not null)return null;
        var resource=new SoftwareSecretResource(e.Kind,e.Id,e.Revision,e.KeyEpoch??envelope.KeyEpoch,envelope);
        return resource.IsValid()?resource:null;
    }
    private sealed record ResourceEvidence(string Kind,Guid Id,long Revision,long? KeyEpoch,string Envelope,long Sequence,string? PurgeAt);
    private sealed record Evidence(Guid OwnerAccountId,long GrantRevision,string RecipientKey,string KeyEnvelope,
        ResourceEvidence Target,ResourceEvidence Provisioning,string? Material,long ProtectionRevision,long ProtectionEpoch,long ProtectionGeneration);
    private sealed class Snapshot
    {
        public long OwnerId{get;set;}public bool Expired{get;set;}public bool Allowed{get;set;}public bool CorruptAncestry{get;set;}
        public string? Payload{get;set;}public string Stable{get;set;}="{}";
    }
    private static async Task<Snapshot> Read(AppDbContext db,SoftwareSecretRequest r,CancellationToken ct)
    {
        object[] parameters=[new NpgsqlParameter("grant",r.Input.GrantId),new NpgsqlParameter("application",r.Identity.ApplicationId),
            new NpgsqlParameter("expiry",r.Identity.ExpiresAt),new NpgsqlParameter("kind",r.Input.Kind),new NpgsqlParameter("id",r.Input.Id),
            new NpgsqlParameter("collection",NpgsqlDbType.Uuid){Value=(object?)r.Input.CollectionId??DBNull.Value}];
        return(await db.Database.SqlQueryRaw<Snapshot>(Sql,parameters).ToListAsync(ct)).Single();
    }
    private const string Sql="""
        WITH RECURSIVE grant_row AS MATERIALIZED (
          SELECT g.* FROM cerberus.software_grant g JOIN cerberus.account a ON a.id=g.account_id
          WHERE g.public_id=@grant AND g.heimdall_software_public_id=@application AND g.state=0 AND a.state=0
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE
              (e.resource_kind='account' AND e.resource_id=a.public_id) OR (e.resource_kind='software-grant' AND e.resource_id=g.public_id))
        ), r AS MATERIALIZED (
          SELECT r.* FROM cerberus.record r JOIN grant_row g ON g.account_id=r.account_id
          WHERE @kind='record' AND r.public_id=@id AND r.deleted_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='record' AND e.resource_id=r.public_id)
        ), c AS MATERIALIZED (
          SELECT c.* FROM cerberus.collection c JOIN grant_row g ON g.account_id=c.account_id
          WHERE c.public_id=COALESCE(@collection,CASE WHEN @kind='collection' THEN @id END) AND c.deleted_at IS NULL
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='collection' AND e.resource_id=c.public_id)
        ), ancestry AS (
          SELECT f.*,ARRAY[f.id] path,false cycle,
            (f.deleted_at IS NOT NULL OR f.purge_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)) hidden
          FROM cerberus.folder f JOIN r ON f.id=r.folder_id AND f.account_id=r.account_id
          UNION ALL
          SELECT f.*,t.path||f.id,f.id=ANY(t.path),
            t.hidden OR f.deleted_at IS NOT NULL OR f.purge_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
          FROM ancestry t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id WHERE NOT t.cycle
        ), wrap AS MATERIALIZED (
          SELECT sr.recipient_key_envelope FROM cerberus.software_grant_record sr JOIN grant_row g ON sr.grant_id=g.id AND sr.account_id=g.account_id
          JOIN r ON sr.record_id=r.id WHERE @collection IS NULL AND @kind='record'
          UNION ALL
          SELECT sc.recipient_key_envelope FROM cerberus.software_grant_collection sc JOIN grant_row g ON sc.grant_id=g.id AND sc.account_id=g.account_id
          JOIN c ON sc.collection_id=c.id WHERE @collection IS NOT NULL OR @kind='collection'
        ), authority AS (
          SELECT g.*,a.public_id owner_public_id FROM grant_row g JOIN cerberus.account a ON a.id=g.account_id
          WHERE EXISTS(SELECT FROM wrap)
            AND (@kind<>'record' OR EXISTS(SELECT FROM r))
            AND ((@kind<>'collection' AND @collection IS NULL) OR EXISTS(SELECT FROM c))
            AND NOT EXISTS(SELECT FROM ancestry WHERE hidden)
            AND (@kind<>'record' OR EXISTS(SELECT FROM r WHERE folder_id IS NULL)
              OR EXISTS(SELECT FROM ancestry WHERE parent_folder_id IS NULL OR cycle))
            AND (@collection IS NULL OR EXISTS(SELECT FROM r CROSS JOIN c WHERE
              EXISTS(SELECT FROM cerberus.collection_record cr WHERE cr.account_id=r.account_id AND cr.record_id=r.id AND cr.collection_id=c.id)
              OR EXISTS(SELECT FROM cerberus.collection_folder cf JOIN ancestry t ON cf.folder_id=t.id
                WHERE cf.account_id=r.account_id AND cf.collection_id=c.id)))
        ), resources AS (
          SELECT 'record' kind,r.public_id id,r.revision,NULL::bigint key_epoch,encode(r.envelope,'hex') envelope,r.server_sequence sequence,r.purge_at FROM r
          UNION ALL SELECT 'collection',c.public_id,c.revision,c.key_epoch,encode(c.envelope,'hex'),c.server_sequence,c.purge_at FROM c
        )
        SELECT COALESCE((SELECT account_id FROM grant_row),0) owner_id,
          @expiry<=statement_timestamp() expired,EXISTS(SELECT FROM authority) allowed,
          EXISTS(SELECT FROM ancestry WHERE cycle) corrupt_ancestry,
          (SELECT jsonb_build_object('ownerAccountId',g.owner_public_id,'grantRevision',g.revision,
            'recipientKey',encode(g.recipient_key,'hex'),'keyEnvelope',(SELECT encode(recipient_key_envelope,'hex') FROM wrap),
            'target',(SELECT jsonb_build_object('kind',v.kind,'id',v.id,'revision',v.revision,'keyEpoch',v.key_epoch,
              'envelope',v.envelope,'sequence',v.sequence,'purgeAt',v.purge_at) FROM resources v WHERE v.kind=@kind),
            'provisioning',(SELECT jsonb_build_object('kind',v.kind,'id',v.id,'revision',v.revision,'keyEpoch',v.key_epoch,
              'envelope',v.envelope,'sequence',v.sequence,'purgeAt',v.purge_at) FROM resources v WHERE v.kind=CASE WHEN @collection IS NOT NULL THEN 'collection' ELSE @kind END),
            'material',encode(p.material,'hex'),'protectionRevision',COALESCE(p.revision,0),
            'protectionEpoch',COALESCE(p.key_epoch,0),'protectionGeneration',COALESCE(p.recovery_generation,0))::text
           FROM authority g LEFT JOIN cerberus.vault_protection p ON p.account_id=g.account_id) payload,
          jsonb_build_object('grant',(SELECT to_jsonb(g) FROM grant_row g),'record',(SELECT to_jsonb(r) FROM r),
            'collection',(SELECT to_jsonb(c) FROM c),'ancestry',(SELECT jsonb_agg(to_jsonb(t) ORDER BY id) FROM ancestry t),
            'membershipRecords',(SELECT jsonb_agg(to_jsonb(cr) ORDER BY cr.record_id) FROM cerberus.collection_record cr JOIN c ON cr.collection_id=c.id JOIN r ON cr.record_id=r.id WHERE cr.account_id=r.account_id),
            'membershipFolders',(SELECT jsonb_agg(to_jsonb(cf) ORDER BY cf.folder_id) FROM cerberus.collection_folder cf JOIN c ON cf.collection_id=c.id JOIN ancestry t ON cf.folder_id=t.id WHERE cf.account_id=c.account_id))::text stable
        """;
}
