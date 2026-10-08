using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Cerberus.Domain.Access;

public sealed class VaultAccessSession : Entity
{
    public string HandleVerifier { get; set; } = string.Empty;
    public long AccountId { get; set; }
    public long? ProfileId { get; set; }
    public DateTimeOffset IssuedAt { get; set; }
    public DateTimeOffset? ExpiresAt { get; set; }
    public long PolicyRevision { get; set; }
    public long RevocationGeneration { get; set; }
    public bool Revoked { get; set; }
}
