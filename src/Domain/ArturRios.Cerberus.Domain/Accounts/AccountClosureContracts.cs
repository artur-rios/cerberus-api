using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;

namespace ArturRios.Cerberus.Domain.Accounts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record AccountClosureInput([property: JsonRequired] long ExpectedRevision)
{
    public bool IsValid() => ExpectedRevision is > 0 and <= ProtocolBinary.MaxInteger;
}

public static class FreshAuthentication
{
    public static bool IsFresh(long issuedAt, DateTimeOffset now)
    {
        var seconds = now.ToUnixTimeSeconds();
        return issuedAt > 0 && issuedAt <= seconds && seconds - issuedAt < 60;
    }
}

public sealed record AccountClosureRequest(Guid Actor, long AuthenticationIssuedAt, long ExpectedRevision);
public sealed record AccountClosureDetails(Guid AccountId, long Revision, long RevocationGeneration,
    DateTimeOffset ClosureRequestedAt, DateTimeOffset ClosureDeadline, DateTimeOffset ServerTime, bool AlreadyPending)
{
    public bool IsValid() => AccountId != Guid.Empty && Revision is > 0 and <= ProtocolBinary.MaxInteger
        && RevocationGeneration is > 0 and <= ProtocolBinary.MaxInteger
        && Time(ClosureRequestedAt) && Time(ClosureDeadline) && Time(ServerTime)
        && ClosureRequestedAt <= ServerTime && ClosureDeadline - ClosureRequestedAt == TimeSpan.FromDays(30);
    private static bool Time(DateTimeOffset value) => value.Offset == TimeSpan.Zero
        && value.Ticks >= TimeSpan.TicksPerMicrosecond && value.Ticks % TimeSpan.TicksPerMicrosecond == 0;
}
public sealed record AccountClosureResult(AccountClosureDetails? Details = null, string? Error = null);
public interface IAccountClosureStore
{
    Task<AccountClosureResult> RequestAsync(AccountClosureRequest request, CancellationToken cancellationToken);
}
