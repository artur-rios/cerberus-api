using ArturRios.Cerberus.Domain.Software;
using ArturRios.Mediator.Query;

namespace ArturRios.Cerberus.Query.Software;

public sealed class GetSoftwareSecretQuery(SoftwareIdentity? identity,string? bearer,SoftwareSecretInput input):BaseQuery
{
    public SoftwareIdentity? Identity{get;}=identity;
    internal string? Bearer{get;}=bearer;
    public SoftwareSecretInput Input{get;}=input;
}
