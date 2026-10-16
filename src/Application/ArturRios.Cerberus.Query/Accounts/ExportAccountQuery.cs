using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Mediator.Query;

namespace ArturRios.Cerberus.Query.Accounts;

public sealed class ExportAccountQuery(Guid actor,long authenticationIssuedAt,string? access,long expectedRevision):BaseQuery
{
    public Guid Actor{get;}=actor;
    public long AuthenticationIssuedAt{get;}=authenticationIssuedAt;
    public string? Access{get;}=access;
    public long ExpectedRevision{get;}=expectedRevision;
}
public sealed class AccountExportOutput:QueryOutput
{
    [JsonIgnore] public IAccountExportLease? Lease{get;init;}
}
