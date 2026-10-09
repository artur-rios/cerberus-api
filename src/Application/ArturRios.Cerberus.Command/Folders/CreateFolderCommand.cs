using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Folders;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class CreateFolderCommand : BaseCommand
{
    [JsonRequired] public Guid FolderId { get; set; }
    [JsonRequired] public EncryptedEnvelope Envelope { get; set; } = null!;
    [JsonRequired] public DateTimeOffset EditedAt { get; set; }
    [JsonRequired] public Guid[] ProfileIds { get; set; } = null!;
    public Guid? ParentFolderId { get; set; }
    internal Guid Actor { get; private set; }
    internal string? VaultAccess { get; private set; }
    public void SetContext(Guid actor, string? access) { Actor = actor; VaultAccess = access; }
    public FolderCreateInput ToInput() => new(FolderId, Envelope, EditedAt, ProfileIds, ParentFolderId);
}
