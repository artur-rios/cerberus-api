using ArturRios.Data.Relational.Core.Entities;
namespace ArturRios.Cerberus.Domain.Trash;

public sealed class TrashOperation : VersionedEntity
{
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public long AccountId { get; set; }
    public string RootResourceKind { get; set; } = string.Empty;
    public Guid RootResourceId { get; set; }
    public DateTimeOffset DeletedAt { get; set; }
    public DateTimeOffset PurgeAt { get; set; }
}
