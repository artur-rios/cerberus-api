using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Collections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CollectionGrantRevocationInput([property: JsonRequired] long ExpectedRevision)
{
    public bool IsValid(Guid grantId) => grantId != Guid.Empty
        && ExpectedRevision is > 0 and < ProtocolBinary.MaxInteger;
}

public sealed record CollectionGrantRevocationRequest(Guid Actor, string AccessVerifier, Guid CollectionId,
    Guid GrantId, CollectionGrantRevocationInput Input);

public sealed record CollectionGrantRevocationDetails(Guid CollectionId, Guid GrantId, long Revision);

public interface ICollectionGrantRevocationStore
{
    Task<VaultResult<CollectionGrantRevocationDetails>> RevokeAsync(CollectionGrantRevocationRequest request,
        CancellationToken cancellationToken);
}
