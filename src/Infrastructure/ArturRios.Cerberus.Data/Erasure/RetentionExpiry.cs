using ArturRios.Cerberus.Domain.Operations;

namespace ArturRios.Cerberus.Data.Erasure;

internal static class RetentionExpiry
{
    internal static Guid Identity(RetentionClaim claim,string kind)
    {
        var parts=claim.OperationKey.Split('/');
        if(parts.Length!=2||parts[0]!=kind||!Guid.TryParseExact(parts[1],"D",out var id)||id==Guid.Empty||parts[1]!=id.ToString("D"))
            throw new InvalidOperationException("Invalid retention expiry work.");
        return id;
    }
    internal static bool Due(DateTimeOffset? start,DateTimeOffset? deadline,DateTimeOffset now)=>
        start is {} began&&deadline is {} end&&Valid(began)&&Valid(end)&&end-began==TimeSpan.FromDays(30)&&end<=now;
    private static bool Valid(DateTimeOffset time)=>time.Offset==TimeSpan.Zero&&time>=DateTimeOffset.UnixEpoch&&time.Ticks%TimeSpan.TicksPerMicrosecond==0;
}
