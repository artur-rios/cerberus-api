namespace ArturRios.Cerberus.Data.Trash;

internal static class TrashEmptySql
{
    internal const string Authority=TrashListSql.Authority+"""
        SELECT EXISTS(SELECT FROM actor a WHERE a.state=0 AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure t WHERE t.resource_kind='account' AND t.resource_id=a.public_id)) active,
          EXISTS(SELECT FROM session) allowed,EXISTS(SELECT FROM session WHERE profile_id IS NULL) wide,
          COALESCE((SELECT id FROM actor),0) owner_id,COALESCE((SELECT public_id FROM actor),'00000000-0000-0000-0000-000000000000'::uuid) account_id,
          COALESCE((SELECT revision FROM actor),0) revision,statement_timestamp() now
        """;
    internal const string Admit=TrashListSql.Authority+"""
        INSERT INTO cerberus.retention_work_item AS w(public_id,operation_key,due_at,claim_token,claim_expires_at,completed_at,attempts,concurrency_stamp)
        SELECT @public,@key,statement_timestamp(),NULL,NULL,NULL,0,@stamp FROM session s JOIN actor a ON a.id=s.account_id
        WHERE s.account_id=@owner AND s.profile_id IS NULL AND a.revision=@expected
        ON CONFLICT(operation_key) DO UPDATE SET due_at=LEAST(w.due_at,EXCLUDED.due_at),
          claim_token=CASE WHEN @invalidate OR w.completed_at IS NOT NULL THEN NULL ELSE w.claim_token END,
          claim_expires_at=CASE WHEN @invalidate OR w.completed_at IS NOT NULL THEN NULL ELSE w.claim_expires_at END,
          completed_at=NULL,
          concurrency_stamp=CASE WHEN @invalidate OR w.completed_at IS NOT NULL OR w.due_at>EXCLUDED.due_at THEN EXCLUDED.concurrency_stamp ELSE w.concurrency_stamp END
        RETURNING public_id work_id,statement_timestamp() admitted_at
        """;
}
