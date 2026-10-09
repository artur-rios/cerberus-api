using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Folders;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class UpdateFolderCommand:BaseCommand
{
    [JsonRequired] public long ExpectedRevision {get;set;}
    [JsonRequired] public EncryptedEnvelope Envelope {get;set;}=null!;
    [JsonRequired] public DateTimeOffset EditedAt {get;set;}
    internal Guid Actor {get;private set;}
    internal Guid FolderId {get;private set;}
    internal string? VaultAccess {get;private set;}
    public void SetContext(Guid actor,string? access,Guid folderId){Actor=actor;VaultAccess=access;FolderId=folderId;}
    public FolderUpdateInput ToInput()=>new(ExpectedRevision,Envelope,EditedAt);
}
