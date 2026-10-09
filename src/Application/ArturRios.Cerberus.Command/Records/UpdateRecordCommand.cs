using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Records;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class UpdateRecordCommand:BaseCommand
{
    [JsonRequired] public long ExpectedRevision {get;set;}
    [JsonRequired] public EncryptedEnvelope Envelope {get;set;}=null!;
    [JsonRequired] public DateTimeOffset EditedAt {get;set;}
    internal Guid Actor {get;private set;}
    internal Guid RecordId {get;private set;}
    internal string? VaultAccess {get;private set;}
    public void SetContext(Guid actor,string? access,Guid recordId){Actor=actor;VaultAccess=access;RecordId=recordId;}
    public RecordUpdateInput ToInput()=>new(ExpectedRevision,Envelope,EditedAt);
}
