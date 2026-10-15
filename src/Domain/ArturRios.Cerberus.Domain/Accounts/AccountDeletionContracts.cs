using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Accounts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record AccountDeletionInput([property:JsonRequired]long ExpectedRevision)
{
    public bool IsValid()=>ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger;
}
public sealed record AccountDeletionRequest(Guid Actor,long AuthenticationIssuedAt,string AccessVerifier,long ExpectedRevision);
public sealed record AccountDeletionDetails(Guid AccountId,DateTimeOffset DeletedAt,string State)
{
    public bool IsValid()=>AccountId!=Guid.Empty&&DeletedAt.Offset==TimeSpan.Zero
        &&DeletedAt>=DateTimeOffset.UnixEpoch&&DeletedAt.Ticks%TimeSpan.TicksPerMicrosecond==0
        &&State is "pending" or "completed";
}
public sealed record AccountDeletionResult(AccountDeletionDetails? Details=null,string? Error=null);
public interface IAccountDeletionStore
{
    Task<AccountDeletionResult> DeleteAsync(AccountDeletionRequest request,CancellationToken cancellationToken);
}
