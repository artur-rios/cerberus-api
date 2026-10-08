using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Records;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record RecordCreateInput(
    [property:JsonRequired] Guid RecordId,
    [property:JsonRequired] EncryptedEnvelope Envelope,
    [property:JsonRequired] DateTimeOffset EditedAt,
    [property:JsonRequired] Guid[] ProfileIds,
    Guid? FolderId=null)
{
    public bool IsValid()=>RecordId!=Guid.Empty && Envelope?.IsValid()==true && Envelope.KeyEpoch==1
        && EditedAt.Offset==TimeSpan.Zero && EditedAt.Ticks>=TimeSpan.TicksPerMicrosecond
        && ProfileAssociationInput.ValidIds(ProfileIds) && FolderId!=Guid.Empty;
}
public sealed record RecordCreateRequest(Guid Actor,string AccessVerifier,RecordCreateInput Input);
public sealed record RecordCreateDetails(Guid RecordId,long Revision,long ServerSequence,DateTimeOffset EditedAt);
public interface IRecordCreateStore
{
    Task<VaultResult<RecordCreateDetails>> CreateAsync(RecordCreateRequest request,CancellationToken cancellationToken);
}
