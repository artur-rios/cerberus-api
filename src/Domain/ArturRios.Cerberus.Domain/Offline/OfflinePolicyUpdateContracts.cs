using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Offline;

public sealed record OfflinePolicyUpdateRequest(Guid IdentityId, string Verifier, long ExpectedPolicyRevision,
    bool RenewalEnabled, TimeSpan? RenewalInterval)
{
    public bool IsValid(DateTimeOffset now) => ExpectedPolicyRevision is > 0 and <= ProtocolBinary.MaxInteger
        && (RenewalEnabled
            ? RenewalInterval is { } interval && interval > TimeSpan.Zero
                && interval.Ticks % TimeSpan.TicksPerSecond == 0 && interval <= DateTimeOffset.MaxValue - now
            : RenewalInterval is null);
}

public interface IOfflinePolicyUpdateStore
{
    Task<OfflinePolicyReadResult> UpdateAsync(OfflinePolicyUpdateRequest request, CancellationToken cancellationToken);
}
