using System.Text.Json.Serialization;
using ArturRios.Mediator.Query;

namespace ArturRios.Cerberus.Query.Offline;

public sealed class OfflinePolicyOutput : QueryOutput
{
    public bool RenewalEnabled { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public TimeSpan? RenewalInterval { get; init; }
    public long PolicyRevision { get; init; }
    public long RevocationGeneration { get; init; }
}
