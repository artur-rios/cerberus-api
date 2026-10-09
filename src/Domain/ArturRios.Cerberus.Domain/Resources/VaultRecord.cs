using ArturRios.Data.Relational.Core.Entities;
namespace ArturRios.Cerberus.Domain.Resources;

public sealed class VaultRecord : VersionedEntity
{
    public Guid PublicId { get; set; }
    public long AccountId { get; set; }
    public byte[] Envelope { get; set; } = [];
    public long Revision { get; set; } = 1;
    public DateTimeOffset EditedAt { get; set; }
    public long ServerSequence { get; set; }
    public DateTimeOffset? DeletedAt { get; set; }
    public DateTimeOffset? PurgeAt { get; set; }
    public long? FolderId { get; set; }
}
