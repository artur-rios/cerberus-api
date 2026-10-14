using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Mediator.Query;

namespace ArturRios.Cerberus.Query.Sync;

public sealed class SyncChangesOutput : QueryOutput
{
    public Guid AccountId { get; init; }
    public Guid ProfileId { get; init; }
    public long PolicyRevision { get; init; }
    public long RevocationGeneration { get; init; }
    public DateTimeOffset ServerTime { get; init; }
    public IReadOnlyList<SyncChangeItem> Items { get; init; } = [];
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public string? NextCursor { get; init; }
    public bool HasMore { get; init; }
    public bool ClearScope { get; init; }
    public bool ResynchronizationRequired { get; init; }
}
