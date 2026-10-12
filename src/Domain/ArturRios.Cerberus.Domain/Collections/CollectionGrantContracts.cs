using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Collections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CollectionGrantInput(
    [property: JsonRequired] Guid GrantId,
    [property: JsonRequired] Guid RecipientAccountId,
    [property: JsonRequired] long? ExpectedRevision,
    [property: JsonRequired] string Access,
    [property: JsonRequired] RecipientEnvelope KeyEnvelope)
{
    public bool IsValid() => GrantId != Guid.Empty && RecipientAccountId != Guid.Empty
        && ExpectedRevision is null or > 0 and < ProtocolBinary.MaxInteger
        && Access is "read-only" or "read-write"
        && KeyEnvelope?.IsValid() == true && KeyEnvelope.GrantId == GrantId
        && KeyEnvelope.GrantRevision == (ExpectedRevision ?? 0) + 1;
}

public sealed record CollectionGrantRequest(Guid Actor, string AccessVerifier, Guid CollectionId, CollectionGrantInput Input);

public sealed record CollectionGrantDetails(Guid CollectionId, Guid GrantId, long Revision, string Access,
    Guid[] RecordIds, Guid[] FolderIds);

public interface ICollectionGrantStore
{
    Task<VaultResult<CollectionGrantDetails>> GrantAsync(CollectionGrantRequest request, CancellationToken cancellationToken);
}
