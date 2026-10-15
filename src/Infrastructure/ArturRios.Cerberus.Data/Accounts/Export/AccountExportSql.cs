using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using Npgsql;

namespace ArturRios.Cerberus.Data.Accounts.Export;

internal static class AccountExportSql
{
    // One frozen clock and one insensitive cursor view supply every package field.
    // Fresh READ COMMITTED fences reuse authority only, never package projections.
    private const string Authority="""
        WITH RECURSIVE clock AS MATERIALIZED (SELECT statement_timestamp() now),
        actor AS MATERIALIZED (SELECT a.* FROM cerberus.account a WHERE a.heimdall_public_id=@actor),
        session AS MATERIALIZED (
          SELECT s.* FROM cerberus.vault_access_session s JOIN actor a ON a.id=s.account_id CROSS JOIN clock t
          WHERE s.handle_verifier=@verifier AND NOT s.revoked AND s.profile_id IS NULL
            AND s.policy_revision>0 AND s.revocation_generation>0
            AND s.policy_revision=a.policy_revision AND s.revocation_generation=a.revocation_generation
            AND s.issued_at<=t.now AND isfinite(s.issued_at)
            AND CASE WHEN a.renewal_enabled THEN s.expires_at>t.now AND isfinite(s.expires_at) ELSE s.expires_at IS NULL END
        ), guard AS MATERIALIZED (
          SELECT CASE
            WHEN NOT EXISTS(SELECT FROM actor a WHERE a.state=0 AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='account' AND e.resource_id=a.public_id)) THEN 'not_found'
            WHEN NOT EXISTS(SELECT FROM session) THEN 'vault_access_denied'
            WHEN @initial AND (@iat<=0 OR @iat>floor(extract(epoch FROM t.now)) OR floor(extract(epoch FROM t.now))-@iat>=60) THEN 'fresh_authentication_required'
            WHEN EXISTS(SELECT FROM actor a WHERE a.revision<>@expected) THEN 'revision_conflict'
            ELSE NULL END error FROM clock t
        )
        """;
    private const string Native="""
        , folders AS MATERIALIZED (
          SELECT f.*,(f.deleted_at IS NULL OR f.purge_at>t.now)
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='folder' AND e.resource_id=f.public_id) state_ok
          FROM cerberus.folder f JOIN actor a ON a.id=f.account_id CROSS JOIN clock t
        ), tree AS (
          SELECT f.id,f.parent_folder_id,f.state_ok visible FROM folders f WHERE f.parent_folder_id IS NULL
          UNION ALL
          SELECT f.id,f.parent_folder_id,t.visible AND f.state_ok FROM folders f JOIN tree t ON t.id=f.parent_folder_id
        ), visible_folders AS MATERIALIZED (SELECT f.* FROM folders f JOIN tree t ON t.id=f.id WHERE t.visible),
        visible_profiles AS MATERIALIZED (
          SELECT p.* FROM cerberus.profile p JOIN actor a ON a.id=p.account_id CROSS JOIN clock t
          WHERE (p.deleted_at IS NULL OR p.purge_at>t.now)
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='profile' AND e.resource_id=p.public_id)
        ), visible_records AS MATERIALIZED (
          SELECT r.* FROM cerberus.record r JOIN actor a ON a.id=r.account_id CROSS JOIN clock t
          WHERE (r.deleted_at IS NULL OR r.purge_at>t.now)
            AND (r.folder_id IS NULL OR EXISTS(SELECT FROM visible_folders f WHERE f.id=r.folder_id))
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='record' AND e.resource_id=r.public_id)
        ), visible_collections AS MATERIALIZED (
          SELECT c.* FROM cerberus.collection c JOIN actor a ON a.id=c.account_id CROSS JOIN clock t
          WHERE (c.deleted_at IS NULL OR c.purge_at>t.now)
            AND NOT EXISTS(SELECT FROM cerberus.terminal_erasure e WHERE e.resource_kind='collection' AND e.resource_id=c.public_id)
        ), natives AS MATERIALIZED (
          SELECT 'profile' kind,p.public_id,p.revision,p.server_sequence,p.edited_at,p.deleted_at,p.purge_at,p.envelope,p.key_wrappers,NULL::bigint key_epoch,NULL::uuid parent FROM visible_profiles p
          UNION ALL SELECT 'record',r.public_id,r.revision,r.server_sequence,r.edited_at,r.deleted_at,r.purge_at,r.envelope,NULL,NULL,f.public_id FROM visible_records r LEFT JOIN visible_folders f ON f.id=r.folder_id
          UNION ALL SELECT 'folder',f.public_id,f.revision,f.server_sequence,f.edited_at,f.deleted_at,f.purge_at,f.envelope,NULL,NULL,parent.public_id FROM visible_folders f LEFT JOIN visible_folders parent ON parent.id=f.parent_folder_id
          UNION ALL SELECT 'collection',c.public_id,c.revision,c.server_sequence,c.edited_at,c.deleted_at,c.purge_at,c.envelope,NULL,c.key_epoch,NULL FROM visible_collections c
        ), native_guard AS MATERIALIZED (
          SELECT CASE WHEN EXISTS(SELECT FROM folders f WHERE f.state_ok AND NOT EXISTS(SELECT FROM tree t WHERE t.id=f.id))
            OR NOT EXISTS(SELECT FROM cerberus.vault_protection p JOIN actor a ON a.id=p.account_id)
            OR EXISTS(SELECT FROM actor a WHERE CASE WHEN a.renewal_enabled THEN a.renewal_interval IS NULL
              OR extract(epoch FROM a.renewal_interval)<=0 OR extract(epoch FROM a.renewal_interval)>9007199254740991
              OR extract(epoch FROM a.renewal_interval)<>floor(extract(epoch FROM a.renewal_interval)) ELSE a.renewal_interval IS NOT NULL END)
            THEN 'persistence_unavailable' ELSE NULL END error
        ), header AS MATERIALIZED (
          SELECT jsonb_build_object('accountId',a.public_id,'identityId',a.heimdall_public_id,'revision',a.revision,
            'policyRevision',a.policy_revision,'revocationGeneration',a.revocation_generation,'renewalEnabled',a.renewal_enabled,
            'renewalIntervalSeconds',CASE WHEN a.renewal_interval IS NULL THEN NULL ELSE extract(epoch FROM a.renewal_interval)::bigint END,
            'serverTime',t.now)::text value FROM actor a CROSS JOIN clock t
        ), native_entries AS (
          SELECT jsonb_build_object('entryType','account','details',replace(encode(a.details_envelope,'base64'),chr(10),'')) body FROM actor a
          UNION ALL SELECT jsonb_build_object('entryType','protection','revision',p.revision,'keyEpoch',p.key_epoch,'recoveryGeneration',p.recovery_generation,
            'material',replace(encode(p.material,'base64'),chr(10),'')) FROM cerberus.vault_protection p JOIN actor a ON a.id=p.account_id
          UNION ALL SELECT jsonb_build_object('entryType',n.kind,'resourceId',n.public_id,'revision',n.revision,'serverSequence',n.server_sequence,
            'editedAt',n.edited_at,'deletedAt',n.deleted_at,'purgeAt',n.purge_at,'envelope',replace(encode(n.envelope,'base64'),chr(10),''))
            || CASE n.kind WHEN 'profile' THEN jsonb_build_object('keyWrappers',replace(encode(n.key_wrappers,'base64'),chr(10),''))
              WHEN 'record' THEN jsonb_build_object('folderId',n.parent) WHEN 'folder' THEN jsonb_build_object('parentFolderId',n.parent)
              ELSE jsonb_build_object('keyEpoch',n.key_epoch) END FROM natives n
        )
        """;
    internal const string Declare="DECLARE cerberus_account_export NO SCROLL CURSOR FOR "+Authority+Native+"""
        SELECT COALESCE(g.error,ng.error) error,h.value header,e.body::text body
        FROM guard g CROSS JOIN native_guard ng LEFT JOIN header h ON true
        LEFT JOIN LATERAL (SELECT body FROM native_entries WHERE g.error IS NULL AND ng.error IS NULL) e ON true
        """;
    internal const string Fetch="FETCH FORWARD 32 FROM cerberus_account_export";
    internal const string Fence=Authority+"""
        SELECT COALESCE(g.error,CASE WHEN NOT EXISTS(SELECT FROM actor a WHERE a.public_id=@owner) THEN 'not_found'
          WHEN EXISTS(SELECT FROM actor a WHERE a.policy_revision<>@policy OR a.revocation_generation<>@generation) THEN 'vault_access_denied'
          WHEN EXISTS(SELECT FROM cerberus.terminal_erasure e JOIN jsonb_to_recordset(@pending::jsonb) AS p(kind text,id uuid)
            ON e.resource_kind=p.kind AND e.resource_id=p.id) THEN 'not_found' END) AS "Value" FROM guard g
        """;
    internal static object[] Parameters(AccountExportRequest r,bool initial)=>
    [new NpgsqlParameter("actor",r.Actor),new NpgsqlParameter("verifier",r.AccessVerifier),new NpgsqlParameter("iat",r.AuthenticationIssuedAt),
        new NpgsqlParameter("expected",r.ExpectedRevision),new NpgsqlParameter("initial",initial)];
    internal const long Max=ProtocolBinary.MaxInteger;
}
