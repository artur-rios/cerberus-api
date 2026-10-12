using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Software;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class GrantSoftwareAccessCommand : BaseCommand
{
    [JsonRequired] public Guid GrantId { get; set; }
    [JsonRequired] public Guid SoftwareIdentityId { get; set; }
    [JsonRequired] public long? ExpectedRevision { get; set; }
    [JsonRequired] public SoftwareRecipientKey RecipientKey { get; set; } = null!;
    [JsonRequired] public SoftwareGrantTargetInput[] Targets { get; set; } = null!;
    internal Guid Actor { get; private set; }
    internal string? VaultAccess { get; private set; }

    public void SetContext(Guid actor, string? access)
    {
        Actor = actor;
        VaultAccess = access;
    }

    public SoftwareGrantInput ToInput() => new(GrantId, SoftwareIdentityId, ExpectedRevision, RecipientKey, Targets);
}
