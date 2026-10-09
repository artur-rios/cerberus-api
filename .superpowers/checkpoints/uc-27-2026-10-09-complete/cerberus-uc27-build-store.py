from pathlib import Path
w=Path('/tmp/cerberus-uc27-worktree');destination=w/'src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderTrashStore.cs';assert not destination.exists()
s=Path('/tmp/cerberus-uc27-store-main.cs').read_text()
old=(w/'src/Infrastructure/ArturRios.Cerberus.Data/Records/RecordTrashStore.cs').read_text();start=old.index('    private sealed class ParentMetadata');end=old.index('    private static bool Safe',start);bump=old[start:end]
for f in ['Id','Revision','Sequence']:bump=bump.replace('parent.'+f,'parent.Row.'+f)
s+=bump
s+='''    private const string InventoryCtes="""
        , tree AS (
            SELECT f.*,ARRAY[f.id] path,false cycle FROM visible f CROSS JOIN session s WHERE f.account_id=s.account_id
            UNION ALL
            SELECT f.*,t.path||f.id,f.id=ANY(t.path) FROM tree t JOIN cerberus.folder f
                ON f.parent_folder_id=t.id AND f.account_id=t.account_id
            WHERE NOT t.cycle AND f.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
        ), members AS MATERIALIZED (
            SELECT r.* FROM cerberus.record r JOIN tree t ON r.folder_id=t.id AND r.account_id=t.account_id
            WHERE r.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='record' AND e.resource_id=r.public_id)
        )
        """;
'''
def resource(alias,parent):
 return f"jsonb_build_object('id',{alias}.id,'publicId',{alias}.public_id,'accountId',{alias}.account_id,'revision',{alias}.revision,'sequence',{alias}.server_sequence,'editedAt',{alias}.edited_at,'envelope',encode({alias}.envelope,'hex'),'parent',{alias}.{parent},'purgeAt',{alias}.purge_at)"
def external(alias,kind):
 return f"jsonb_build_object('id',{alias}.id,'publicId',{alias}.public_id,'revision',{alias}.revision,'sequence',{alias}.server_sequence,'editedAt',{alias}.edited_at,'deletedAt',{alias}.deleted_at,'terminal',EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='{kind}' AND e.resource_id={alias}.public_id))"
def array(expr,source,order):return f"COALESCE((SELECT jsonb_agg({expr} ORDER BY {order}) FROM {source}),'[]'::jsonb)"
folders=array(resource('f','parent_folder_id'),'tree f','f.public_id');records=array(resource('r','folder_id'),'members r','r.public_id')
profiles=array(external('p','profile'),"cerberus.profile p CROSS JOIN session s WHERE p.account_id=s.account_id AND (p.id=s.profile_id OR EXISTS(SELECT FROM cerberus.profile_folder pf JOIN tree t ON t.id=pf.folder_id WHERE pf.profile_id=p.id) OR EXISTS(SELECT FROM cerberus.profile_record pr JOIN members r ON r.id=pr.record_id WHERE pr.profile_id=p.id))",'p.public_id')
collections=array(external('c','collection'),"cerberus.collection c CROSS JOIN session s WHERE c.account_id=s.account_id AND (EXISTS(SELECT FROM cerberus.collection_folder cf JOIN tree t ON t.id=cf.folder_id WHERE cf.collection_id=c.id) OR EXISTS(SELECT FROM cerberus.collection_record cr JOIN members r ON r.id=cr.record_id WHERE cr.collection_id=c.id))",'c.public_id')
parent=f"(SELECT {external('f','folder')} FROM visible v JOIN cerberus.folder f ON f.id=v.parent_folder_id AND f.account_id=v.account_id)"
links=[]
for short,left,right,source in [('pf','profile','folder','tree'),('pr','profile','record','members'),('cf','collection','folder','tree'),('cr','collection','record','members')]:
 expr=f"jsonb_build_object('{left}Id',{short}.{left}_id,'{right}Id',{short}.{right}_id)";table=f"cerberus.{left}_{right} {short} JOIN {source} t ON t.id={short}.{right}_id";order=f'{short}.{left}_id,{short}.{right}_id';links.append(array(expr,table,order))
s+='    private const string InventorySelect="""\n        SELECT jsonb_build_object(\n'+f"            'folders',{folders},\n            'records',{records},\n            'profiles',{profiles},\n            'collections',{collections},\n            'parent',{parent},\n"+''.join(f"            '{k}',{v},\n" for k,v in zip(['profileFolders','profileRecords','collectionFolders','collectionRecords'],links))+"            'cycle',EXISTS(SELECT FROM tree WHERE cycle))::text AS value\n        \"\"\";\n"
source=(w/'src/Infrastructure/ArturRios.Cerberus.Data/Folders/FolderUpdateStore.cs').read_text();tail=source[source.index('    private static bool Safe'):];tail=tail.replace('FolderUpdateRequest','FolderTrashRequest');s+=tail
# Raw full-row snapshot SQL is reused only for current root scope/native evidence;
# owned cascade snapshots never deserialize ciphertext as an envelope.
destination.write_text(s)
print('Implemented complete folder cascade store after genuine missing-store RED:',len(s.splitlines()),'lines')
