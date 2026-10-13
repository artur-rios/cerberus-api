namespace ArturRios.Cerberus.Domain.Offline;

public sealed record OfflinePolicySnapshot(bool RenewalEnabled, TimeSpan? RenewalInterval,
    long PolicyRevision, long RevocationGeneration);

public sealed record OfflinePolicyReadResult(OfflinePolicySnapshot? Policy = null, string? Error = null);

public interface IOfflinePolicyReadStore
{
    Task<OfflinePolicyReadResult> ReadAsync(Guid identityId, string verifier, DateTimeOffset now,
        CancellationToken cancellationToken);
}
