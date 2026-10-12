using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Collections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class UpdateCollectionCommand : BaseCommand
{
    [JsonRequired] public long ExpectedRevision { get; set; }
    [JsonRequired] public EncryptedEnvelope Envelope { get; set; } = null!;
    [JsonRequired] public DateTimeOffset EditedAt { get; set; }
    internal Guid Actor { get; private set; }
    internal Guid CollectionId { get; private set; }
    internal string? VaultAccess { get; private set; }

    public void SetContext(Guid actor, string? access, Guid collectionId)
    {
        Actor = actor;
        VaultAccess = access;
        CollectionId = collectionId;
    }

    public CollectionUpdateInput ToInput() => new(ExpectedRevision, Envelope, EditedAt);
}
