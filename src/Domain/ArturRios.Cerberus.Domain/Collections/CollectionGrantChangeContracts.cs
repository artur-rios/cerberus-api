using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Collections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CollectionGrantChangeInput(
    [property: JsonRequired] long ExpectedRevision,
    [property: JsonRequired] string Access,
    [property: JsonRequired] RecipientEnvelope KeyEnvelope)
{
    public bool IsValid(Guid grantId) => grantId != Guid.Empty
        && ExpectedRevision is > 0 and < ProtocolBinary.MaxInteger
        && Access is "read-only" or "read-write"
        && KeyEnvelope?.IsValid() == true && KeyEnvelope.GrantId == grantId
        && KeyEnvelope.GrantRevision == ExpectedRevision + 1;
}

public sealed record CollectionGrantChangeRequest(Guid Actor, string AccessVerifier, Guid CollectionId,
    Guid GrantId, CollectionGrantChangeInput Input);

public sealed record CollectionGrantChangeDetails(Guid CollectionId, Guid GrantId, long Revision, string Access,
    Guid[] RecordIds, Guid[] FolderIds);

public interface ICollectionGrantChangeStore
{
    Task<VaultResult<CollectionGrantChangeDetails>> ChangeAsync(CollectionGrantChangeRequest request, CancellationToken cancellationToken);
}
