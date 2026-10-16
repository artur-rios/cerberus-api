namespace ArturRios.Cerberus.Data.Accounts.Export;

internal static class AccountExportRelationsSql
{
    internal const string Projections="""
        , incoming AS MATERIALIZED (
          SELECT c.id,c.public_id collection_id,owner.public_id owner_id,g.public_id grant_id,g.revision,g.access
          FROM cerberus.collection_grant g JOIN actor a ON a.id=g.recipient_account_id
          JOIN cerberus.collection c ON c.id=g.collection_id AND c.account_id<>a.id JOIN cerberus.account owner ON owner.id=c.account_id
          WHERE owner.state=0 AND c.deleted_at IS NULL AND c.purge_at IS NULL AND g.state=0 AND g.access IN(0,1)
            AND g.revision>0 AND g.revision<=9007199254740991
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE (e.resource_kind='account' AND e.resource_id=owner.public_id)
              OR(e.resource_kind='collection' AND e.resource_id=c.public_id) OR(e.resource_kind='grant' AND e.resource_id=g.public_id))
        ), eligible_collections AS MATERIALIZED (
          SELECT c.id,c.public_id FROM visible_collections c UNION ALL SELECT i.id,i.collection_id FROM incoming i
        ), outgoing AS MATERIALIZED (
          SELECT g.*,c.public_id collection_public_id,recipient.public_id recipient_public_id
          FROM cerberus.collection_grant g JOIN visible_collections c ON c.id=g.collection_id
          JOIN cerberus.account recipient ON recipient.id=g.recipient_account_id AND recipient.state=0
          WHERE NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE(e.resource_kind='grant' AND e.resource_id=g.public_id)
            OR(e.resource_kind='account' AND e.resource_id=recipient.public_id))
        ), software AS MATERIALIZED (
          SELECT g.* FROM cerberus.software_grant g JOIN actor a ON a.id=g.account_id
          WHERE NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='grant' AND e.resource_id=g.public_id)
        ), current_edges AS (
          SELECT 'profile'::text source_kind,p.public_id source_id,'record'::text target_kind,r.public_id target_id
          FROM cerberus.profile_record link JOIN visible_profiles p ON p.id=link.profile_id AND p.account_id=link.account_id
          JOIN visible_records r ON r.id=link.record_id AND r.account_id=link.account_id
          UNION ALL SELECT 'profile',p.public_id,'folder',f.public_id
          FROM cerberus.profile_folder link JOIN visible_profiles p ON p.id=link.profile_id AND p.account_id=link.account_id
          JOIN visible_folders f ON f.id=link.folder_id AND f.account_id=link.account_id
          UNION ALL SELECT 'profile',p.public_id,'collection',c.public_id
          FROM cerberus.profile_collection link JOIN visible_profiles p ON p.id=link.profile_id JOIN eligible_collections c ON c.id=link.collection_id
          UNION ALL SELECT 'collection',c.public_id,'record',r.public_id
          FROM cerberus.collection_record link JOIN visible_collections c ON c.id=link.collection_id AND c.account_id=link.account_id
          JOIN visible_records r ON r.id=link.record_id AND r.account_id=link.account_id
          UNION ALL SELECT 'collection',c.public_id,'folder',f.public_id
          FROM cerberus.collection_folder link JOIN visible_collections c ON c.id=link.collection_id AND c.account_id=link.account_id
          JOIN visible_folders f ON f.id=link.folder_id AND f.account_id=link.account_id
          UNION ALL SELECT 'record',r.public_id,'folder',f.public_id FROM visible_records r JOIN visible_folders f ON f.id=r.folder_id AND f.account_id=r.account_id
          UNION ALL SELECT 'folder',f.public_id,'folder',parent.public_id FROM visible_folders f JOIN visible_folders parent ON parent.id=f.parent_folder_id AND parent.account_id=f.account_id
        ), retained AS MATERIALIZED (SELECT n.* FROM natives n WHERE n.deleted_at IS NOT NULL),
        history_raw AS MATERIALIZED (
          SELECT n.kind,n.public_id,n.deleted_at,n.purge_at,o.public_id operation_id,o.root_resource_kind root_kind,o.root_resource_id root_id,
            o.deleted_at operation_deleted,o.purge_at operation_purge,e.association_snapshot
          FROM retained n JOIN cerberus.trash_entry e ON e.resource_kind=n.kind AND e.resource_id=n.public_id
          JOIN cerberus.trash_operation o ON o.id=e.operation_id JOIN actor a ON a.id=o.account_id CROSS JOIN guard g WHERE g.error IS NULL
        ), history_text AS MATERIALIZED (
          SELECT h.*,convert_from(h.association_snapshot,'UTF8') snapshot_text FROM history_raw h
        ), history_parsed AS MATERIALIZED (
          SELECT h.*,CASE WHEN h.snapshot_text IS JSON OBJECT WITH UNIQUE KEYS THEN h.snapshot_text::jsonb ELSE NULL END document FROM history_text h
        ), history_checked AS MATERIALIZED (
          SELECT h.*,COALESCE(h.document IS NOT NULL
            AND ARRAY(SELECT k.name FROM jsonb_object_keys(h.document) AS k(name) ORDER BY k.name)=CASE h.kind
              WHEN 'profile' THEN ARRAY['collectionIds','folderIds','recordIds'] WHEN 'record' THEN ARRAY['collectionIds','folderId','profileIds']
              WHEN 'folder' THEN ARRAY['collectionIds','parentFolderId','profileIds'] ELSE ARRAY['folderIds','profileIds','recordIds'] END
            AND NOT EXISTS(SELECT FROM jsonb_each(h.document) field WHERE field.key IN('profileIds','recordIds','folderIds','collectionIds') AND jsonb_typeof(field.value)<>'array')
            AND NOT EXISTS(SELECT FROM jsonb_each(h.document) field
              CROSS JOIN LATERAL jsonb_array_elements(CASE WHEN jsonb_typeof(field.value)='array' THEN field.value ELSE '[]'::jsonb END) item
              WHERE jsonb_typeof(item.value)<>'string' OR (item.value#>>ARRAY[]::text[])!~@uuid_pattern
                OR item.value#>>ARRAY[]::text[]='00000000-0000-0000-0000-000000000000')
            AND NOT EXISTS(SELECT FROM jsonb_each(h.document) field WHERE CASE WHEN jsonb_typeof(field.value)='array'
              THEN jsonb_array_length(field.value)<>(SELECT count(DISTINCT item.value) FROM jsonb_array_elements(field.value) item) ELSE false END)
            AND CASE WHEN h.kind IN('record','folder') THEN
              jsonb_typeof(h.document->CASE h.kind WHEN 'record' THEN 'folderId' ELSE 'parentFolderId' END)='null'
              OR(jsonb_typeof(h.document->CASE h.kind WHEN 'record' THEN 'folderId' ELSE 'parentFolderId' END)='string'
                AND (h.document->>CASE h.kind WHEN 'record' THEN 'folderId' ELSE 'parentFolderId' END)~@uuid_pattern
                AND (h.document->>CASE h.kind WHEN 'record' THEN 'folderId' ELSE 'parentFolderId' END)<>'00000000-0000-0000-0000-000000000000')
              ELSE true END,false) valid FROM history_parsed h
        ), relation_guard AS MATERIALIZED (
          SELECT CASE WHEN EXISTS(SELECT FROM retained n CROSS JOIN guard g WHERE g.error IS NULL AND NOT EXISTS(SELECT FROM history_raw h WHERE h.kind=n.kind AND h.public_id=n.public_id))
            OR EXISTS(SELECT FROM history_checked h WHERE NOT h.valid OR h.deleted_at<>h.operation_deleted OR h.purge_at<>h.operation_purge
              OR NOT EXISTS(SELECT FROM retained root WHERE root.kind=h.root_kind AND root.public_id=h.root_id AND root.deleted_at=h.operation_deleted AND root.purge_at=h.operation_purge))
            THEN 'persistence_unavailable' ELSE NULL END error
        ), history_roots AS MATERIALIZED (
          SELECT DISTINCT h.operation_id,h.root_kind,h.root_id,h.operation_deleted,h.operation_purge FROM history_checked h
        ), historical_edges AS (
          SELECT h.operation_id,'profile'::text source_kind,h.public_id source_id,'record'::text target_kind,r.public_id target_id
          FROM history_checked h CROSS JOIN LATERAL jsonb_array_elements_text(h.document->'recordIds') id JOIN visible_records r ON r.public_id=id.value::uuid WHERE h.kind='profile'
          UNION ALL SELECT h.operation_id,'profile',h.public_id,'folder',f.public_id
          FROM history_checked h CROSS JOIN LATERAL jsonb_array_elements_text(h.document->'folderIds') id JOIN visible_folders f ON f.public_id=id.value::uuid WHERE h.kind='profile'
          UNION ALL SELECT h.operation_id,'profile',h.public_id,'collection',c.public_id
          FROM history_checked h CROSS JOIN LATERAL jsonb_array_elements_text(h.document->'collectionIds') id JOIN eligible_collections c ON c.public_id=id.value::uuid WHERE h.kind='profile'
          UNION ALL SELECT h.operation_id,'profile',p.public_id,h.kind,h.public_id
          FROM history_checked h CROSS JOIN LATERAL jsonb_array_elements_text(h.document->'profileIds') id JOIN visible_profiles p ON p.public_id=id.value::uuid WHERE h.kind IN('record','folder','collection')
          UNION ALL SELECT h.operation_id,'collection',c.public_id,h.kind,h.public_id
          FROM history_checked h CROSS JOIN LATERAL jsonb_array_elements_text(h.document->'collectionIds') id JOIN visible_collections c ON c.public_id=id.value::uuid WHERE h.kind IN('record','folder')
          UNION ALL SELECT h.operation_id,'record',h.public_id,'folder',f.public_id FROM history_checked h JOIN visible_folders f ON f.public_id=(h.document->>'folderId')::uuid WHERE h.kind='record'
          UNION ALL SELECT h.operation_id,'folder',h.public_id,'folder',f.public_id FROM history_checked h JOIN visible_folders f ON f.public_id=(h.document->>'parentFolderId')::uuid WHERE h.kind='folder'
          UNION ALL SELECT h.operation_id,'collection',h.public_id,'record',r.public_id
          FROM history_checked h CROSS JOIN LATERAL jsonb_array_elements_text(h.document->'recordIds') id JOIN visible_records r ON r.public_id=id.value::uuid WHERE h.kind='collection'
          UNION ALL SELECT h.operation_id,'collection',h.public_id,'folder',f.public_id
          FROM history_checked h CROSS JOIN LATERAL jsonb_array_elements_text(h.document->'folderIds') id JOIN visible_folders f ON f.public_id=id.value::uuid WHERE h.kind='collection'
        ), metadata_entries AS (
          SELECT jsonb_build_object('entryType','relationship','relationshipKind','current','sourceKind',e.source_kind,'sourceId',e.source_id,
            'targetKind',e.target_kind,'targetId',e.target_id,'operationId',NULL) body FROM current_edges e
          UNION ALL SELECT DISTINCT jsonb_build_object('entryType','relationship','relationshipKind','historical','sourceKind',e.source_kind,'sourceId',e.source_id,
            'targetKind',e.target_kind,'targetId',e.target_id,'operationId',e.operation_id) FROM historical_edges e
          UNION ALL SELECT jsonb_build_object('entryType','collection-grant','grantId',g.public_id,'collectionId',g.collection_public_id,'recipientAccountId',g.recipient_public_id,
            'access',CASE g.access WHEN 0 THEN 'read-only' WHEN 1 THEN 'read-write' END,'state',CASE g.state WHEN 0 THEN 'active' WHEN 1 THEN 'revoked' END,
            'revision',g.revision,'recipientKeyEnvelope',replace(encode(g.recipient_key_envelope,'base64'),chr(10),'')) FROM outgoing g
          UNION ALL SELECT jsonb_build_object('entryType','shared-collection-reference','collectionId',i.collection_id,'ownerAccountId',i.owner_id,
            'grantId',i.grant_id,'grantRevision',i.revision,'access',CASE i.access WHEN 0 THEN 'read-only' WHEN 1 THEN 'read-write' END) FROM incoming i
          UNION ALL SELECT jsonb_build_object('entryType','software-grant','grantId',g.public_id,'softwareId',g.heimdall_software_public_id,
            'state',CASE g.state WHEN 0 THEN 'active' WHEN 1 THEN 'revoked' END,'revision',g.revision,'recipientKey',replace(encode(g.recipient_key,'base64'),chr(10),'')) FROM software g
          UNION ALL SELECT jsonb_build_object('entryType','software-target','grantId',g.public_id,'resourceKind','record','resourceId',r.public_id,
            'recipientKeyEnvelope',replace(encode(link.recipient_key_envelope,'base64'),chr(10),''))
          FROM cerberus.software_grant_record link JOIN software g ON g.id=link.grant_id AND g.account_id=link.account_id JOIN visible_records r ON r.id=link.record_id AND r.account_id=link.account_id
          UNION ALL SELECT jsonb_build_object('entryType','software-target','grantId',g.public_id,'resourceKind','collection','resourceId',c.public_id,
            'recipientKeyEnvelope',replace(encode(link.recipient_key_envelope,'base64'),chr(10),''))
          FROM cerberus.software_grant_collection link JOIN software g ON g.id=link.grant_id AND g.account_id=link.account_id JOIN visible_collections c ON c.id=link.collection_id AND c.account_id=link.account_id
          UNION ALL SELECT jsonb_build_object('entryType','trash-operation','operationId',h.operation_id,'rootKind',h.root_kind,'rootId',h.root_id,
            'deletedAt',h.operation_deleted,'purgeAt',h.operation_purge) FROM history_roots h
        ), all_entries AS (SELECT body FROM native_entries UNION ALL SELECT body FROM metadata_entries),
        resource_dependencies AS MATERIALIZED (
          SELECT n.kind,n.public_id,'folder'::text dependency_kind,unnest(t.path) dependency_id FROM natives n
          LEFT JOIN visible_folders f ON f.public_id=CASE n.kind WHEN 'folder' THEN n.public_id WHEN 'record' THEN n.parent END LEFT JOIN tree t ON t.id=f.id
          UNION ALL SELECT 'collection',i.collection_id,'grant',i.grant_id FROM incoming i
          UNION ALL SELECT 'collection',i.collection_id,'account',i.owner_id FROM incoming i
        )
        """;
    // Internal permission dependencies never become archive fields.
    private const string ResourceRefs="""
        SELECT refs.kind,refs.id FROM (
          VALUES
            (CASE WHEN e.body->>'entryType' IN('profile','record','folder','collection') THEN e.body->>'entryType'
                WHEN e.body->>'entryType'='relationship' THEN e.body->>'sourceKind'
                WHEN e.body->>'entryType'='software-target' THEN e.body->>'resourceKind'
                WHEN e.body->>'entryType'='trash-operation' THEN e.body->>'rootKind'
                WHEN e.body->>'entryType'='collection-grant' THEN 'collection' END,
              COALESCE(e.body->>'resourceId',e.body->>'sourceId',e.body->>'rootId',e.body->>'collectionId')::uuid),
            (CASE WHEN e.body->>'entryType'='relationship' THEN e.body->>'targetKind' END,(e.body->>'targetId')::uuid)
          UNION ALL SELECT h.root_kind,h.root_id FROM history_roots h WHERE h.operation_id=(e.body->>'operationId')::uuid
        ) refs(kind,id) WHERE refs.kind IS NOT NULL AND refs.id IS NOT NULL
        """;
    internal const string Dependencies="""
        LEFT JOIN LATERAL (
          SELECT COALESCE(jsonb_agg(DISTINCT jsonb_build_object('kind',d.kind,'id',d.id)),'[]'::jsonb)::text value
          FROM (
        """+"\n"+ResourceRefs+"\n"+"""
            UNION ALL SELECT n.dependency_kind,n.dependency_id FROM resource_dependencies n JOIN (
        """+"\n"+ResourceRefs+"\n"+"""
            ) refs ON refs.kind=n.kind AND refs.id=n.public_id
          ) d
        ) dependencies ON true
        """;
}
