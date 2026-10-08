using ArturRios.Data.Relational.Core.Entities;
namespace ArturRios.Cerberus.Domain.Trash;

public sealed class TrashEntry : VersionedEntity
{
    public long OperationId { get; set; }
    public string ResourceKind { get; set; } = string.Empty;
    public Guid ResourceId { get; set; }
    public byte[] AssociationSnapshot { get; set; } = [];
}
