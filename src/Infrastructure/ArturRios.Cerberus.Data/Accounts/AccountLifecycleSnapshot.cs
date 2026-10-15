using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.Data.Accounts;

internal static class AccountLifecycleSnapshot
{
    internal static Task<string> ReadAsync(AppDbContext db, long owner, long[] roots, long[] recipients, bool reducing, CancellationToken ct) =>
        db.Database.SqlQueryRaw<string>("""
            WITH retained(kind,public_id) AS (
              SELECT 'account',public_id FROM cerberus.account WHERE id=ANY(@roots)
              UNION SELECT 'profile',public_id FROM cerberus.profile WHERE account_id=@owner
              UNION SELECT 'record',public_id FROM cerberus.record WHERE account_id=@owner
              UNION SELECT 'folder',public_id FROM cerberus.folder WHERE account_id=@owner
              UNION SELECT 'collection',c.public_id FROM cerberus.collection c WHERE c.account_id=@owner
                OR EXISTS(SELECT FROM cerberus.collection_grant g WHERE g.collection_id=c.id AND g.recipient_account_id=@owner)
              UNION SELECT 'grant',g.public_id FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id
                WHERE c.account_id=@owner OR EXISTS(SELECT FROM cerberus.collection_grant incoming WHERE incoming.collection_id=c.id AND incoming.recipient_account_id=@owner)
              UNION SELECT 'grant',public_id FROM cerberus.software_grant WHERE account_id=@owner)
            SELECT jsonb_build_object(
            'terminal',(SELECT jsonb_agg(to_jsonb(e) ORDER BY resource_kind,resource_id) FROM cerberus.terminal_erasure e
              WHERE EXISTS(SELECT FROM retained r WHERE r.kind=e.resource_kind AND r.public_id=e.resource_id)),
            'accounts',(SELECT jsonb_agg(CASE WHEN r.id=@owner THEN to_jsonb(r)-ARRAY['state','revision','revocation_generation','closure_requested_at','closure_purge_at','concurrency_stamp']
              WHEN r.id=ANY(@recipients) THEN to_jsonb(r)-ARRAY['revocation_generation','concurrency_stamp'] ELSE to_jsonb(r) END ORDER BY r.id)
              FROM cerberus.account r WHERE r.id=ANY(@roots)),
            'incoming_collections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c.id) FROM cerberus.collection c
              WHERE c.account_id<>@owner AND EXISTS(SELECT FROM cerberus.collection_grant g WHERE g.collection_id=c.id AND g.recipient_account_id=@owner)),
            'incoming_siblings',(SELECT jsonb_agg(to_jsonb(g) ORDER BY g.id) FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id
              WHERE c.account_id<>@owner AND g.recipient_account_id<>@owner
                AND EXISTS(SELECT FROM cerberus.collection_grant incoming WHERE incoming.collection_id=c.id AND incoming.recipient_account_id=@owner)),
            'native_grants',(SELECT jsonb_agg(CASE WHEN @reducing THEN to_jsonb(g)-ARRAY['state','revision','recipient_key_envelope','concurrency_stamp'] ELSE to_jsonb(g) END ORDER BY g.id)
              FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id WHERE c.account_id=@owner OR g.recipient_account_id=@owner),
            'software_grants',(SELECT jsonb_agg(CASE WHEN @reducing THEN to_jsonb(g)-ARRAY['state','revision','recipient_key','concurrency_stamp'] ELSE to_jsonb(g) END ORDER BY g.id)
              FROM cerberus.software_grant g WHERE g.account_id=@owner),
            'software_record_targets',CASE WHEN @reducing THEN NULL ELSE (SELECT jsonb_agg(to_jsonb(r) ORDER BY grant_id,record_id) FROM cerberus.software_grant_record r WHERE r.account_id=@owner) END,
            'software_collection_targets',CASE WHEN @reducing THEN NULL ELSE (SELECT jsonb_agg(to_jsonb(r) ORDER BY grant_id,collection_id) FROM cerberus.software_grant_collection r WHERE r.account_id=@owner) END,
            'vault_protection',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.vault_protection r WHERE r.account_id=@owner),
            'vault_recovery_operation',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.vault_recovery_operation r WHERE r.account_id=@owner),
            'profile',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.profile r WHERE r.account_id=@owner),
            'record',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.record r WHERE r.account_id=@owner),
            'folder',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.folder r WHERE r.account_id=@owner),
            'collection',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.collection r WHERE r.account_id=@owner),
            'profile_record',(SELECT jsonb_agg(to_jsonb(r) ORDER BY profile_id,record_id) FROM cerberus.profile_record r WHERE r.account_id=@owner),
            'profile_folder',(SELECT jsonb_agg(to_jsonb(r) ORDER BY profile_id,folder_id) FROM cerberus.profile_folder r WHERE r.account_id=@owner),
            'collection_record',(SELECT jsonb_agg(to_jsonb(r) ORDER BY collection_id,record_id) FROM cerberus.collection_record r WHERE r.account_id=@owner),
            'collection_folder',(SELECT jsonb_agg(to_jsonb(r) ORDER BY collection_id,folder_id) FROM cerberus.collection_folder r WHERE r.account_id=@owner),
            'trash_operation',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.trash_operation r WHERE r.account_id=@owner),
            'sync_stream',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.sync_stream r WHERE r.account_id=@owner),
            'sync_inventory',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.sync_inventory r JOIN cerberus.sync_stream s ON s.id=r.stream_id WHERE s.account_id=@owner),
            'sync_change',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.sync_change r JOIN cerberus.sync_stream s ON s.id=r.stream_id WHERE s.account_id=@owner),
            'sync_operation',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.sync_operation r WHERE r.account_id=@owner),
            'profile_collection',(SELECT jsonb_agg(to_jsonb(r) ORDER BY profile_id,collection_id) FROM cerberus.profile_collection r JOIN cerberus.profile p ON p.id=r.profile_id WHERE p.account_id=@owner),
            'trash_entry',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.id) FROM cerberus.trash_entry r JOIN cerberus.trash_operation t ON t.id=r.operation_id WHERE t.account_id=@owner),
            'sessions',(SELECT jsonb_agg(CASE WHEN @reducing THEN to_jsonb(r)-'revoked' ELSE to_jsonb(r) END ORDER BY id) FROM cerberus.vault_access_session r WHERE r.account_id=@owner),
            'challenges',(SELECT jsonb_agg(CASE WHEN @reducing THEN to_jsonb(r)-'consumed' ELSE to_jsonb(r) END ORDER BY id) FROM cerberus.vault_unlock_challenge r WHERE r.account_id=@owner))::text AS "Value"
            """, new NpgsqlParameter("owner", owner), new NpgsqlParameter("reducing", reducing), new NpgsqlParameter("roots", roots), new NpgsqlParameter("recipients", recipients)).SingleAsync(ct);
}
