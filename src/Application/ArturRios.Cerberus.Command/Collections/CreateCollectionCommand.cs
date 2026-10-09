using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Collections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class CreateCollectionCommand : BaseCommand
{
    [JsonRequired] public Guid CollectionId { get; set; }
    [JsonRequired] public EncryptedEnvelope Envelope { get; set; } = null!;
    [JsonRequired] public DateTimeOffset EditedAt { get; set; }
    [JsonRequired] public Guid[] ProfileIds { get; set; } = null!;
    [JsonRequired] public Guid[] RecordIds { get; set; } = null!;
    [JsonRequired] public Guid[] FolderIds { get; set; } = null!;
    internal Guid Actor { get; private set; }
    internal string? VaultAccess { get; private set; }
    public void SetContext(Guid actor, string? access) { Actor = actor; VaultAccess = access; }
    public CollectionCreateInput ToInput() => new(CollectionId, Envelope, EditedAt, ProfileIds, RecordIds, FolderIds);
}
