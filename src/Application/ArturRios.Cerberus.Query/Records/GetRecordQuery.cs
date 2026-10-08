using ArturRios.Mediator.Query;
namespace ArturRios.Cerberus.Query.Records;

public sealed class GetRecordQuery(Guid actor, string? access, Guid recordId) : BaseQuery
{
    public Guid Actor { get; } = actor;
    public string? Access { get; } = access;
    public Guid RecordId { get; } = recordId;
}
