using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Sync;

public sealed record SyncCheckpoint(Guid ProfileId, Guid StreamId, long WindowVersion, long After, long Boundary)
{
    public bool IsValid() => ProfileId != Guid.Empty && StreamId != Guid.Empty && WindowVersion is > 0 and <= ProtocolBinary.MaxInteger
        && After >= 0 && After <= Boundary && Boundary is >= 0 and <= ProtocolBinary.MaxInteger;
}
public sealed record SyncDownloadRequest(Guid Actor, string AccessVerifier, int PageSize, SyncCheckpoint? Checkpoint = null, bool InvalidCursor = false);
public sealed record SyncDownloadPage(Guid AccountId, Guid ProfileId, long PolicyRevision, long RevocationGeneration, DateTimeOffset ServerTime,
    IReadOnlyList<SyncChangeItem> Items, SyncCheckpoint? Checkpoint, bool HasMore, bool ClearScope, bool ResynchronizationRequired);
public sealed record SyncDownloadResult(SyncDownloadPage? Page = null, string? Error = null);
public interface ISyncDownloadStore
{
    Task<SyncDownloadResult> DownloadAsync(SyncDownloadRequest request, CancellationToken cancellationToken);
}
