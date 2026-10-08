using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Profiles;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class IssueProfileAccessChallengeCommand:BaseCommand
{
    [JsonRequired] public long ExpectedRevision {get;set;}
    [JsonRequired] public string RequestHash {get;set;}=null!;
    internal Guid Actor {get;private set;}
    internal Guid ProfileId {get;private set;}
    public void SetContext(Guid actor,Guid profileId){Actor=actor;ProfileId=profileId;}
    public ProfileChallengeRequest ToRequest()=>new(Actor,ProfileId,ExpectedRevision,RequestHash);
}
