using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Collections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class DeleteCollectionCommand : BaseCommand
{
    [JsonRequired] public long ExpectedRevision { get; set; }
    internal Guid Actor { get; private set; }
    internal Guid CollectionId { get; private set; }
    internal string? VaultAccess { get; private set; }

    public void SetContext(Guid actor, string? access, Guid collectionId)
    {
        Actor = actor;
        VaultAccess = access;
        CollectionId = collectionId;
    }
}
