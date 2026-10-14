using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Collections;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record CollectionUpdateInput(
    [property: JsonRequired] long ExpectedRevision,
    [property: JsonRequired] EncryptedEnvelope Envelope,
    [property: JsonRequired] DateTimeOffset EditedAt)
{
    public bool IsValid() => ExpectedRevision is > 0 and <= ProtocolBinary.MaxInteger
        && Envelope?.IsValid() == true && EditedAt.Offset == TimeSpan.Zero
        && EditedAt.Ticks >= TimeSpan.TicksPerMicrosecond;
}

public sealed record CollectionUpdateRequest(Guid Actor, string AccessVerifier, Guid CollectionId, CollectionUpdateInput Input, bool OfflineLatestEdit = false, bool OfflineInspectOnly = false);

public interface ICollectionUpdateStore
{
    Task<VaultResult<CollectionCreateDetails>> UpdateAsync(CollectionUpdateRequest request, CancellationToken cancellationToken);
}
