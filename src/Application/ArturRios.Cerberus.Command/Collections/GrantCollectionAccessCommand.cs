using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Collections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class GrantCollectionAccessCommand : BaseCommand
{
    [JsonRequired] public Guid GrantId { get; set; }
    [JsonRequired] public Guid RecipientAccountId { get; set; }
    [JsonRequired] public long? ExpectedRevision { get; set; }
    [JsonRequired] public string Access { get; set; } = null!;
    [JsonRequired] public RecipientEnvelope KeyEnvelope { get; set; } = null!;
    internal Guid Actor { get; private set; }
    internal Guid CollectionId { get; private set; }
    internal string? VaultAccess { get; private set; }

    public void SetContext(Guid actor, string? access, Guid collectionId)
    {
        Actor = actor;
        VaultAccess = access;
        CollectionId = collectionId;
    }

    public CollectionGrantInput ToInput() => new(GrantId, RecipientAccountId, ExpectedRevision, Access, KeyEnvelope);
}
