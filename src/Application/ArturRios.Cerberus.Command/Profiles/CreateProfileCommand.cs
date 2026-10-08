using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Profiles;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class CreateProfileCommand:BaseCommand
{
    public Guid ProfileId {get;set;}
    public EncryptedEnvelope Envelope {get;set;}=null!;
    public ProfileKeyWrappers KeyWrappers {get;set;}=null!;
    public DateTimeOffset EditedAt {get;set;}
    public Guid[] RecordIds {get;set;}=null!;
    public Guid[] FolderIds {get;set;}=null!;
    public Guid[] CollectionIds {get;set;}=null!;
    internal Guid Actor {get;private set;}
    internal string? VaultAccess {get;private set;}
    public void SetContext(Guid actor,string? access){Actor=actor;VaultAccess=access;}
    public ProfileCreateInput ToInput()=>new(ProfileId,Envelope,KeyWrappers,EditedAt,RecordIds,FolderIds,CollectionIds);
}
