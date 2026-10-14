using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Cerberus.Domain.Sync;

public sealed class SyncStream : VersionedEntity
{
    public Guid PublicId { get; set; }
    public long AccountId { get; set; }
    public long ProfileId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset ExpiresAt { get; set; }
    public int PageSize { get; set; }
    public long WindowVersion { get; set; } = 1;
    public long Boundary { get; set; }
    public long LastAfter { get; set; }
    public bool WindowComplete { get; set; }
    public byte[] VisibilityStamp { get; set; } = [];
}

public sealed class SyncInventory : Entity
{
    public long StreamId { get; set; }
    public string ResourceKind { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public byte[] Fingerprint { get; set; } = [];
    public bool Visible { get; set; } = true;
    public bool Terminal { get; set; }
}

public sealed class SyncChange : Entity
{
    public long StreamId { get; set; }
    public long WindowVersion { get; set; }
    public long Sequence { get; set; }
    public string ResourceKind { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public string EventType { get; set; } = string.Empty;
    public DateTimeOffset? DeletedAt { get; set; }
}
