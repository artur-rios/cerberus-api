using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Accounts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class DeleteAccountCommand:BaseCommand
{
    [JsonRequired]public long ExpectedRevision{get;set;}
    internal Guid Actor{get;private set;}
    internal long AuthenticationIssuedAt{get;private set;}
    internal string? VaultAccess{get;private set;}
    public void SetContext(Guid actor,long issuedAt,string? access){Actor=actor;AuthenticationIssuedAt=issuedAt;VaultAccess=access;}
}
