using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Accounts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UpdateAccountCommand : BaseCommand
{
    public long ExpectedRevision { get; set; }
    public EncryptedEnvelope Details { get; set; } = null!;
    internal Guid IdentityId { get; private set; }
    internal string? VaultAccess { get; private set; }

    public void SetAccess(Guid identityId, string? vaultAccess) { IdentityId = identityId; VaultAccess = vaultAccess; }
}
