from pathlib import Path
root=Path('/tmp/cerberus-uc19-worktree')
(root/'src/Domain/ArturRios.Cerberus.Domain/Records/RecordUpdateContracts.cs').write_text('''using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Records;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record RecordUpdateInput(
    [property:JsonRequired] long ExpectedRevision,
    [property:JsonRequired] EncryptedEnvelope Envelope,
    [property:JsonRequired] DateTimeOffset EditedAt)
{
    public bool IsValid()=>ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger && Envelope?.IsValid()==true
        && EditedAt.Offset==TimeSpan.Zero && EditedAt.Ticks>=TimeSpan.TicksPerMicrosecond;
}
public sealed record RecordUpdateRequest(Guid Actor,string AccessVerifier,Guid RecordId,RecordUpdateInput Input);
public interface IRecordUpdateStore
{
    Task<VaultResult<RecordCreateDetails>> UpdateAsync(RecordUpdateRequest request,CancellationToken cancellationToken);
}
''')
old=(root/'src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordReadStore.cs').read_text()
cte=old[old.index('WITH RECURSIVE actor AS ('):old.index('                SELECT EXISTS(SELECT FROM actor')]
# The purpose-built writer reuses the exact targeted read permission relation privately;
# all values remain PostgreSQL parameters in both snapshot and final guarded statement.
replacements={'{request.Actor}':'@actor','{request.AccessVerifier}':'@verifier','{request.RecordId}':'@target','{(int)AccountState.Active}':'@active','{(int)CollectionGrantState.Active}':'@grant_active','{(int)CollectionGrantAccess.ReadOnly}':'@read_only','{(int)CollectionGrantAccess.ReadWrite}':'@read_write','{ProtocolBinary.MaxInteger}':'@max'}
for a,b in replacements.items():cte=cte.replace(a,b)
cte=cte.rstrip()+''', writable AS (
    SELECT r.* FROM visible r CROSS JOIN session s WHERE r.account_id=s.account_id
      OR EXISTS(SELECT FROM included i JOIN cerberus.collection_grant g ON g.id=i.grant_id
                WHERE i.record_id=r.id AND g.access=@read_write)
)
'''
s=r'''using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace ArturRios.Cerberus.Data.Records;

public sealed class RecordUpdateStore(IDbContextFactory<AppDbContext> factory):IRecordUpdateStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false,NumberHandling=JsonNumberHandling.Strict};
    public async Task<VaultResult<RecordCreateDetails>> UpdateAsync(RecordUpdateRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(request.RecordId==Guid.Empty || request.Input?.IsValid()!=true)return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR UPDATE",ct);
            var account=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.HeimdallPublicId==request.Actor,ct);
            if(account is null || account.State!=AccountState.Active || await db.TerminalErasures.AnyAsync(x=>x.ResourceId==account.PublicId,ct))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",ct);
            var session=await db.VaultAccessSessions.AsNoTracking().SingleOrDefaultAsync(x=>x.AccountId==account.Id && x.HandleVerifier==request.AccessVerifier,ct);
            if(session?.ProfileId is not null)
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile WHERE id={session.ProfileId} AND account_id={account.Id} FOR UPDATE",ct);
            var initial=await SnapshotAsync(db,request,ct);
            var error=StateError(initial);
            if(error is not null)return new(Error:error);
            var target=JsonSerializer.Deserialize<Item>(initial.Item!,Json)!;
            var selection=session?.ProfileId??0;
            // Cross-owner order: own account/session/selection, collections, grants, target.
            // Do not lock foreign accounts/profiles or folders. Existing create locks folders
            // leaf-to-root; complete current ancestry is instead recomputed INSIDE the write.
            var collections=await db.Collections.FromSqlInterpolated($"""
                SELECT c.* FROM cerberus.collection c WHERE c.account_id={target.AccountId} AND c.deleted_at IS NULL
                  AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=c.public_id)
                  AND (c.account_id={account.Id} OR EXISTS(SELECT FROM cerberus.collection_grant g
                    WHERE g.collection_id=c.id AND g.recipient_account_id={account.Id} AND g.state={CollectionGrantState.Active}
                      AND g.access IN ({CollectionGrantAccess.ReadOnly},{CollectionGrantAccess.ReadWrite})
                      AND g.revision>0 AND g.revision<={ProtocolBinary.MaxInteger}
                      AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=g.public_id)))
                  AND ({selection}=0 OR EXISTS(SELECT FROM cerberus.profile_collection pc WHERE pc.profile_id={selection} AND pc.collection_id=c.id))
                ORDER BY c.public_id FOR SHARE
                """).AsNoTracking().ToListAsync(ct);
            var lockedCollections=collections.Select(x=>x.Id).ToArray();
            var grants=await db.CollectionGrants.FromSqlInterpolated($"""
                SELECT g.* FROM cerberus.collection_grant g WHERE g.collection_id=ANY({lockedCollections})
                  AND g.recipient_account_id={account.Id} AND g.state={CollectionGrantState.Active}
                ORDER BY g.public_id FOR SHARE
                """).AsNoTracking().ToListAsync(ct);
            var lockedGrants=grants.Select(x=>x.Id).ToArray();
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE public_id={request.RecordId} AND account_id={target.AccountId} FOR UPDATE",ct);
            var current=await SnapshotAsync(db,request,ct);
            error=StateError(current);
            if(error is not null)return new(Error:error);
            if(current.Collections.Except(lockedCollections).Any() || current.GrantsIds.Except(lockedGrants).Any())return new(Error:"revision_conflict");
            if(!ValidNative(current))return new(Error:"persistence_unavailable");
            if(!current.Writable)return new(Error:"vault_access_denied");
            target=JsonSerializer.Deserialize<Item>(current.Item!,Json)!;
            if(!Safe(target.Revision) || !Safe(target.Sequence) || target.EditedAt.Offset!=TimeSpan.Zero
                || target.EditedAt.Ticks<10 || target.EditedAt.Ticks%10!=0)return new(Error:"persistence_unavailable");
            var original=Convert.FromHexString(target.Envelope);
            var old=JsonSerializer.Deserialize<EncryptedEnvelope>(original,Json);
            if(old?.IsValid()!=true)return new(Error:"persistence_unavailable");
            if(target.Revision!=request.Input.ExpectedRevision || target.Revision==ProtocolBinary.MaxInteger)return new(Error:"revision_conflict");
            var input=request.Input;
            if(input.Envelope.KeyEpoch!=old.KeyEpoch || input.Envelope!=old && (input.Envelope.KeySalt==old.KeySalt || input.Envelope.Nonce==old.Nonce))return new(Error:"validation_failed");
            var edited=input.EditedAt.AddTicks(-(input.EditedAt.Ticks%10));
            var parameters=Parameters(request).Concat(new object[]{new NpgsqlParameter("replacement",JsonSerializer.SerializeToUtf8Bytes(input.Envelope,Json)),new NpgsqlParameter("edited",edited),new NpgsqlParameter("stamp",Guid.NewGuid()),new NpgsqlParameter("expected",input.ExpectedRevision),new NpgsqlParameter("previous_sequence",target.Sequence),new NpgsqlParameter("original",original),new NpgsqlParameter("collections",current.Collections),new NpgsqlParameter("grants",current.GrantsIds)}).ToArray();
            var changed=await db.Database.ExecuteSqlRawAsync(Authority+"""
                UPDATE cerberus.record AS r SET envelope=@replacement,edited_at=@edited,revision=r.revision+1,
                  server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp
                FROM writable w WHERE r.id=w.id AND r.public_id=@target AND r.account_id=w.account_id
                  AND r.revision=@expected AND w.revision=@expected AND w.server_sequence=@previous_sequence AND w.envelope=@original
                  AND NOT EXISTS(SELECT FROM included i JOIN visible v ON v.id=i.record_id WHERE NOT(i.collection_id=ANY(@collections)))
                  AND NOT EXISTS(SELECT FROM relevant_grants rg WHERE NOT(rg.grant_id=ANY(@grants)))
                """,parameters,ct);
            if(changed!=1)
            {
                var after=await SnapshotAsync(db,request,ct);
                return new(Error:StateError(after) ?? (!after.Writable?"vault_access_denied":"revision_conflict"));
            }
            var result=await db.Records.AsNoTracking().Where(x=>x.PublicId==request.RecordId).Select(x=>new RecordCreateDetails(x.PublicId,x.Revision,x.ServerSequence,x.EditedAt)).SingleAsync(ct);
            if(result.Revision!=target.Revision+1 || !Safe(result.ServerSequence) || result.ServerSequence<=target.Sequence || result.EditedAt!=edited)return new(Error:"persistence_unavailable");
            await tx.CommitAsync(ct);return new(result);
        }
        catch(PostgresException e) when(e.SqlState=="2200H"){return new(Error:"revision_conflict");}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e) when(e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException {InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
    }
    private static bool Safe(long n)=>n is >0 and <=ProtocolBinary.MaxInteger;
    private static string? StateError(Snapshot s)=>!s.ActorActive?"not_found":!s.Allowed?"vault_access_denied":s.CorruptAncestry?"persistence_unavailable":s.Item is null?"not_found":null;
    private static bool ValidNative(Snapshot snapshot)
    {
        var grants=JsonSerializer.Deserialize<GrantEvidence[]>(snapshot.Grants,Json)!;
        if(grants.Length==0)return true;
        var p=snapshot.Protection is null?null:JsonSerializer.Deserialize<ProtectionEvidence>(snapshot.Protection,Json);
        if(p is null || !CollectionGrantBinding.TryReadPins(Pins(p.OwnerMaterial,p.OwnerRevision,p.OwnerEpoch,p.OwnerGeneration),out var owner)
            || !CollectionGrantBinding.TryReadPins(Pins(p.RecipientMaterial,p.RecipientRevision,p.RecipientEpoch,p.RecipientGeneration),out var recipient))return false;
        return grants.All(g=>CollectionGrantBinding.IsBound(new(){PublicId=g.GrantId,Revision=g.GrantRevision,RecipientKeyEnvelope=Convert.FromHexString(g.GrantEnvelope)},
            new(){PublicId=g.CollectionId,KeyEpoch=g.CollectionEpoch,Envelope=Convert.FromHexString(g.CollectionEnvelope)},p.OwnerId,p.RecipientIdentity,owner!,recipient!));
    }
    private static VaultProtection? Pins(string? material,long? revision,long? epoch,long? generation)=>material is null || revision is null || epoch is null || generation is null?null:new(){Material=Convert.FromHexString(material),Revision=revision.Value,KeyEpoch=epoch.Value,RecoveryGeneration=generation.Value};
    private static object[] Parameters(RecordUpdateRequest r)=>[new NpgsqlParameter("actor",r.Actor),new NpgsqlParameter("verifier",r.AccessVerifier),new NpgsqlParameter("target",r.RecordId),new NpgsqlParameter("active",(int)AccountState.Active),new NpgsqlParameter("grant_active",(int)CollectionGrantState.Active),new NpgsqlParameter("read_only",(int)CollectionGrantAccess.ReadOnly),new NpgsqlParameter("read_write",(int)CollectionGrantAccess.ReadWrite),new NpgsqlParameter("max",ProtocolBinary.MaxInteger)];
    private static async Task<Snapshot> SnapshotAsync(AppDbContext db,RecordUpdateRequest request,CancellationToken ct)
    {
        var rows=await db.Database.SqlQueryRaw<Snapshot>(Authority+SnapshotSelect,Parameters(request)).ToListAsync(ct);
        return rows.Single();
    }
    private sealed record Item(Guid RecordId,long Id,long AccountId,long Revision,long Sequence,DateTimeOffset EditedAt,string Envelope);
    private sealed class Snapshot
    {
        public bool ActorActive{get;set;}public bool Allowed{get;set;}public bool CorruptAncestry{get;set;}public bool Writable{get;set;}
        public string? Item{get;set;}public long[] Collections{get;set;}=[];public long[] GrantsIds{get;set;}=[];
        public string Grants{get;set;}="[]";public string? Protection{get;set;}
    }
    private sealed record GrantEvidence(Guid CollectionId,long CollectionEpoch,string CollectionEnvelope,Guid GrantId,long GrantRevision,string GrantEnvelope);
    private sealed record ProtectionEvidence(Guid OwnerId,Guid RecipientIdentity,string? OwnerMaterial,long? OwnerRevision,long? OwnerEpoch,long? OwnerGeneration,string? RecipientMaterial,long? RecipientRevision,long? RecipientEpoch,long? RecipientGeneration);
    private const string SnapshotSelect="""
        SELECT EXISTS(SELECT FROM actor a WHERE a.state=@active AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=a.public_id)) AS actor_active,
          EXISTS(SELECT FROM session) AS allowed,EXISTS(SELECT FROM writable) AS writable,
          EXISTS(SELECT FROM scoped r JOIN ancestry t ON t.record_id=r.id WHERE t.cycle
            AND NOT EXISTS(SELECT FROM ancestry hidden WHERE hidden.record_id=r.id AND hidden.hidden)) AS corrupt_ancestry,
          (SELECT jsonb_build_object('recordId',r.public_id,'id',r.id,'accountId',r.account_id,'revision',r.revision,
            'sequence',r.server_sequence,'editedAt',r.edited_at,'envelope',encode(r.envelope,'hex'))::text FROM visible r) AS item,
          ARRAY(SELECT DISTINCT i.collection_id FROM included i JOIN visible r ON r.id=i.record_id) AS collections,
          ARRAY(SELECT grant_id FROM relevant_grants) AS grants_ids,
          (SELECT jsonb_build_object('ownerId',owner.public_id,'recipientIdentity',a.heimdall_public_id,
            'ownerMaterial',encode(op.material,'hex'),'ownerRevision',op.revision,'ownerEpoch',op.key_epoch,'ownerGeneration',op.recovery_generation,
            'recipientMaterial',encode(rp.material,'hex'),'recipientRevision',rp.revision,'recipientEpoch',rp.key_epoch,'recipientGeneration',rp.recovery_generation)::text
            FROM visible r JOIN cerberus.account owner ON owner.id=r.account_id CROSS JOIN actor a
            LEFT JOIN cerberus.vault_protection op ON op.account_id=owner.id LEFT JOIN cerberus.vault_protection rp ON rp.account_id=a.id
            WHERE EXISTS(SELECT FROM relevant_grants)) AS protection,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('collectionId',c.public_id,'collectionEpoch',c.key_epoch,
            'collectionEnvelope',encode(c.envelope,'hex'),'grantId',g.public_id,'grantRevision',g.revision,'grantEnvelope',encode(g.recipient_key_envelope,'hex')))
            FROM relevant_grants rg JOIN cerberus.collection_grant g ON g.id=rg.grant_id JOIN cerberus.collection c ON c.id=g.collection_id),'[]'::jsonb)::text AS grants
        """;
    private const string Authority="""
'''+cte+'''        """;
}
'''
(root/'src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordUpdateStore.cs').write_text(s)
with (root/'.superpowers/sdd/2026-10-09-uc-19-update-record/progress.md').open('a') as f:f.write('\nTask1 RED observed exit1 CS0246 missing RecordUpdateStore/RecordUpdateInput (data-red.log), matrix written before product. Minimal purpose-built store/domain implementation now written; focused GREEN next.\n')
print('Implemented UC19 strict domain contract, targeted authority snapshot and guarded transactional writer.')
