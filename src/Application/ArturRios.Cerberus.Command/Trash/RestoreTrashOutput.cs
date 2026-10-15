using ArturRios.Cerberus.Domain.Trash;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Trash;
public sealed class RestoreTrashOutput:CommandOutput
{
    public Guid OperationId{get;set;}
    public string RootKind{get;set;}=string.Empty;
    public Guid RootResourceId{get;set;}
    public DateTimeOffset RestoredAt{get;set;}
    public IReadOnlyList<TrashRestoredItem> Items{get;set;}=[];
    public IReadOnlyList<TrashRestoreOmission> Omissions{get;set;}=[];
}
