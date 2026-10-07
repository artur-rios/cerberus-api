namespace ArturRios.Cerberus.Domain.Operations;

public sealed record ErasureEntry(Guid ResourceId, string ResourceKind, DateTimeOffset DeletedAt);

public interface IErasureLedger
{
    Task RecordAsync(ErasureEntry entry, CancellationToken cancellationToken);
    IAsyncEnumerable<ErasureEntry> ReadAsync(CancellationToken cancellationToken);
}
