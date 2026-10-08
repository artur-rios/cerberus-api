using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Profiles;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class SetProfileAssociationsCommand:BaseCommand
{
    [JsonRequired] public long ExpectedRevision {get;set;}
    [JsonRequired] public Guid[] RecordIds {get;set;}=null!;
    [JsonRequired] public Guid[] FolderIds {get;set;}=null!;
    [JsonRequired] public Guid[] CollectionIds {get;set;}=null!;
    internal Guid Actor {get;private set;}
    internal Guid ProfileId {get;private set;}
    internal string? VaultAccess {get;private set;}
    public void SetContext(Guid actor,string? access,Guid profileId){Actor=actor;VaultAccess=access;ProfileId=profileId;}
    public ProfileAssociationInput ToInput()=>new(ExpectedRevision,RecordIds,FolderIds,CollectionIds);
}
