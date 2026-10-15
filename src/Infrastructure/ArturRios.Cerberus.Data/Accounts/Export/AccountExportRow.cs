using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;

namespace ArturRios.Cerberus.Data.Accounts.Export;

internal sealed class AccountExportRow
{
    public string? Error{get;set;}
    public string? Header{get;set;}
    public string? Body{get;set;}
    internal static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false,AllowOutOfOrderMetadataProperties=true,
        UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow,NumberHandling=JsonNumberHandling.Strict,
        RespectNullableAnnotations=true,RespectRequiredConstructorParameters=true
    };
    internal AccountExportEntry Read(AccountExportHeader header,string capturedHeader)
    {
        if(Error is not null)throw new AccountExportException(Error is "not_found" or "vault_access_denied" or "fresh_authentication_required" or "revision_conflict"?Error:"persistence_unavailable");
        if(Header!=capturedHeader||Body is null)throw Unavailable();
        var entry=JsonSerializer.Deserialize<AccountExportEntry>(Body,Json)??throw Unavailable();
        var valid=entry switch
        {
            AccountExportAccount a=>a.Details is not null,
            AccountExportProtection p=>Counter(p.Revision)&&Counter(p.KeyEpoch)&&Counter(p.RecoveryGeneration)&&p.Material is not null,
            AccountExportProfile p=>Native(p.ResourceId,p.Revision,p.ServerSequence,p.EditedAt,p.DeletedAt,p.PurgeAt,p.Envelope,header)&&p.KeyWrappers is not null,
            AccountExportRecord r=>Native(r.ResourceId,r.Revision,r.ServerSequence,r.EditedAt,r.DeletedAt,r.PurgeAt,r.Envelope,header)&&OptionalId(r.FolderId),
            AccountExportFolder f=>Native(f.ResourceId,f.Revision,f.ServerSequence,f.EditedAt,f.DeletedAt,f.PurgeAt,f.Envelope,header)&&OptionalId(f.ParentFolderId),
            AccountExportCollection c=>Native(c.ResourceId,c.Revision,c.ServerSequence,c.EditedAt,c.DeletedAt,c.PurgeAt,c.Envelope,header)&&Counter(c.KeyEpoch),
            _=>false
        };
        return valid?entry:throw Unavailable();
    }
    internal static IEnumerable<(string Kind,Guid Id)> Ids(AccountExportEntry entry)=>entry switch
    {
        AccountExportProfile p=>[("profile",p.ResourceId)],
        AccountExportRecord r=>r.FolderId is {} f?[("record",r.ResourceId),("folder",f)]:[("record",r.ResourceId)],
        AccountExportFolder f=>f.ParentFolderId is {} p?[("folder",f.ResourceId),("folder",p)]:[("folder",f.ResourceId)],
        AccountExportCollection c=>[("collection",c.ResourceId)],_=>[]
    };
    internal static bool Counter(long n)=>n is >0 and <=AccountExportSql.Max;
    internal static bool Timestamp(DateTimeOffset t)=>t.Offset==TimeSpan.Zero&&t>=DateTimeOffset.UnixEpoch&&t.Ticks%10==0;
    private static bool OptionalId(Guid? id)=>id is null||id!=Guid.Empty;
    private static bool Native(Guid id,long rev,long sequence,DateTimeOffset edit,DateTimeOffset? deleted,DateTimeOffset? purge,byte[] bytes,AccountExportHeader header)=>
        id!=Guid.Empty&&Counter(rev)&&Counter(sequence)&&Timestamp(edit)&&bytes is not null
        &&(deleted is null?purge is null:purge is {} end&&Timestamp(deleted.Value)&&Timestamp(end)&&deleted<=header.ServerTime&&end>header.ServerTime&&end-deleted==TimeSpan.FromHours(720));
    internal static AccountExportException Unavailable()=>new("persistence_unavailable");
}
