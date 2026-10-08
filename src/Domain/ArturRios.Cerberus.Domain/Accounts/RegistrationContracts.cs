namespace ArturRios.Cerberus.Domain.Accounts;

public enum RegistrationIdentityStatus { Verified, Denied, Forbidden, NotFound, Unavailable }
public sealed record RegistrationIdentity(RegistrationIdentityStatus Status, Guid? IdentityId = null);
public sealed record RegistrationRequest(Guid OperationId, Guid AccountId, byte[] DetailsEnvelope,
    string RequestFingerprint, long IdentityLock);
public sealed record RegistrationResult(Guid? AccountId = null, long Revision = 0, bool Replayed = false, string? Error = null);

public interface IRegistrationStore
{
    Task<RegistrationResult> RegisterAsync(RegistrationRequest request,
        Func<CancellationToken, Task<RegistrationIdentity>> establishIdentity, CancellationToken cancellationToken);
}
