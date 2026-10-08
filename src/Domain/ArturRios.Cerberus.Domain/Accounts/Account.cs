using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Cerberus.Domain.Accounts;

public enum AccountState { Active, ClosurePending }

public sealed class Account : VersionedEntity
{
    public Guid PublicId { get; set; }
    public Guid HeimdallPublicId { get; set; }
    public byte[] DetailsEnvelope { get; set; } = [];
    public long Revision { get; set; } = 1;
    public AccountState State { get; set; }
    public DateTimeOffset? ClosureRequestedAt { get; set; }
    public DateTimeOffset? ClosurePurgeAt { get; set; }
    public bool RenewalEnabled { get; set; } = true;
    public TimeSpan? RenewalInterval { get; set; } = TimeSpan.FromHours(24);
    public long PolicyRevision { get; set; } = 1;
    public long RevocationGeneration { get; set; } = 1;
}
