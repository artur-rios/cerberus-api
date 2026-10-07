using ArturRios.Cerberus.Domain.Operations;

namespace ArturRios.Cerberus.Shared.Operations;

public interface IRetentionHandler
{
    string Kind { get; }
    Task ExecuteAsync(RetentionClaim claim, CancellationToken cancellationToken);
}

public sealed class RetentionExecutor(IEnumerable<IRetentionHandler> handlers) : IRetentionExecutor
{
    public Task ExecuteAsync(RetentionClaim claim, CancellationToken cancellationToken)
    {
        var kind = claim.OperationKey.Split('/', 2)[0];
        var matching = handlers.Where(x => x.Kind == kind).ToArray();
        if (matching.Length != 1) throw new InvalidOperationException("Retention handler unavailable or ambiguous.");
        return matching[0].ExecuteAsync(claim, cancellationToken);
    }
}
