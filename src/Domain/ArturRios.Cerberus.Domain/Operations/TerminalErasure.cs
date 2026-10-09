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
    Task<bool> IsErasedAsync(string resourceKind, Guid resourceId, CancellationToken cancellationToken);
}

public static class TerminalResourceIdentity
{
    public static bool IsValid(string kind, Guid id) => id != Guid.Empty
        && kind is "account" or "profile" or "record" or "folder" or "collection" or "grant";
}
