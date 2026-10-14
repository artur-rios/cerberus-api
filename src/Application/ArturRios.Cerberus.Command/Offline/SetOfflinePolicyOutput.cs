using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Offline;

public sealed class SetOfflinePolicyOutput : CommandOutput
{
    public bool RenewalEnabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TimeSpan? RenewalInterval { get; init; }
    public long PolicyRevision { get; init; }
    public long RevocationGeneration { get; init; }
}
