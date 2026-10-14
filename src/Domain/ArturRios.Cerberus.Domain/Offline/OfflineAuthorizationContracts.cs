using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Offline;

public sealed record OfflineAuthorizationRequest(Guid IdentityId, string Verifier, Guid ProfileId, long ExpectedProfileRevision)
{
    public bool IsValid() => IdentityId != Guid.Empty && ProfileId != Guid.Empty
        && !string.IsNullOrWhiteSpace(Verifier) && ExpectedProfileRevision is > 0 and <= ProtocolBinary.MaxInteger;
}
public sealed record OfflineAuthorizationDetails(OfflineLeaseSnapshot Snapshot, SignedOfflineLease Lease);
public sealed record OfflineAuthorizationResult(OfflineAuthorizationDetails? Details = null, string? Error = null);
public interface IOfflineAuthorizationStore
{
    Task<OfflineAuthorizationResult> RenewAsync(OfflineAuthorizationRequest request, CancellationToken cancellationToken);
}
