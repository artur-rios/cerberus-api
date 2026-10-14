using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Offline;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Offline;

public sealed class OfflineAuthorizationOutput : CommandOutput
{
    public string Lease { get; init; } = string.Empty;
    public Guid AccountId { get; init; }
    public Guid ProfileId { get; init; }
    public long KeyEpoch { get; init; }
    public long ProtectionRevision { get; init; }
    public long PolicyRevision { get; init; }
    public long RevocationGeneration { get; init; }
    public OfflineGrantRevision[] GrantRevisions { get; init; } = [];
    public bool RenewalEnabled { get; init; }
    public DateTimeOffset IssuedAt { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.Never)] public DateTimeOffset? ExpiresAt { get; init; }
}
