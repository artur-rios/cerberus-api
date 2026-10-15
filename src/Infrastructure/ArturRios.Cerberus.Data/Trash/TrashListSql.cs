namespace ArturRios.Cerberus.Data.Trash;

internal static class TrashListSql
{
    internal const string Authority = """
        WITH RECURSIVE actor AS (
            SELECT a.* FROM cerberus.account a WHERE a.heimdall_public_id=@actor
        ), session AS (
            SELECT s.* FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id
            WHERE a.state=0 AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)
              AND s.handle_verifier=@verifier AND NOT s.revoked AND s.policy_revision>0 AND s.revocation_generation>0
              AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
              AND s.issued_at<=statement_timestamp()
              AND CASE WHEN a.renewal_enabled THEN s.expires_at>statement_timestamp() ELSE s.expires_at IS NULL END
              AND (s.profile_id IS NULL OR EXISTS(SELECT FROM cerberus.profile p WHERE p.id=s.profile_id AND p.account_id=a.id
                AND p.deleted_at IS NULL AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id)))
        )
        """;
    // No stored JSON is cast in SQL. Only strictly parsed canonical identifiers enter @history.
    internal const string Snapshot = Authority + """
        , history AS (
            SELECT h.* FROM jsonb_to_recordset(@history::jsonb) AS h(id bigint,bytes text,parent uuid,profiles uuid[],collections uuid[])
        ), collections AS (
            SELECT c.id,c.public_id,c.account_id FROM cerberus.collection c CROSS JOIN session s
            WHERE c.account_id=s.account_id AND c.deleted_at IS NULL
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='collection' AND e.resource_id=c.public_id)
              AND EXISTS(SELECT FROM cerberus.profile_collection pc WHERE pc.profile_id=s.profile_id AND pc.collection_id=c.id)
        ), operations AS MATERIALIZED (
            SELECT o.*,e.association_snapshot,h.parent,h.profiles,h.collections,
              (s.profile_id IS NULL OR (s.profile_id IS NOT NULL AND o.root_resource_kind<>'profile' AND h.id IS NOT NULL
                AND (EXISTS(SELECT FROM cerberus.profile p WHERE p.id=s.profile_id AND p.public_id=ANY(h.profiles))
                  OR EXISTS(SELECT FROM collections c WHERE c.public_id=ANY(h.collections))))) direct
            FROM cerberus.trash_operation o CROSS JOIN session s
            JOIN cerberus.trash_entry e ON e.operation_id=o.id
            LEFT JOIN history h ON h.id=o.id AND h.bytes=encode(e.association_snapshot,'hex')
            WHERE o.account_id=s.account_id AND o.purge_at>statement_timestamp()
              AND e.resource_kind=o.root_resource_kind AND e.resource_id=o.root_resource_id
              AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure t WHERE t.resource_kind=o.root_resource_kind AND t.resource_id=o.root_resource_id)
        ), ancestry AS (
            SELECT o.id operation_id,f.id,f.account_id,f.parent_folder_id,ARRAY[f.id] path,false cycle,
              (f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure t WHERE t.resource_kind='folder' AND t.resource_id=f.public_id)) hidden
            FROM operations o JOIN cerberus.folder f ON f.account_id=o.account_id AND f.public_id=o.parent
            WHERE NOT o.direct AND o.root_resource_kind IN ('record','folder')
            UNION ALL
            SELECT t.operation_id,f.id,f.account_id,f.parent_folder_id,t.path||f.id,f.id=ANY(t.path),
              t.hidden OR f.deleted_at IS NOT NULL OR EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id)
            FROM ancestry t JOIN cerberus.folder f ON f.id=t.parent_folder_id AND f.account_id=t.account_id WHERE NOT t.cycle
        ), scoped AS (
            SELECT o.* FROM operations o CROSS JOIN session s WHERE o.direct
              OR EXISTS(SELECT FROM ancestry t WHERE t.operation_id=o.id AND
                (EXISTS(SELECT FROM cerberus.profile_folder pf WHERE pf.account_id=s.account_id AND pf.profile_id=s.profile_id AND pf.folder_id=t.id)
                  OR EXISTS(SELECT FROM cerberus.collection_folder cf JOIN collections c ON c.id=cf.collection_id
                    WHERE cf.account_id=s.account_id AND cf.folder_id=t.id)))
        ), visible AS MATERIALIZED (
            SELECT o.* FROM scoped o WHERE o.direct OR
              (NOT EXISTS(SELECT FROM ancestry t WHERE t.operation_id=o.id AND (t.hidden OR t.cycle))
                AND EXISTS(SELECT FROM ancestry t WHERE t.operation_id=o.id AND t.parent_folder_id IS NULL))
        ), roots AS MATERIALIZED (
            SELECT o.id position,o.public_id operation_id,o.root_resource_kind kind,r.public_id resource_id,r.revision,r.server_sequence,
              r.edited_at,r.deleted_at,r.purge_at,r.envelope,NULL::bigint key_epoch,NULL::bytea key_wrappers,o.deleted_at operation_deleted,o.purge_at operation_purge
            FROM visible o JOIN cerberus.record r ON o.root_resource_kind='record' AND r.public_id=o.root_resource_id AND r.account_id=o.account_id
            UNION ALL
            SELECT o.id,o.public_id,o.root_resource_kind,f.public_id,f.revision,f.server_sequence,f.edited_at,f.deleted_at,f.purge_at,f.envelope,NULL::bigint,NULL::bytea,o.deleted_at,o.purge_at
            FROM visible o JOIN cerberus.folder f ON o.root_resource_kind='folder' AND f.public_id=o.root_resource_id AND f.account_id=o.account_id
            UNION ALL
            SELECT o.id,o.public_id,o.root_resource_kind,c.public_id,c.revision,c.server_sequence,c.edited_at,c.deleted_at,c.purge_at,c.envelope,c.key_epoch,NULL::bytea,o.deleted_at,o.purge_at
            FROM visible o JOIN cerberus.collection c ON o.root_resource_kind='collection' AND c.public_id=o.root_resource_id AND c.account_id=o.account_id
            UNION ALL
            SELECT o.id,o.public_id,o.root_resource_kind,p.public_id,p.revision,p.server_sequence,p.edited_at,p.deleted_at,p.purge_at,p.envelope,NULL::bigint,p.key_wrappers,o.deleted_at,o.purge_at
            FROM visible o JOIN cerberus.profile p ON o.root_resource_kind='profile' AND p.public_id=o.root_resource_id AND p.account_id=o.account_id
        ), retained AS MATERIALIZED (
            SELECT r.* FROM roots r WHERE r.deleted_at IS NOT NULL AND r.purge_at>statement_timestamp()
        ), boundary AS (
            SELECT COALESCE(@boundary::bigint,(SELECT MAX(position) FROM retained),0) value
        ), page AS MATERIALIZED (
            SELECT r.* FROM retained r CROSS JOIN boundary b WHERE r.position>@after AND r.position<=b.value
            ORDER BY r.position LIMIT @take
        )
        SELECT EXISTS(SELECT FROM actor a WHERE a.state=0 AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)) actor_active,
          EXISTS(SELECT FROM session) allowed,
          EXISTS(SELECT FROM scoped o JOIN ancestry t ON t.operation_id=o.id WHERE t.cycle AND NOT o.direct
            AND NOT EXISTS(SELECT FROM ancestry h WHERE h.operation_id=o.id AND h.hidden)) corrupt_ancestry,
          EXISTS(SELECT FROM retained r WHERE r.position<=0 OR r.position>@max OR r.revision<=0 OR r.revision>@max
            OR r.server_sequence<=0 OR r.server_sequence>@max OR r.deleted_at<>r.operation_deleted OR r.purge_at<>r.operation_purge
            OR NOT isfinite(r.deleted_at) OR NOT isfinite(r.purge_at) OR NOT isfinite(r.edited_at)
            OR r.deleted_at>statement_timestamp() OR r.purge_at-r.deleted_at<>interval '30 days') corrupt_metadata,
          (SELECT value FROM boundary) boundary,
          EXISTS(SELECT FROM page OFFSET @size) has_more,
          COALESCE((SELECT jsonb_agg(jsonb_build_object('position',p.position,'operationId',p.operation_id,'kind',p.kind,
            'resourceId',p.resource_id,'revision',p.revision,'serverSequence',p.server_sequence,'editedAt',p.edited_at,
            'deletedAt',p.deleted_at,'purgeAt',p.purge_at,'envelope',encode(p.envelope,'hex'),'keyEpoch',p.key_epoch,
            'keyWrappers',encode(p.key_wrappers,'hex')) ORDER BY p.position) FROM (SELECT * FROM page ORDER BY position LIMIT @size) p),'[]'::jsonb)::text items,
          (SELECT public_id FROM actor) owner_id,
          (SELECT encode(v.material,'hex') FROM cerberus.vault_protection v JOIN actor a ON a.id=v.account_id) material,
          (SELECT v.revision FROM cerberus.vault_protection v JOIN actor a ON a.id=v.account_id) protection_revision,
          (SELECT v.key_epoch FROM cerberus.vault_protection v JOIN actor a ON a.id=v.account_id) protection_epoch,
          (SELECT v.recovery_generation FROM cerberus.vault_protection v JOIN actor a ON a.id=v.account_id) protection_generation
        """;
}
