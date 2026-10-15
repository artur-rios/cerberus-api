using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Mediator.Query;

namespace ArturRios.Cerberus.Query.Software;

public sealed class SoftwareSecretOutput:QueryOutput
{
    [JsonRequired]public Guid OwnerAccountId{get;init;}
    [JsonRequired]public Guid GrantId{get;init;}
    [JsonRequired]public long GrantRevision{get;init;}
    [JsonRequired]public required SoftwareSecretResource Target{get;init;}
    [JsonRequired]public required SoftwareSecretProvisioning Provisioning{get;init;}
    [JsonRequired]public required SoftwareRecipientKey RecipientKey{get;init;}
    [JsonRequired]public required PublicJwk AuthorKey{get;init;}
}
