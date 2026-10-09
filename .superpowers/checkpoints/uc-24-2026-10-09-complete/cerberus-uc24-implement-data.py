from pathlib import Path
w=Path('/tmp/cerberus-uc24-worktree')
red=Path('/tmp/cerberus-uc24-data-red.log').read_text();assert 'error CS0246' in red and 'FolderListStore' in red
s=(w/'src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordListStore.cs').read_text().replace('Record','Folder').replace('record_id','folder_id').replace('recordId','folderId').replace('cerberus.record','cerberus.folder').replace("'record'","'folder'")
a=s.index('                ), admitted AS (');b=s.index('                ), candidates AS (',a)
s=s[:a]+'''                ), admitted AS (
                    SELECT f.id folder_id FROM session s JOIN cerberus.folder f ON f.account_id=s.account_id WHERE s.profile_id IS NULL
                    UNION
                    SELECT f.id FROM reachable f
'''+s[b:]
old='FROM candidates r JOIN cerberus.folder f ON f.id=r.folder_id AND f.account_id=r.account_id';assert old in s;s=s.replace(old,'FROM candidates r JOIN cerberus.folder f ON f.id=r.id AND f.account_id=r.account_id')
a=s.index('                ), included AS (');b=s.index('                ), visible AS (',a)
s=s[:a]+'''                ), included AS (
                    SELECT r.id folder_id,c.id collection_id,c.grant_id FROM candidates r JOIN collections c ON c.account_id=r.account_id
                    WHERE EXISTS(SELECT FROM cerberus.collection_folder cf JOIN ancestry t ON t.id=cf.folder_id
                        WHERE cf.collection_id=c.id AND cf.account_id=r.account_id AND t.folder_id=r.id)
                ), scoped AS (
                    SELECT r.* FROM candidates r CROSS JOIN session s WHERE
                      (r.account_id=s.account_id AND (s.profile_id IS NULL
                        OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN ancestry t ON t.id=pf.folder_id
                            WHERE pf.profile_id=s.profile_id AND pf.account_id=s.account_id AND t.folder_id=r.id)))
                      OR EXISTS(SELECT FROM included i WHERE i.folder_id=r.id)
'''+s[b:]
old='AND (r.folder_id IS NULL OR EXISTS(SELECT FROM ancestry t WHERE t.folder_id=r.id AND t.parent_folder_id IS NULL))';assert old in s;s=s.replace(old,'AND EXISTS(SELECT FROM ancestry t WHERE t.folder_id=r.id AND t.parent_folder_id IS NULL)')
old='''            foreach (var evidence in JsonSerializer.Deserialize<GrantEvidence[]>(snapshot.Grants, Json)!)
                if (!evidence.IsBound()) return new(Error: "persistence_unavailable");'''
assert old in s
s=s.replace(old,'''            var grants = JsonSerializer.Deserialize<GrantEvidence[]>(snapshot.Grants, Json)!;
            if (grants.Length > 0)
            {
                var first = grants[0];
                if (!CollectionGrantBinding.TryReadPins(Pins(first.RecipientMaterial, first.RecipientRevision,
                    first.RecipientEpoch, first.RecipientGeneration), out var recipient))
                    return new(Error: "persistence_unavailable");
                var owners = new Dictionary<Guid, ProtectionMaterial>();
                foreach (var evidence in grants)
                {
                    if (!owners.TryGetValue(evidence.OwnerId, out var owner))
                    {
                        if (!CollectionGrantBinding.TryReadPins(Pins(evidence.OwnerMaterial, evidence.OwnerRevision,
                            evidence.OwnerEpoch, evidence.OwnerGeneration), out owner))
                            return new(Error: "persistence_unavailable");
                        owners.Add(evidence.OwnerId, owner!);
                    }
                    if (!evidence.IsBound(owner!, recipient!)) return new(Error: "persistence_unavailable");
                }
            }''')
a=s.index('    private sealed record GrantEvidence(')
s=s[:a]+'''    private static VaultProtection? Pins(string? material, long? revision, long? epoch, long? generation) =>
        material is null || revision is null || epoch is null || generation is null ? null : new()
        { Material = Convert.FromHexString(material), Revision = revision.Value, KeyEpoch = epoch.Value, RecoveryGeneration = generation.Value };

    private sealed record GrantEvidence(Guid CollectionId, long CollectionEpoch, string CollectionEnvelope,
        Guid GrantId, long GrantRevision, string GrantEnvelope, Guid OwnerId, Guid RecipientIdentity,
        string? OwnerMaterial, long? OwnerRevision, long? OwnerEpoch, long? OwnerGeneration,
        string? RecipientMaterial, long? RecipientRevision, long? RecipientEpoch, long? RecipientGeneration)
    {
        internal bool IsBound(ProtectionMaterial owner, ProtectionMaterial recipient) =>
            CollectionGrantBinding.IsBound(new() { PublicId = GrantId, Revision = GrantRevision, RecipientKeyEnvelope = Convert.FromHexString(GrantEnvelope) },
                new() { PublicId = CollectionId, KeyEpoch = CollectionEpoch, Envelope = Convert.FromHexString(CollectionEnvelope) }, OwnerId, RecipientIdentity, owner, recipient);
    }
}
'''
s=s.replace('// Admit only current own/direct/member/descendant routes before walking full ancestry.','// Admit only current own/direct/member/descendant folders before walking full ancestry.').replace('// descendant routes; complete upward paths then diagnose only relevant active cycles.','// descendant routes; complete self/upward paths then diagnose only relevant active cycles.')
assert 'cerberus.profile_record' not in s and 'cerberus.collection_record' not in s
assert 'public sealed folder' not in s and 'private sealed folder' not in s
p=w/'src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderListStore.cs';assert not p.exists();p.write_text(s)
l=w/'.superpowers/sdd/2026-10-09-uc-24-list-folders/progress.md';l.write_text(l.read_text()+'\nTask2 installed actual Data matrix missing FolderListStore compile RED observed data-red.log BEFORE store. Added minimal one-snapshot admitted-folder/self+upward ancestry/native all-visible contributor read with distinct-account pin reuse, no record-based scope route or writes. Focused Data GREEN next; installer zero-match attempt excluded from RED.\n')
print('FolderListStore implemented only after genuine installed missing-store RED; no record-only admission or read mutations.')
