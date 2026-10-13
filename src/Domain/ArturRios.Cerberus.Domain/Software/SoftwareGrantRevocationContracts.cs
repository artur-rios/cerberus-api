using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Software;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record SoftwareGrantRevocationInput([property: JsonRequired] long ExpectedRevision)
{
    public bool IsValid(Guid grantId) => grantId != Guid.Empty
        && ExpectedRevision is > 0 and < ProtocolBinary.MaxInteger;
}

public sealed record SoftwareGrantRevocationRequest(Guid Actor, string AccessVerifier, Guid GrantId,
    SoftwareGrantRevocationInput Input);

public sealed record SoftwareGrantRevocationDetails(Guid GrantId, long Revision);

public interface ISoftwareGrantRevocationStore
{
    Task<VaultResult<SoftwareGrantRevocationDetails>> RevokeAsync(SoftwareGrantRevocationRequest request,
        CancellationToken cancellationToken);
}
