from pathlib import Path
w=Path('/tmp/cerberus-uc28-worktree');r=(w/'src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordMoveStore.cs').read_text();t=(w/'src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderTrashStore.cs').read_text();s=Path('/tmp/cerberus-uc28-store-main.txt').read_text()
s+=r[r.index('    private static bool ValidResultNative('):r.index('    private static async Task<Snapshot> SnapshotAsync(')].replace('RecordMoveRequest','FolderMoveRequest').replace('r.FolderId','r.ParentFolderId').replace('r.RecordId','r.FolderId')
s+='''    private static async Task<Snapshot> SnapshotAsync(AppDbContext db,FolderMoveRequest request,CancellationToken ct)
        =>(await db.Database.SqlQueryRaw<Snapshot>(Authority+SnapshotSelect,Parameters(request)).ToListAsync(ct)).Single();
    private sealed record Item(Guid FolderId,long Id,long AccountId,long Revision,long Sequence,DateTimeOffset EditedAt,string Envelope);
    private sealed record Member(string Kind,long Id,long Revision,long Sequence,DateTimeOffset EditedAt,DateTimeOffset? PurgeAt);
    private sealed class Snapshot
    {
        public bool ActorActive{get;set;}public bool Allowed{get;set;}public bool CorruptAncestry{get;set;}public bool Writable{get;set;}
        public string? Item{get;set;}public long[] Collections{get;set;}=[];public long[] GrantsIds{get;set;}=[];
        public string Grants{get;set;}="[]";public string? Protection{get;set;}
        public bool DestinationAllowed{get;set;}public bool DestinationCorrupt{get;set;}public bool InvalidCycle{get;set;}public bool MemberCorrupt{get;set;}public bool Subset{get;set;}
        public long? DestinationId{get;set;}public long[] SourceCollections{get;set;}=[];public long[] ResultCollections{get;set;}=[];public long[] ResultGrantIds{get;set;}=[];
        public string NativeEvidence{get;set;}="[]";public string Members{get;set;}="[]";public string Frozen{get;set;}="{}";
    }
'''
s+=r[r.index('    private sealed record ResultEvidence'):r.index('    private const string SourceSelect=')]
source=t[t.index('    private const string SnapshotSelect='):t.index('    private const string Authority=')].replace('SnapshotSelect','SourceSelect')
# Source select must remain extensible, with appended columns before FROM.
s+=source
s+='''    private const string SnapshotSelect=SourceSelect+"""
        ,m.allowed destination_allowed,m.corrupt destination_corrupt,m.invalid_cycle,m.subset,
          (EXISTS(SELECT FROM tree WHERE cycle) OR EXISTS(SELECT FROM before_paths WHERE cycle OR hidden)
            OR (NOT m.invalid_cycle AND EXISTS(SELECT FROM after_paths WHERE cycle OR hidden))) member_corrupt,
          (SELECT id FROM destination_visible) destination_id,
          ARRAY(SELECT id FROM source_collections ORDER BY id) source_collections,
          ARRAY(SELECT id FROM result_collections ORDER BY id) result_collections,
          ARRAY(SELECT id FROM result_grants ORDER BY public_id) result_grant_ids,
          n.evidence::text native_evidence,snapshot.evidence::text frozen,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('kind',r.kind,'id',r.id,'revision',r.revision,'sequence',r.server_sequence,
            'editedAt',r.edited_at,'purgeAt',r.purge_at) ORDER BY r.kind,r.public_id) FROM members r),'[]'::jsonb)::text members
        FROM move_scope m CROSS JOIN native_result n CROSS JOIN captured snapshot
        """;
'''
a=t[t.index('    private const string Authority='):];a=a[:a.rfind('\n}')].replace('private const string Authority','private const string SourceAuthority')
s+=a+'\n    private const string Authority=SourceAuthority+MoveAuthority;\n    private const string MoveAuthority="""\n'+Path('/tmp/cerberus-uc28-store-authority.txt').read_text()+'        """;\n}\n'
p=w/'src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderMoveStore.cs';assert not p.exists();p.write_text(s)
print('FolderMoveStore installed after missing-store RED:',len(s.splitlines()),'lines')
