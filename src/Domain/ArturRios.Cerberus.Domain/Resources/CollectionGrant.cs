using ArturRios.Data.Relational.Core.Entities;
namespace ArturRios.Cerberus.Domain.Resources;

public enum CollectionGrantAccess { ReadOnly, ReadWrite }
public enum CollectionGrantState { Active, Revoked }
public sealed class CollectionGrant : VersionedEntity
{
    public Guid PublicId { get; set; }
    public long CollectionId { get; set; }
    public long RecipientAccountId { get; set; }
    public CollectionGrantAccess Access { get; set; }
    public CollectionGrantState State { get; set; }
    public long Revision { get; set; } = 1;
    public byte[] RecipientKeyEnvelope { get; set; } = [];
}
