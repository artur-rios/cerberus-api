using ArturRios.Mediator.Query;
namespace ArturRios.Cerberus.Query.Profiles;
public sealed class GetProfileQuery(Guid actor, string? access, Guid profileId) : BaseQuery
{
    public Guid Actor { get; } = actor;
    public string? Access { get; } = access;
    public Guid ProfileId { get; } = profileId;
}
