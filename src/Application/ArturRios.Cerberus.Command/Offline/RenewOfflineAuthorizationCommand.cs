using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Offline;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class RenewOfflineAuthorizationCommand : BaseCommand
{
    [JsonRequired] public Guid ProfileId { get; set; }
    [JsonRequired] public long ExpectedProfileRevision { get; set; }
    internal Guid IdentityId { get; private set; }
    internal string? VaultAccess { get; private set; }
    public void SetContext(Guid identity, string? access) { IdentityId = identity; VaultAccess = access; }
}
