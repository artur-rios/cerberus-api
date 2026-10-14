using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Sync;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class UploadSyncEditsCommand : BaseCommand
{
    [JsonRequired] public SyncEditInput[] Edits { get; set; } = [];
    internal Guid Actor { get; private set; }
    internal string? Access { get; private set; }
    public void SetContext(Guid actor, string? access) { Actor = actor; Access = access; }
}
