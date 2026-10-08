using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Profiles;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class DeleteProfileCommand : BaseCommand
{
    public long ExpectedRevision { get; set; }
    internal Guid Actor { get; private set; }
    internal Guid ProfileId { get; private set; }
    internal string? VaultAccess { get; private set; }
    public void SetContext(Guid actor,string? access,Guid profileId){Actor=actor;VaultAccess=access;ProfileId=profileId;}
}
