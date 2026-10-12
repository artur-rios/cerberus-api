using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Collections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class RevokeCollectionAccessCommand : BaseCommand
{
    [JsonRequired] public long ExpectedRevision { get; set; }
    internal Guid Actor { get; private set; }
    internal Guid CollectionId { get; private set; }
    internal Guid GrantId { get; private set; }
    internal string? VaultAccess { get; private set; }

    public void SetContext(Guid actor, string? access, Guid collectionId, Guid grantId)
    {
        Actor = actor;
        VaultAccess = access;
        CollectionId = collectionId;
        GrantId = grantId;
    }

    public CollectionGrantRevocationInput ToInput() => new(ExpectedRevision);
}
