using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Folders;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record FolderCreateInput(
    [property: JsonRequired] Guid FolderId,
    [property: JsonRequired] EncryptedEnvelope Envelope,
    [property: JsonRequired] DateTimeOffset EditedAt,
    [property: JsonRequired] Guid[] ProfileIds,
    Guid? ParentFolderId = null)
{
    public bool IsValid() => FolderId != Guid.Empty && Envelope?.IsValid() == true
        && Envelope.KeyEpoch == 1 && EditedAt.Offset == TimeSpan.Zero
        && EditedAt.Ticks >= TimeSpan.TicksPerMicrosecond
        && ProfileAssociationInput.ValidIds(ProfileIds)
        && ParentFolderId != Guid.Empty && ParentFolderId != FolderId;
}

public sealed record FolderCreateRequest(Guid Actor, string AccessVerifier, FolderCreateInput Input);
public sealed record FolderCreateDetails(Guid FolderId, long Revision, long ServerSequence, DateTimeOffset EditedAt);

public interface IFolderCreateStore
{
    Task<VaultResult<FolderCreateDetails>> CreateAsync(FolderCreateRequest request, CancellationToken cancellationToken);
}
