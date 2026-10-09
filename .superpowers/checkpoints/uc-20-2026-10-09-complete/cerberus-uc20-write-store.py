from pathlib import Path
w=Path('/tmp/cerberus-uc20-worktree')
(w/'src/Domain/ArturRios.Cerberus.Domain/Records/RecordTrashContracts.cs').write_text('''using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Records;

public sealed record RecordTrashRequest(Guid Actor,string AccessVerifier,Guid RecordId,long ExpectedRevision);
public sealed record RecordTrashDetails(Guid RecordId,Guid TrashOperationId,long Revision,long ServerSequence,
    DateTimeOffset DeletedAt,DateTimeOffset PurgeAt);
public interface IRecordTrashStore
{
    Task<VaultResult<RecordTrashDetails>> TrashAsync(RecordTrashRequest request,CancellationToken cancellationToken);
}
''')
(w/'src/Domain/ArturRios.Cerberus.Domain/Trash/RecordAssociationSnapshot.cs').write_text('''namespace ArturRios.Cerberus.Domain.Trash;
public sealed record RecordAssociationSnapshot(Guid? FolderId,Guid[] ProfileIds,Guid[] CollectionIds);
''')
old=(w/'src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordUpdateStore.cs').read_text()
shared=old[old.index('    private static bool Safe('):].replace('RecordUpdateRequest','RecordTrashRequest')
source=r'''using System.Data.Common;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Data.Resources;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Trash;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using NpgsqlTypes;
namespace ArturRios.Cerberus.Data.Records;

public sealed class RecordTrashStore(IDbContextFactory<AppDbContext> factory):IRecordTrashStore
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false,NumberHandling=JsonNumberHandling.Strict};

    public async Task<VaultResult<RecordTrashDetails>> TrashAsync(RecordTrashRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(request.RecordId==Guid.Empty || !Safe(request.ExpectedRevision))return new(Error:"validation_failed");
        try
        {
            await using var db=await factory.CreateDbContextAsync(ct);
            await using var tx=await db.Database.BeginTransactionAsync(ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE heimdall_public_id={request.Actor} FOR NO KEY UPDATE",ct);
            var account=await db.Accounts.AsNoTracking().SingleOrDefaultAsync(x=>x.HeimdallPublicId==request.Actor,ct);
            if(account is null || account.State!=AccountState.Active || await db.TerminalErasures.AnyAsync(x=>x.ResourceId==account.PublicId,ct))return new(Error:"not_found");
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.vault_access_session WHERE account_id={account.Id} AND handle_verifier={request.AccessVerifier} FOR UPDATE",ct);
            var initial=await SnapshotAsync(db,request,ct);
            var error=StateError(initial);if(error is not null)return new(Error:error);
            var item=JsonSerializer.Deserialize<Item>(initial.Item!,Json)!;
            // Foreign routes establish only whether this denial is visible. No foreign locks
            // or content inspection are necessary to deny both RO and RW delete requests.
            if(item.AccountId!=account.Id)return new(Error:ValidNative(initial)?"vault_access_denied":"persistence_unavailable");
            var original=await db.Records.AsNoTracking().SingleAsync(x=>x.Id==item.Id,ct);
            var originalFolder=original.FolderId;
            var profileIds=await db.ProfileRecords.Where(x=>x.RecordId==item.Id && x.AccountId==account.Id).Select(x=>x.ProfileId).ToArrayAsync(ct);
            var collectionIds=await db.CollectionRecords.Where(x=>x.RecordId==item.Id && x.AccountId==account.Id).Select(x=>x.CollectionId).ToArrayAsync(ct);
            var selection=await db.VaultAccessSessions.Where(x=>x.AccountId==account.Id && x.HandleVerifier==request.AccessVerifier).Select(x=>x.ProfileId).SingleAsync(ct);
            // All owned parents precede content. NO KEY UPDATE permits FK KEY SHARE checks;
            // the target UPDATE below excludes new references once admitted.
            var profiles=await db.Profiles.FromSqlInterpolated($"""
                SELECT p.* FROM cerberus.profile p WHERE p.account_id={account.Id}
                  AND (p.id=ANY({profileIds}) OR p.id={selection??0}) ORDER BY p.public_id FOR NO KEY UPDATE
                """).AsNoTracking().ToListAsync(ct);
            error=StateError(await SnapshotAsync(db,request,ct));if(error is not null)return new(Error:error);
            var collections=await db.Collections.FromSqlInterpolated($"""
                SELECT c.* FROM cerberus.collection c WHERE c.account_id={account.Id}
                  AND c.id=ANY({collectionIds}) ORDER BY c.public_id FOR NO KEY UPDATE
                """).AsNoTracking().ToListAsync(ct);
            error=StateError(await SnapshotAsync(db,request,ct));if(error is not null)return new(Error:error);
            VaultFolder? folder=null;
            if(originalFolder is not null)
            {
                await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.folder WHERE account_id={account.Id} AND id={originalFolder} FOR NO KEY UPDATE",ct);
                folder=await db.Folders.AsNoTracking().SingleOrDefaultAsync(x=>x.Id==originalFolder && x.AccountId==account.Id,ct);
                error=StateError(await SnapshotAsync(db,request,ct));if(error is not null)return new(Error:error);
            }
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.record WHERE account_id={account.Id} AND id={item.Id} FOR UPDATE",ct);
            // Lock existing association rows after the target excludes phantom FK inserts.
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.profile_record WHERE record_id={item.Id} ORDER BY profile_id FOR UPDATE",ct);
            await db.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.collection_record WHERE record_id={item.Id} ORDER BY collection_id FOR UPDATE",ct);
            var current=await SnapshotAsync(db,request,ct);error=StateError(current);if(error is not null)return new(Error:error);
            original=await db.Records.AsNoTracking().SingleAsync(x=>x.Id==item.Id,ct);
            var actualProfiles=await db.ProfileRecords.Where(x=>x.RecordId==item.Id && x.AccountId==account.Id).Select(x=>x.ProfileId).ToArrayAsync(ct);
            var actualCollections=await db.CollectionRecords.Where(x=>x.RecordId==item.Id && x.AccountId==account.Id).Select(x=>x.CollectionId).ToArrayAsync(ct);
            var heldProfiles=profiles.Select(x=>x.Id).ToArray();var heldCollections=collections.Select(x=>x.Id).ToArray();
            if(original.FolderId!=originalFolder || actualProfiles.Except(heldProfiles).Any() || actualCollections.Except(heldCollections).Any())return new(Error:"revision_conflict");
            if(!Safe(original.Revision) || !Safe(original.ServerSequence) || original.EditedAt.Offset!=TimeSpan.Zero
                || original.EditedAt.Ticks<10 || original.EditedAt.Ticks%10!=0 || original.PurgeAt is not null)return new(Error:"persistence_unavailable");
            if(original.Revision!=request.ExpectedRevision || original.Revision==ProtocolBinary.MaxInteger
                || await db.TrashEntries.AnyAsync(x=>x.ResourceKind=="record" && x.ResourceId==request.RecordId,ct))return new(Error:"revision_conflict");
            var activeProfiles=new List<Profile>();var activeCollections=new List<VaultCollection>();
            foreach(var p in profiles.Where(x=>actualProfiles.Contains(x.Id)))
                if(p.DeletedAt is null && !await db.TerminalErasures.AnyAsync(x=>x.ResourceId==p.PublicId,ct))activeProfiles.Add(p);
            foreach(var c in collections.Where(x=>actualCollections.Contains(x.Id)))
                if(c.DeletedAt is null && !await db.TerminalErasures.AnyAsync(x=>x.ResourceId==c.PublicId,ct))activeCollections.Add(c);
            var parents=activeProfiles.Select(x=>new Parent("profile",x.Id,x.Revision,x.ServerSequence))
                .Concat(activeCollections.Select(x=>new Parent("collection",x.Id,x.Revision,x.ServerSequence))).ToList();
            if(folder is not null)parents.Add(new("folder",folder.Id,folder.Revision,folder.ServerSequence));
            if(parents.Any(x=>!Safe(x.Revision) || !Safe(x.Sequence)))return new(Error:"persistence_unavailable");
            if(parents.Any(x=>x.Revision==ProtocolBinary.MaxInteger))return new(Error:"revision_conflict");
            var snapshot=new RecordAssociationSnapshot(folder?.PublicId,
                profiles.Where(x=>actualProfiles.Contains(x.Id)).Select(x=>x.PublicId).Order().ToArray(),
                collections.Where(x=>actualCollections.Contains(x.Id)).Select(x=>x.PublicId).Order().ToArray());
            var parameters=Parameters(request).Concat(new object[]{new NpgsqlParameter("expected",request.ExpectedRevision),
                new NpgsqlParameter("previous_sequence",original.ServerSequence),new NpgsqlParameter("original",original.Envelope),
                new NpgsqlParameter("parent",NpgsqlDbType.Bigint){Value=(object?)originalFolder??DBNull.Value},
                new NpgsqlParameter("profiles",heldProfiles),new NpgsqlParameter("collections",heldCollections),new NpgsqlParameter("stamp",Guid.NewGuid())}).ToArray();
            var changed=await db.Database.ExecuteSqlRawAsync(Authority+"""
                UPDATE cerberus.record AS r SET deleted_at=statement_timestamp(),purge_at=statement_timestamp()+interval '720 hours',
                  folder_id=NULL,revision=r.revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp
                FROM visible v CROSS JOIN session s WHERE r.id=v.id AND r.account_id=s.account_id AND r.public_id=@target
                  AND r.revision=@expected AND v.revision=@expected AND v.server_sequence=@previous_sequence AND v.envelope=@original
                  AND v.folder_id IS NOT DISTINCT FROM @parent AND r.purge_at IS NULL
                  AND NOT EXISTS(SELECT FROM cerberus.profile_record pr WHERE pr.record_id=r.id AND NOT(pr.profile_id=ANY(@profiles)))
                  AND NOT EXISTS(SELECT FROM cerberus.collection_record cr WHERE cr.record_id=r.id AND NOT(cr.collection_id=ANY(@collections)))
                """,parameters,ct);
            if(changed!=1)return new(Error:StateError(await SnapshotAsync(db,request,ct))??"revision_conflict");
            var row=await db.Records.AsNoTracking().SingleAsync(x=>x.Id==original.Id,ct);
            if(row.Revision!=original.Revision+1 || !Safe(row.ServerSequence) || row.ServerSequence<=original.ServerSequence
                || row.DeletedAt is null || row.DeletedAt.Value.Ticks<10 || row.DeletedAt.Value.Offset!=TimeSpan.Zero || row.DeletedAt.Value.Ticks%10!=0
                || row.PurgeAt is null || row.PurgeAt.Value.Offset!=TimeSpan.Zero || row.PurgeAt-row.DeletedAt!=TimeSpan.FromDays(30)
                || row.FolderId is not null || row.AccountId!=original.AccountId || row.EditedAt!=original.EditedAt
                || !row.Envelope.AsSpan().SequenceEqual(original.Envelope))return new(Error:"persistence_unavailable");
            await db.ProfileRecords.Where(x=>x.RecordId==original.Id).ExecuteDeleteAsync(ct);
            await db.CollectionRecords.Where(x=>x.RecordId==original.Id).ExecuteDeleteAsync(ct);
            foreach(var parent in parents)
            {
                error=await BumpAsync(db,account.Id,parent,ct);if(error is not null)return new(Error:error);
            }
            var operation=new TrashOperation{AccountId=account.Id,RootResourceKind="record",RootResourceId=row.PublicId,
                DeletedAt=row.DeletedAt.Value,PurgeAt=row.PurgeAt.Value};
            db.TrashOperations.Add(operation);await db.SaveChangesAsync(ct);
            db.TrashEntries.Add(new(){OperationId=operation.Id,ResourceKind="record",ResourceId=row.PublicId,AssociationSnapshot=JsonSerializer.SerializeToUtf8Bytes(snapshot,Json)});
            db.RetentionWorkItems.Add(new RetentionWorkItem{OperationKey="trash/"+operation.PublicId,DueAt=operation.PurgeAt});
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(new(row.PublicId,operation.PublicId,row.Revision,row.ServerSequence,operation.DeletedAt,operation.PurgeAt));
        }
        catch(PostgresException e) when(e.SqlState=="2200H"){return new(Error:"revision_conflict");}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e) when(e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            || e is InvalidOperationException {InnerException:DbException or DbUpdateException or TimeoutException}){return new(Error:"persistence_unavailable");}
    }
    private sealed record Parent(string Kind,long Id,long Revision,long Sequence);
    private sealed class ParentMetadata{public long Revision{get;set;}public long ServerSequence{get;set;}}
    private static async Task<string?> BumpAsync(AppDbContext db,long account,Parent parent,CancellationToken ct)
    {
        var update=parent.Kind switch{
            "profile"=>"UPDATE cerberus.profile SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp WHERE id=@id AND account_id=@owner AND revision=@revision AND server_sequence=@sequence AND deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=cerberus.profile.public_id)",
            "collection"=>"UPDATE cerberus.collection SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp WHERE id=@id AND account_id=@owner AND revision=@revision AND server_sequence=@sequence AND deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=cerberus.collection.public_id)",
            _=>"UPDATE cerberus.folder SET revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp WHERE id=@id AND account_id=@owner AND revision=@revision AND server_sequence=@sequence AND deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_id=cerberus.folder.public_id)"};
        object[] args=[new NpgsqlParameter("stamp",Guid.NewGuid()),new NpgsqlParameter("id",parent.Id),new NpgsqlParameter("owner",account),new NpgsqlParameter("revision",parent.Revision),new NpgsqlParameter("sequence",parent.Sequence)];
        if(await db.Database.ExecuteSqlRawAsync(update,args,ct)!=1)return "revision_conflict";
        var select=parent.Kind switch{"profile"=>"SELECT revision,server_sequence FROM cerberus.profile WHERE id=@id","collection"=>"SELECT revision,server_sequence FROM cerberus.collection WHERE id=@id",_=>"SELECT revision,server_sequence FROM cerberus.folder WHERE id=@id"};
        var current=(await db.Database.SqlQueryRaw<ParentMetadata>(select,new NpgsqlParameter("id",parent.Id)).ToListAsync(ct)).Single();
        return current.Revision!=parent.Revision+1 || !Safe(current.ServerSequence) || current.ServerSequence<=parent.Sequence?"persistence_unavailable":null;
    }
'''
(w/'src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordTrashStore.cs').write_text(source+shared)
ledger=w/'.superpowers/sdd/2026-10-09-uc-20-delete-record/progress.md'
with ledger.open('a') as f:f.write('\nTask1 RED observed /tmp/cerberus-uc20-data-red.log exit1 CS0246 missing RecordTrashStore/RecordTrashDetails; firstbehavioralData matrix written before product. Minimal Domain+purpose-built store now written, focusedGREEN next. Shared private targeted currentauthority/nativeevidence SQL copied from reviewedUC19, no existingproductrefactor/schemachanges.\n')
print('Implemented ownertrash transaction after observed missingtypes RED.')
