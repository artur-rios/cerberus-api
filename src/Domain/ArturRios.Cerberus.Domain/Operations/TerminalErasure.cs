using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Cerberus.Domain.Operations;

public sealed class TerminalErasure : Entity
{
    public Guid ResourceId { get; set; }
    public string ResourceKind { get; set; } = string.Empty;
    public DateTimeOffset DeletedAt { get; set; }
}

public interface ITerminalErasureStore
{
    Task ReapplyAsync(ErasureEntry entry, CancellationToken cancellationToken);
    Task<bool> IsErasedAsync(Guid resourceId, CancellationToken cancellationToken);
}
