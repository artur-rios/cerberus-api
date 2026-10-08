using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Identity;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UpdateIdentityCommand : BaseCommand
{
    public string Name { get; set; } = null!;
    public string Email { get; set; } = null!;
    internal Guid IdentityId { get; private set; }
    internal string Token { get; private set; } = null!;
    internal string? VaultAccess { get; private set; }

    public void SetActor(Guid identityId, string token, string? vaultAccess)
    { IdentityId = identityId; Token = token; VaultAccess = vaultAccess; }
}
