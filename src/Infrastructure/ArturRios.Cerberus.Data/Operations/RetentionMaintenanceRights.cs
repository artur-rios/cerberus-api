using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.Data.Operations;

internal static class RetentionMaintenanceRights
{
    internal static async Task RequireAsync(AppDbContext db,CancellationToken ct)
    {
        // A comma-separated privilege string means ANY in PostgreSQL. Require
        // every capability independently, including UPDATE for row locks.
        var permitted=await db.Database.SqlQuery<bool>($"""
            SELECT has_schema_privilege(current_user,'cerberus','USAGE')
              AND has_sequence_privilege(current_user,'cerberus.server_sequence','USAGE')
              AND NOT EXISTS(
                SELECT FROM(VALUES
                  ('account',ARRAY['SELECT','UPDATE','DELETE']),
                  ('profile',ARRAY['SELECT','UPDATE','DELETE']),
                  ('record',ARRAY['SELECT','UPDATE','DELETE']),
                  ('folder',ARRAY['SELECT','UPDATE','DELETE']),
                  ('collection',ARRAY['SELECT','UPDATE','DELETE']),
                  ('vault_protection',ARRAY['SELECT']),
                  ('vault_access_session',ARRAY['SELECT','UPDATE']),
                  ('vault_unlock_challenge',ARRAY['SELECT']),
                  ('vault_recovery_operation',ARRAY['SELECT']),
                  ('registration_operation',ARRAY['SELECT','DELETE']),
                  ('retention_work_item',ARRAY['SELECT','INSERT','UPDATE','DELETE']),
                  ('terminal_erasure',ARRAY['SELECT','INSERT','UPDATE']),
                  ('trash_operation',ARRAY['SELECT','UPDATE','DELETE']),
                  ('trash_entry',ARRAY['SELECT','UPDATE','DELETE']),
                  ('profile_record',ARRAY['SELECT']),('profile_folder',ARRAY['SELECT']),('profile_collection',ARRAY['SELECT']),
                  ('collection_record',ARRAY['SELECT']),('collection_folder',ARRAY['SELECT']),('collection_grant',ARRAY['SELECT']),
                  ('software_grant',ARRAY['SELECT']),('software_grant_record',ARRAY['SELECT']),('software_grant_collection',ARRAY['SELECT']),
                  ('sync_stream',ARRAY['SELECT','UPDATE','DELETE']),('sync_inventory',ARRAY['SELECT']),
                  ('sync_change',ARRAY['SELECT']),('sync_operation',ARRAY['SELECT'])
                ) AS required(table_name,permissions)
                CROSS JOIN LATERAL unnest(required.permissions) AS p(permission)
                WHERE NOT has_table_privilege(current_user,'cerberus.'||required.table_name,p.permission)
              ) AS "Value"
            """).SingleAsync(ct);
        if(!permitted)throw new UnauthorizedAccessException("Retention execution rights are unavailable.");
    }
}
