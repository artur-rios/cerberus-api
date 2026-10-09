using System.Text.Json.Serialization;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Records;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class DeleteRecordCommand:BaseCommand
{
    [JsonRequired] public long ExpectedRevision {get;set;}
    internal Guid Actor {get;private set;}
    internal Guid RecordId {get;private set;}
    internal string? VaultAccess {get;private set;}
    public void SetContext(Guid actor,string? access,Guid recordId){Actor=actor;VaultAccess=access;RecordId=recordId;}
}
