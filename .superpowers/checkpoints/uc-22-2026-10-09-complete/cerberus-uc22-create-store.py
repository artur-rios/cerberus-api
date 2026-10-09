from pathlib import Path
p=Path('src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordTrashStore.cs')
s=p.read_text().replace('using System.Data.Common;', 'using System.Data.Common;\nusing System.ComponentModel.DataAnnotations.Schema;\nusing ArturRios.Cerberus.Data.Erasure;\nusing ArturRios.Cerberus.Data.Operations;\nusing ArturRios.Cerberus.Shared.Configuration;\nusing ArturRios.Cerberus.Shared.Operations;')
s=s.replace('public sealed class RecordTrashStore(IDbContextFactory<AppDbContext> factory):IRecordTrashStore','public sealed class RecordPermanentDeleteStore(IDbContextFactory<AppDbContext> factory, IErasureLedger ledger, CerberusOptions options, TimeProvider clock):IRecordPermanentDeleteStore')
a=s.index('    public async Task<VaultResult<RecordTrashDetails>> TrashAsync');b=s.index('            await using var db=',a)
s=s[:a]+'''    public async Task<VaultResult<RecordPermanentDeleteDetails>> DeleteAsync(RecordPermanentDeleteRequest request,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if(request.Actor==Guid.Empty)return new(Error:"authentication_required");
        if(request.RecordId==Guid.Empty || !Safe(request.ExpectedRevision))return new(Error:"validation_failed");
        try
        {
            var result=await CommitIntentAsync(request,ct);
            if(result.Error is not null)return new(Error:result.Error);
            var intent=result.Data!;
            var workStore=new RetentionWorkStore(factory);
            var claim=await workStore.TryClaimAsync(intent.WorkId,clock.GetUtcNow(),TimeSpan.Parse(options.RetentionInterval),ct);
            if(claim is null)return new(Error:"persistence_unavailable");
            await new RecordPurgeHandler(factory,ledger).ExecuteAsync(claim,ct);
            if(!await workStore.TryCompleteAsync(claim,clock.GetUtcNow(),ct))return new(Error:"persistence_unavailable");
            return new(intent.Details);
        }
        catch(PostgresException e) when(e.SqlState=="2200H"){return new(Error:"revision_conflict");}
        catch(OperationCanceledException) when(!ct.IsCancellationRequested){return new(Error:"persistence_unavailable");}
        catch(Exception e) when(e is DbException or DbUpdateException or TimeoutException or JsonException or FormatException
            or IOException or InvalidOperationException){return new(Error:"persistence_unavailable");}
    }
    private sealed record Intent(RecordPermanentDeleteDetails Details,Guid WorkId);
    private async Task<VaultResult<Intent>> CommitIntentAsync(RecordPermanentDeleteRequest request,CancellationToken ct)
    {
''' +s[b:]
a=s.index('            if(!Safe(original.Revision)');b=s.index('            var activeProfiles=',a)
s=s[:a]+'''            if(!Safe(original.Revision) || !Safe(original.ServerSequence))return new(Error:"persistence_unavailable");
            if(original.Revision!=request.ExpectedRevision)return new(Error:"revision_conflict");
''' +s[b:]
s=s.replace('if(folder is not null)parents.Add(', 'if(original.DeletedAt is null && folder is not null)parents.Add(')
s=s.replace('var parents=activeProfiles.Select', 'if(original.DeletedAt is not null){activeProfiles.Clear();activeCollections.Clear();}\n            var parents=activeProfiles.Select')
a=s.index('            var snapshot=new RecordAssociationSnapshot');b=s.index('            var changed=',a)
s=s[:a]+'''            var parameters=Parameters(request,current.History).Concat(new object[]{new NpgsqlParameter("expected",request.ExpectedRevision),
                new NpgsqlParameter("previous_sequence",original.ServerSequence),new NpgsqlParameter("original",original.Envelope),
                new NpgsqlParameter("parent",NpgsqlDbType.Bigint){Value=(object?)originalFolder??DBNull.Value},
                new NpgsqlParameter("profiles",heldProfiles),new NpgsqlParameter("collections",heldCollections),new NpgsqlParameter("stamp",Guid.NewGuid())}).ToArray();
''' +s[b:]
s=s.replace("UPDATE cerberus.record AS r SET deleted_at=statement_timestamp(),purge_at=statement_timestamp()+interval '720 hours',\n                  folder_id=NULL,revision=r.revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp", "UPDATE cerberus.record AS r SET deleted_at=COALESCE(r.deleted_at,statement_timestamp()),\n                  folder_id=NULL,concurrency_stamp=@stamp")
s=s.replace('AND v.folder_id IS NOT DISTINCT FROM @parent AND r.purge_at IS NULL','AND v.folder_id IS NOT DISTINCT FROM @parent')
a=s.index('            if(row.Revision!=');b=s.index('            await db.ProfileRecords',a)
s=s[:a]+'''            if(row.Revision!=original.Revision || row.ServerSequence!=original.ServerSequence
                || row.DeletedAt is null || row.FolderId is not null || row.AccountId!=original.AccountId
                || !row.Envelope.AsSpan().SequenceEqual(original.Envelope))return new(Error:"persistence_unavailable");
''' +s[b:]
a=s.index('            var operation=new TrashOperation');b=s.index('    private sealed record Parent',a)
s=s[:a]+'''            await db.TrashEntries.Where(x=>x.ResourceKind=="record" && x.ResourceId==request.RecordId).ExecuteDeleteAsync(ct);
            var now=await db.Database.SqlQuery<DateTimeOffset>($"SELECT statement_timestamp() AS \\"Value\\"").SingleAsync(ct);
            db.TerminalErasures.Add(new(){ResourceKind="record",ResourceId=row.PublicId,DeletedAt=now});
            var work=new RetentionWorkItem{OperationKey="record-purge/"+row.PublicId.ToString("D"),DueAt=now};
            db.RetentionWorkItems.Add(work);
            await db.SaveChangesAsync(ct);
            await tx.CommitAsync(ct);
            return new(new(new(row.PublicId,now),work.PublicId));
    }
''' .replace('\\"','\\"')+s[b:]
# Parameterized retained associations are admitted only while the original typed
# same-owner snapshot remains identical. Every current scope query reads anew.
a=s.index('    private static object[] Parameters(');b=s.index('    private sealed record Item',a)
s=s[:a]+'''    private sealed record History(byte[]? Bytes,Guid? Folder,Guid[] Profiles,Guid[] Collections)
    {
        internal static readonly History Empty=new(null,null,[],[]);
    }
    private sealed class HistoryRow{public bool Needed{get;set;}public byte[]? Bytes{get;set;}}
    private static object[] BaseParameters(RecordPermanentDeleteRequest r)=>[new NpgsqlParameter("actor",r.Actor),new NpgsqlParameter("verifier",r.AccessVerifier),new NpgsqlParameter("target",r.RecordId),new NpgsqlParameter("active",(object)(int)AccountState.Active),new NpgsqlParameter("grant_active",(object)(int)CollectionGrantState.Active),new NpgsqlParameter("read_only",(object)(int)CollectionGrantAccess.ReadOnly),new NpgsqlParameter("read_write",(object)(int)CollectionGrantAccess.ReadWrite),new NpgsqlParameter("max",ProtocolBinary.MaxInteger)];
    private static object[] Parameters(RecordPermanentDeleteRequest r,History history)=>BaseParameters(r).Concat(new object[]{
        new NpgsqlParameter("history_bytes",NpgsqlDbType.Bytea){Value=(object?)history.Bytes??DBNull.Value},
        new NpgsqlParameter("history_folder",NpgsqlDbType.Uuid){Value=(object?)history.Folder??DBNull.Value},
        new NpgsqlParameter("history_profiles",history.Profiles),new NpgsqlParameter("history_collections",history.Collections)}).ToArray();
    private static async Task<Snapshot> SnapshotAsync(AppDbContext db,RecordPermanentDeleteRequest request,CancellationToken ct)
    {
        var prefix=Authority[..Authority.IndexOf(", history AS",StringComparison.Ordinal)].Replace("WITH RECURSIVE","WITH",StringComparison.Ordinal);
        var historyRow=(await db.Database.SqlQueryRaw<HistoryRow>(prefix+"""
            SELECT EXISTS(SELECT FROM cerberus.record r CROSS JOIN session s
                WHERE r.public_id=@target AND r.account_id=s.account_id AND r.deleted_at IS NOT NULL AND s.profile_id IS NOT NULL
                  AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='record' AND e.resource_id=r.public_id)) AS needed,
              (SELECT e.association_snapshot FROM cerberus.trash_entry e JOIN cerberus.trash_operation o ON o.id=e.operation_id
                JOIN cerberus.record r ON r.public_id=e.resource_id CROSS JOIN session s
                WHERE e.resource_kind='record' AND e.resource_id=@target AND o.account_id=s.account_id AND r.account_id=s.account_id
                  AND r.deleted_at IS NOT NULL AND s.profile_id IS NOT NULL
                  AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure t WHERE t.resource_kind='record' AND t.resource_id=r.public_id)) AS bytes
            """,BaseParameters(request)).ToListAsync(ct)).Single();
        var history=History.Empty;
        if(historyRow.Needed && historyRow.Bytes is not null)
        {
            using var json=JsonDocument.Parse(historyRow.Bytes);
            var root=json.RootElement;
            if(root.ValueKind!=JsonValueKind.Object)throw new JsonException("Invalid retained associations.");
            var properties=root.EnumerateObject().ToArray();
            if(properties.Length!=3 || properties.Select(x=>x.Name).Distinct().Count()!=3
                || !root.TryGetProperty("folderId",out var folder) || !root.TryGetProperty("profileIds",out var profiles)
                || !root.TryGetProperty("collectionIds",out var collections))throw new JsonException("Invalid retained associations.");
            static Guid Id(JsonElement e)=>e.ValueKind==JsonValueKind.String && Guid.TryParseExact(e.GetString(),"D",out var id)
                && id!=Guid.Empty && e.GetString()==id.ToString("D")?id:throw new JsonException("Invalid retained identity.");
            static Guid[] Ids(JsonElement e){if(e.ValueKind!=JsonValueKind.Array)throw new JsonException("Invalid retained associations.");
                var ids=e.EnumerateArray().Select(Id).ToArray();if(ids.Distinct().Count()!=ids.Length)throw new JsonException("Invalid retained associations.");return ids;}
            history=new(historyRow.Bytes,folder.ValueKind==JsonValueKind.Null?null:Id(folder),Ids(profiles),Ids(collections));
        }
        var rows=await db.Database.SqlQueryRaw<Snapshot>(Authority+SnapshotSelect,Parameters(request,history)).ToListAsync(ct);
        var snapshot=rows.Single();snapshot.History=history;return snapshot;
    }
''' +s[b:]
s=s.replace('public bool ActorActive{get;set;}', '[NotMapped] public History History{get;set;}=History.Empty;\n        public bool ActorActive{get;set;}')
s=s.replace('WHERE t.cycle\n', 'WHERE t.cycle AND NOT r.history_direct\n')
s=s.replace('                        ), collections AS (', '''                        ), history AS (
                            SELECT e.association_snapshot FROM cerberus.trash_entry e
                            JOIN cerberus.trash_operation o ON o.id=e.operation_id CROSS JOIN session s
                            WHERE e.resource_kind='record' AND e.resource_id=@target AND o.account_id=s.account_id
                              AND s.profile_id IS NOT NULL AND e.association_snapshot=@history_bytes
                        ), collections AS (''')
s=s.replace('SELECT r.* FROM cerberus.record r JOIN cerberus.account owner', '''SELECT r.*,
                                CASE WHEN r.deleted_at IS NULL THEN r.folder_id ELSE
                                  (SELECT f.id FROM cerberus.folder f WHERE f.account_id=r.account_id AND f.public_id=@history_folder) END scope_folder_id,
                                (r.deleted_at IS NOT NULL AND r.account_id=s.account_id AND EXISTS(SELECT FROM history)
                                  AND (EXISTS(SELECT FROM cerberus.profile p WHERE p.id=s.profile_id AND p.public_id=ANY(@history_profiles))
                                    OR EXISTS(SELECT FROM collections c WHERE c.account_id=s.account_id AND c.public_id=ANY(@history_collections)))) history_direct
                            FROM cerberus.record r JOIN cerberus.account owner''')
s=s.replace('AND r.deleted_at IS NULL\n', 'AND (r.deleted_at IS NULL OR (r.account_id=s.account_id AND (s.profile_id IS NULL OR EXISTS(SELECT FROM history))))\n')
s=s.replace('ON f.id=r.folder_id AND f.account_id=r.account_id', 'ON f.id=r.scope_folder_id AND f.account_id=r.account_id')
s=s.replace('WHERE EXISTS(SELECT FROM cerberus.collection_record', '''WHERE (r.deleted_at IS NOT NULL AND r.account_id=c.account_id AND EXISTS(SELECT FROM history) AND c.public_id=ANY(@history_collections))
                               OR EXISTS(SELECT FROM cerberus.collection_record''')
s=s.replace('(r.account_id=s.account_id AND (s.profile_id IS NULL', '(r.account_id=s.account_id AND (s.profile_id IS NULL OR r.history_direct')
# Above replace also hit candidates condition where history_direct alias unavailable: undo there.
s=s.replace('(r.account_id=s.account_id AND (s.profile_id IS NULL OR r.history_direct OR EXISTS(SELECT FROM history)))', '(r.account_id=s.account_id AND (s.profile_id IS NULL OR EXISTS(SELECT FROM history)))')
s=s.replace('NOT EXISTS(SELECT FROM ancestry t WHERE t.record_id=r.id AND (t.hidden OR t.cycle))\n                              AND (r.folder_id IS NULL OR EXISTS(SELECT FROM ancestry t WHERE t.record_id=r.id AND t.parent_folder_id IS NULL))', '''r.history_direct OR (NOT EXISTS(SELECT FROM ancestry t WHERE t.record_id=r.id AND (t.hidden OR t.cycle))
                              AND (r.scope_folder_id IS NULL OR EXISTS(SELECT FROM ancestry t WHERE t.record_id=r.id AND t.parent_folder_id IS NULL)))''')
Path('src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordPermanentDeleteStore.cs').write_text(s)
