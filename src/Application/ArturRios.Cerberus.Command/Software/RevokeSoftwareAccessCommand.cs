using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Software;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class RevokeSoftwareAccessCommand : BaseCommand
{
    [JsonRequired] public long ExpectedRevision { get; set; }
    internal Guid Actor { get; private set; }
    internal Guid GrantId { get; private set; }
    internal string? VaultAccess { get; private set; }

    public void SetContext(Guid actor, string? access, Guid grantId)
    {
        Actor = actor;
        VaultAccess = access;
        GrantId = grantId;
    }

    public SoftwareGrantRevocationInput ToInput() => new(ExpectedRevision);
}
