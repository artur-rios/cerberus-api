using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Records;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class CreateRecordCommand : BaseCommand
{
    [JsonRequired] public Guid RecordId { get; set; }
    [JsonRequired] public EncryptedEnvelope Envelope { get; set; } = null!;
    [JsonRequired] public DateTimeOffset EditedAt { get; set; }
    [JsonRequired] public Guid[] ProfileIds { get; set; } = null!;
    public Guid? FolderId { get; set; }
    internal Guid Actor { get; private set; }
    internal string? VaultAccess { get; private set; }
    public void SetContext(Guid actor, string? access) { Actor = actor; VaultAccess = access; }
    public RecordCreateInput ToInput() => new(RecordId, Envelope, EditedAt, ProfileIds, FolderId);
}
