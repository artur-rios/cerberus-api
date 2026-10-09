using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Records;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record RecordUpdateInput(
    [property:JsonRequired] long ExpectedRevision,
    [property:JsonRequired] EncryptedEnvelope Envelope,
    [property:JsonRequired] DateTimeOffset EditedAt)
{
    public bool IsValid()=>ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger && Envelope?.IsValid()==true
        && EditedAt.Offset==TimeSpan.Zero && EditedAt.Ticks>=TimeSpan.TicksPerMicrosecond;
}
public sealed record RecordUpdateRequest(Guid Actor,string AccessVerifier,Guid RecordId,RecordUpdateInput Input);
public interface IRecordUpdateStore
{
    Task<VaultResult<RecordCreateDetails>> UpdateAsync(RecordUpdateRequest request,CancellationToken cancellationToken);
}
