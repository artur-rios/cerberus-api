using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Profiles;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class UpdateProfileCommand:BaseCommand
{
    public long ExpectedRevision {get;set;}
    public EncryptedEnvelope Envelope {get;set;}=null!;
    public DateTimeOffset EditedAt {get;set;}
    internal Guid Actor {get;private set;}
    internal Guid ProfileId {get;private set;}
    internal string? VaultAccess {get;private set;}
    public void SetContext(Guid actor,string? access,Guid profileId){Actor=actor;VaultAccess=access;ProfileId=profileId;}
    public ProfileUpdateInput ToInput()=>new(ExpectedRevision,Envelope,EditedAt);
}
