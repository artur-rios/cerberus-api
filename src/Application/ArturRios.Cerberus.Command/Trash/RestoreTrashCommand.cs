using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Trash;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class RestoreTrashCommand:BaseCommand
{
    [JsonRequired]public long ExpectedRevision{get;set;}
    internal Guid Actor{get;private set;}
    internal Guid OperationId{get;private set;}
    internal string? VaultAccess{get;private set;}
    public void SetContext(Guid actor,string? access,Guid operationId){Actor=actor;VaultAccess=access;OperationId=operationId;}
}
