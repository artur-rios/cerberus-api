using ArturRios.Cerberus.Domain.Trash;
namespace ArturRios.Cerberus.Query.Trash;
public sealed class TrashListOutput(IReadOnlyList<TrashListItem> items, string? nextCursor) : ArturRios.Mediator.Query.QueryOutput
{
    public IReadOnlyList<TrashListItem> Items { get; } = items;
    public string? NextCursor { get; } = nextCursor;
}
