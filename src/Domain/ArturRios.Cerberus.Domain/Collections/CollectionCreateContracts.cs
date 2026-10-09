using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Collections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CollectionCreateInput(
    [property: JsonRequired] Guid CollectionId,
    [property: JsonRequired] EncryptedEnvelope Envelope,
    [property: JsonRequired] DateTimeOffset EditedAt,
    [property: JsonRequired] Guid[] ProfileIds,
    [property: JsonRequired] Guid[] RecordIds,
    [property: JsonRequired] Guid[] FolderIds)
{
    public bool IsValid() => CollectionId != Guid.Empty && Envelope?.IsValid() == true
        && Envelope.KeyEpoch == 1 && EditedAt.Offset == TimeSpan.Zero
        && EditedAt.Ticks >= TimeSpan.TicksPerMicrosecond
        && ProfileAssociationInput.ValidIds(ProfileIds)
        && ProfileAssociationInput.ValidIds(RecordIds)
        && ProfileAssociationInput.ValidIds(FolderIds);
}

public sealed record CollectionCreateRequest(Guid Actor, string AccessVerifier, CollectionCreateInput Input);
public sealed record CollectionCreateDetails(Guid CollectionId, long Revision, long ServerSequence, DateTimeOffset EditedAt);

public interface ICollectionCreateStore
{
    Task<VaultResult<CollectionCreateDetails>> CreateAsync(CollectionCreateRequest request, CancellationToken cancellationToken);
}
