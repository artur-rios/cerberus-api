using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Trash;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class EmptyTrashCommand:BaseCommand
{
    [JsonRequired]public long ExpectedRevision{get;set;}
    internal Guid Actor{get;private set;}
    internal string? VaultAccess{get;private set;}
    public void SetContext(Guid actor,string? access){Actor=actor;VaultAccess=access;}
}
