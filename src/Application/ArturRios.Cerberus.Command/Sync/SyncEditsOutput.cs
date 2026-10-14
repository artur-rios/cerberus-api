using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Sync;

public sealed class SyncEditsOutput : CommandOutput
{
    public Guid AccountId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public Guid? ProfileId { get; init; }
    public DateTimeOffset ServerTime { get; init; }
    public IReadOnlyList<SyncEditOutcome> Outcomes { get; init; } = [];
}
