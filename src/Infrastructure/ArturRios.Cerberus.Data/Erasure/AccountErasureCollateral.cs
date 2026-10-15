using Microsoft.EntityFrameworkCore;
using Npgsql;
namespace ArturRios.Cerberus.Data.Erasure;

internal static class AccountErasureCollateral
{
    internal static Task<string> ReadAsync(AppDbContext db,long owner,long[] roots,long[] recipients,CancellationToken ct)=>db.Database.SqlQueryRaw<string>("""
        SELECT jsonb_build_object(
        'accounts',(SELECT jsonb_agg(CASE WHEN id=ANY(@recipients) THEN to_jsonb(a)-ARRAY['revocation_generation','concurrency_stamp'] ELSE to_jsonb(a) END ORDER BY id) FROM cerberus.account a WHERE id=ANY(@roots) AND id<>@owner),
        'profiles',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.profile r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.record r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'folders',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.folder r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'collections',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.collection r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'protection',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.vault_protection r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'sessions',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.vault_access_session r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'challenges',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.vault_unlock_challenge r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'grants',(SELECT jsonb_agg(to_jsonb(g) ORDER BY g.id) FROM cerberus.collection_grant g JOIN cerberus.collection c ON c.id=g.collection_id
          WHERE (c.account_id=ANY(@roots) OR g.recipient_account_id=ANY(@roots)) AND c.account_id<>@owner AND g.recipient_account_id<>@owner),
        'profile_collections',(SELECT jsonb_agg(to_jsonb(r) ORDER BY profile_id,collection_id) FROM cerberus.profile_collection r JOIN cerberus.profile p ON p.id=r.profile_id JOIN cerberus.collection c ON c.id=r.collection_id WHERE p.account_id=ANY(@roots) AND p.account_id<>@owner AND c.account_id<>@owner),
        'profile_records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY profile_id,record_id) FROM cerberus.profile_record r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'profile_folders',(SELECT jsonb_agg(to_jsonb(r) ORDER BY profile_id,folder_id) FROM cerberus.profile_folder r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'collection_records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY collection_id,record_id) FROM cerberus.collection_record r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'collection_folders',(SELECT jsonb_agg(to_jsonb(r) ORDER BY collection_id,folder_id) FROM cerberus.collection_folder r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'software',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.software_grant r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'software_records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY grant_id,record_id) FROM cerberus.software_grant_record r WHERE account_id=ANY(@roots) AND account_id<>@owner),
        'software_collections',(SELECT jsonb_agg(to_jsonb(r) ORDER BY grant_id,collection_id) FROM cerberus.software_grant_collection r WHERE account_id=ANY(@roots) AND account_id<>@owner))::text AS "Value"
        """,new NpgsqlParameter("owner",owner),new NpgsqlParameter("roots",roots),new NpgsqlParameter("recipients",recipients)).SingleAsync(ct);
}
