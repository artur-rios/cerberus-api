using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Profiles;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class OpenProfileAccessCommand:BaseCommand
{
    [JsonRequired] public long ExpectedRevision {get;set;}
    internal Guid Actor {get;private set;}
    internal Guid ProfileId {get;private set;}
    internal Guid ChallengeId {get;private set;}
    internal string Proof {get;private set;}=null!;
    internal byte[] RawBody {get;private set;}=[];
    public void SetContext(Guid actor,Guid profileId,Guid challengeId,string proof,byte[] rawBody)
    {Actor=actor;ProfileId=profileId;ChallengeId=challengeId;Proof=proof;RawBody=rawBody;}
    public ProfileAccessRequest ToRequest()=>new(Actor,ProfileId,ExpectedRevision,ChallengeId,Proof,RawBody);
}
