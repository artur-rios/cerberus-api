namespace ArturRios.Cerberus.Domain.Operations;

public sealed record RetentionClaim(Guid WorkId, Guid Token, string OperationKey, DateTimeOffset ExpiresAt);

public interface IRetentionWorkStore
{
    Task<IReadOnlyList<Guid>> GetDueAsync(DateTimeOffset now, int limit, CancellationToken cancellationToken);
    Task<RetentionClaim?> TryClaimAsync(Guid workId, DateTimeOffset now, TimeSpan lease, CancellationToken cancellationToken);
    Task<bool> TryCompleteAsync(RetentionClaim claim, DateTimeOffset now, CancellationToken cancellationToken);
}
