using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Accounts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record AccountClosureCancellationInput([property:JsonRequired] long ExpectedRevision)
{
    public bool IsValid()=>ExpectedRevision is >0 and <=ProtocolBinary.MaxInteger;
}
public sealed record AccountClosureCancellationRequest(Guid Actor,long AuthenticationIssuedAt,long ExpectedRevision);
public sealed record AccountClosureCancellationDetails(Guid AccountId,long Revision,long RevocationGeneration,
    DateTimeOffset CancelledAt,DateTimeOffset ServerTime)
{
    public bool IsValid()=>AccountId!=Guid.Empty&&Revision is >0 and <=ProtocolBinary.MaxInteger
        &&RevocationGeneration is >0 and <=ProtocolBinary.MaxInteger&&ValidTime(CancelledAt)&&ValidTime(ServerTime)&&CancelledAt<=ServerTime;
    internal static bool ValidTime(DateTimeOffset t)=>t.Offset==TimeSpan.Zero&&t.Ticks>=10&&t.Ticks%10==0;
}
public sealed record AccountClosureCancellationResult(AccountClosureCancellationDetails? Details=null,string? Error=null);
public interface IAccountClosureCancellationStore
{
    Task<AccountClosureCancellationResult> CancelAsync(AccountClosureCancellationRequest request,CancellationToken cancellationToken);
}
public sealed record AccountClosureRecoveryRequest(Guid Actor,long AuthenticationIssuedAt);
public sealed record AccountClosureRecoveryDetails(Guid AccountId,long Revision,DateTimeOffset ClosureDeadline,DateTimeOffset ServerTime)
{
    public bool IsValid()=>AccountId!=Guid.Empty&&Revision is >0 and <=ProtocolBinary.MaxInteger
        &&AccountClosureCancellationDetails.ValidTime(ClosureDeadline)&&AccountClosureCancellationDetails.ValidTime(ServerTime)
        &&ClosureDeadline>ServerTime&&ClosureDeadline-ServerTime<=TimeSpan.FromDays(30);
}
public sealed record AccountClosureRecoveryResult(AccountClosureRecoveryDetails? Details=null,string? Error=null);
public interface IAccountClosureRecoveryStore
{
    Task<AccountClosureRecoveryResult> ReadAsync(AccountClosureRecoveryRequest request,CancellationToken cancellationToken);
}
