using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Trash;
public sealed class EmptyTrashOutput:CommandOutput
{
    public Guid AccountId{get;set;}
    public DateTimeOffset PurgedAt{get;set;}
    public IReadOnlyList<TrashPurgedCount> Counts{get;set;}=[];
    public long TotalPurged{get;set;}
}
