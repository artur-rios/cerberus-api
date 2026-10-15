namespace ArturRios.Cerberus.Domain.Software;

public sealed record SoftwareIdentity(Guid ApplicationId,Guid ScopeId,Guid CredentialId,DateTimeOffset ExpiresAt)
{
    public bool IsValid()=>ApplicationId!=Guid.Empty&&ScopeId!=Guid.Empty&&CredentialId!=Guid.Empty
        &&ExpiresAt.Offset==TimeSpan.Zero&&ExpiresAt>DateTimeOffset.UnixEpoch&&ExpiresAt.Ticks%TimeSpan.TicksPerSecond==0;
}
