using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
namespace ArturRios.Cerberus.Domain.Profiles;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ProfileUpdateInput(long ExpectedRevision,EncryptedEnvelope Envelope,DateTimeOffset EditedAt)
{
    public bool IsValid()=>ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger && Envelope?.IsValid()==true
        && EditedAt.Ticks>=TimeSpan.TicksPerMicrosecond && EditedAt.Offset==TimeSpan.Zero;
}
public sealed record ProfileUpdateRequest(Guid Actor,string AccessVerifier,Guid ProfileId,ProfileUpdateInput Input);
public interface IProfileUpdateStore
{
    Task<VaultResult<ProfileCreateDetails>> UpdateAsync(ProfileUpdateRequest request,CancellationToken cancellationToken);
}
