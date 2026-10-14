using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Accounts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class RequestAccountClosureCommand : BaseCommand
{
    [JsonRequired] public long ExpectedRevision { get; set; }
    internal Guid Actor { get; private set; }
    internal long AuthenticationIssuedAt { get; private set; }
    public void SetContext(Guid actor, long issuedAt) { Actor = actor; AuthenticationIssuedAt = issuedAt; }
}
