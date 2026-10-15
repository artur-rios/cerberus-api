using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;

namespace ArturRios.Cerberus.Data.Accounts.Export;

internal sealed class AccountExportRow
{
    public string? Error{get;set;}
    public string? Header{get;set;}
    public string? Body{get;set;}
    public string? Fences{get;set;}
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
            AccountExportRelationship r=>Id(r.SourceId)&&Id(r.TargetId)&&Direction(r.SourceKind,r.TargetKind)
                &&(r.RelationshipKind=="current"?r.OperationId is null:r.RelationshipKind=="historical"&&r.OperationId is {} operation&&Id(operation)),
            AccountExportCollectionGrant g=>Id(g.GrantId)&&Id(g.CollectionId)&&Id(g.RecipientAccountId)&&Counter(g.Revision)&&Access(g.Access)&&State(g.State)&&g.RecipientKeyEnvelope is not null,
            AccountExportSharedReference r=>Id(r.CollectionId)&&Id(r.OwnerAccountId)&&Id(r.GrantId)&&Counter(r.GrantRevision)&&Access(r.Access),
            AccountExportSoftwareGrant g=>Id(g.GrantId)&&Id(g.SoftwareId)&&Counter(g.Revision)&&State(g.State)&&g.RecipientKey is not null,
            AccountExportSoftwareTarget t=>Id(t.GrantId)&&Id(t.ResourceId)&&t.ResourceKind is "record" or "collection"&&t.RecipientKeyEnvelope is not null,
            AccountExportTrashOperation o=>Id(o.OperationId)&&Id(o.RootId)&&Kind(o.RootKind)&&Timestamp(o.DeletedAt)&&Timestamp(o.PurgeAt)
                &&o.DeletedAt<=header.ServerTime&&o.PurgeAt>header.ServerTime&&o.PurgeAt-o.DeletedAt==TimeSpan.FromHours(720),
            _=>false
        };
        return valid?entry:throw Unavailable();
    }
    internal static IEnumerable<(string Kind,Guid Id)> Ids(AccountExportEntry entry)=>entry switch
    {
        AccountExportProfile p=>[("profile",p.ResourceId)],
        AccountExportRecord r=>r.FolderId is {} f?[("record",r.ResourceId),("folder",f)]:[("record",r.ResourceId)],
        AccountExportFolder f=>f.ParentFolderId is {} p?[("folder",f.ResourceId),("folder",p)]:[("folder",f.ResourceId)],
        AccountExportCollection c=>[("collection",c.ResourceId)],
        AccountExportRelationship r=>[(r.SourceKind,r.SourceId),(r.TargetKind,r.TargetId)],
        AccountExportCollectionGrant g=>[("grant",g.GrantId),("collection",g.CollectionId),("account",g.RecipientAccountId)],
        AccountExportSharedReference r=>[("collection",r.CollectionId),("account",r.OwnerAccountId),("grant",r.GrantId)],
        AccountExportSoftwareGrant g=>[("grant",g.GrantId)],AccountExportSoftwareTarget t=>[("grant",t.GrantId),(t.ResourceKind,t.ResourceId)],
        AccountExportTrashOperation o=>[(o.RootKind,o.RootId)],_=>[]
    };
    internal IEnumerable<(string Kind,Guid Id)> Dependencies()
    {
        var fences=Fences is null?null:JsonSerializer.Deserialize<AccountExportFence[]>(Fences,Json);
        if(fences is null||fences.Any(f=>!Id(f.Id)||!(Kind(f.Kind)||f.Kind is "account" or "grant")))throw Unavailable();
        return fences.Select(f=>(f.Kind,f.Id));
    }
    private static bool Id(Guid id)=>id!=Guid.Empty;
    private static bool Kind(string kind)=>kind is "profile" or "record" or "folder" or "collection";
    private static bool Direction(string source,string target)=>source switch{"profile"=>target is "record" or "folder" or "collection","collection"=>target is "record" or "folder","record" or "folder"=>target=="folder",_=>false};
    private static bool Access(string access)=>access is "read-only" or "read-write";
    private static bool State(string state)=>state is "active" or "revoked";
    internal static bool Counter(long n)=>n is >0 and <=AccountExportSql.Max;
    internal static bool Timestamp(DateTimeOffset t)=>t.Offset==TimeSpan.Zero&&t>=DateTimeOffset.UnixEpoch&&t.Ticks%10==0;
    private static bool OptionalId(Guid? id)=>id is null||id!=Guid.Empty;
    private static bool Native(Guid id,long rev,long sequence,DateTimeOffset edit,DateTimeOffset? deleted,DateTimeOffset? purge,byte[] bytes,AccountExportHeader header)=>
        id!=Guid.Empty&&Counter(rev)&&Counter(sequence)&&Timestamp(edit)&&bytes is not null
        &&(deleted is null?purge is null:purge is {} end&&Timestamp(deleted.Value)&&Timestamp(end)&&deleted<=header.ServerTime&&end>header.ServerTime&&end-deleted==TimeSpan.FromHours(720));
    internal static AccountExportException Unavailable()=>new("persistence_unavailable");
    private sealed record AccountExportFence(string Kind,Guid Id);
}
