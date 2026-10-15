namespace ArturRios.Cerberus.Data.Erasure;

internal static class ResourceErasureSql
{
    internal const string Root="""
        SELECT id,account_id FROM cerberus.folder WHERE @kind='folder' AND public_id=@id
        UNION ALL SELECT id,account_id FROM cerberus.collection WHERE @kind='collection' AND public_id=@id
        UNION ALL SELECT id,account_id FROM cerberus.profile WHERE @kind='profile' AND public_id=@id
        """;
    internal const string Children="""
        SELECT 'record' kind,r.public_id resource_id,to_jsonb(r)::text body FROM cerberus.record r WHERE r.account_id=@owner AND r.folder_id=@parent
        UNION ALL SELECT 'folder',f.public_id,to_jsonb(f)::text FROM cerberus.folder f WHERE f.account_id=@owner AND f.parent_folder_id=@parent
        """;
    internal const string Child="""
        SELECT 'record' kind,r.public_id resource_id,to_jsonb(r)::text body FROM cerberus.record r WHERE @kind='record' AND r.account_id=@owner AND r.public_id=@id
        UNION ALL SELECT 'folder',f.public_id,to_jsonb(f)::text FROM cerberus.folder f WHERE @kind='folder' AND f.account_id=@owner AND f.public_id=@id
        """;
    internal const string Sessions="SELECT id,to_jsonb(s)::text body FROM cerberus.vault_access_session s WHERE s.account_id=@owner AND s.profile_id=@parent ORDER BY id";
    internal const string Session="SELECT to_jsonb(s)::text AS \"Value\" FROM cerberus.vault_access_session s WHERE s.id=@id";
    internal static string Delete(string kind)=>kind switch{
        "folder"=>"DELETE FROM cerberus.folder WHERE public_id=@id","collection"=>"DELETE FROM cerberus.collection WHERE public_id=@id","profile"=>"DELETE FROM cerberus.profile WHERE public_id=@id",_=>throw new InvalidOperationException("Invalid physical erasure kind.")};
    internal static string Detach(string kind)=>kind switch{
        "record"=>"UPDATE cerberus.record r SET folder_id=NULL,"+ChildChange,
        "folder"=>"UPDATE cerberus.folder r SET parent_folder_id=NULL,"+ChildChange,_=>throw new InvalidOperationException("Invalid surviving child kind.")};
    private const string ChildChange="revision=revision+1,server_sequence=nextval('cerberus.server_sequence'),concurrency_stamp=@stamp WHERE r.account_id=@owner AND r.public_id=@id AND to_jsonb(r)::text=@before";
}
