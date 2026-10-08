namespace ArturRios.Cerberus.Domain.Identity;

public sealed record IdentityDetails(Guid Id, string Name, string Email, bool EmailVerified);
public sealed record IdentityUpdateResult(IdentityDetails? Identity = null, string? Error = null);

public interface IIdentityUpdateStore
{
    Task<IdentityUpdateResult> UpdateAsync(Guid identityId, string verifier,
        Func<CancellationToken, Task<IdentityUpdateResult>> update, CancellationToken cancellationToken);
}
