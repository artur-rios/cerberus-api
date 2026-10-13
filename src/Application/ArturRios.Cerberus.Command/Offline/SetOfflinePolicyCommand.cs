using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Offline;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class SetOfflinePolicyCommand : BaseCommand
{
    [JsonRequired]
    public long ExpectedPolicyRevision { get; set; }
    [JsonRequired]
    public bool RenewalEnabled { get; set; }
    [JsonRequired]
    public TimeSpan? RenewalInterval { get; set; }
    internal Guid IdentityId { get; private set; }
    internal string? VaultAccess { get; private set; }
    public void SetContext(Guid identityId, string? vaultAccess) { IdentityId = identityId; VaultAccess = vaultAccess; }
}
