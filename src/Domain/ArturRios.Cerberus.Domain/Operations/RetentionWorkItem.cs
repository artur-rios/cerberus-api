using ArturRios.Data.Relational.Core.Entities;

namespace ArturRios.Cerberus.Domain.Operations;

public sealed class RetentionWorkItem : VersionedEntity
{
    public Guid PublicId { get; set; } = Guid.NewGuid();
    public string OperationKey { get; set; } = string.Empty;
    public DateTimeOffset DueAt { get; set; }
    public Guid? ClaimToken { get; set; }
    public DateTimeOffset? ClaimExpiresAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public int Attempts { get; set; }

    public bool TryClaim(Guid token, DateTimeOffset now, TimeSpan duration)
    {
        if (token == Guid.Empty || duration <= TimeSpan.Zero || now.Offset != TimeSpan.Zero
            || duration.Ticks > DateTimeOffset.MaxValue.UtcTicks - now.UtcTicks
            || CompletedAt is not null || DueAt > now || ClaimExpiresAt > now)
            return false;
        ClaimToken = token;
        ClaimExpiresAt = now + duration;
        Attempts++;
        return true;
    }

    public bool TryComplete(Guid token, DateTimeOffset now)
    {
        if (token == Guid.Empty || ClaimToken != token || ClaimExpiresAt <= now || CompletedAt is not null) return false;
        CompletedAt = now;
        return true;
    }
}
