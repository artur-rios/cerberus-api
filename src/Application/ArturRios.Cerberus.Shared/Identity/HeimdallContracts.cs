namespace ArturRios.Cerberus.Shared.Identity;

public sealed record HeimdallLogin(string? Token, DateTimeOffset? ExpiresAt, bool? EmailVerified,
    bool RequiresTwoFactor, string? ChallengeToken, IReadOnlyList<string>? AvailableMethods);
public sealed record HeimdallAuthentication(HeimdallLogin? Login = null, Guid? IdentityId = null, string? Error = null);
public sealed record HeimdallPerson(Guid Id, Guid? ScopeId, int Role, bool IsDeleted, IReadOnlyList<Guid>? OwnedScopeIds);
[System.Text.Json.Serialization.JsonUnmappedMemberHandling(System.Text.Json.Serialization.JsonUnmappedMemberHandling.Disallow)]
public sealed record HeimdallRegistration(string Name, string Email, string Password);

public interface IHeimdallClient
{
    Task<HeimdallAuthentication> AuthenticateAsync(string email, string password, CancellationToken cancellationToken);
    Task<HeimdallAuthentication> VerifyChallengeAsync(string challenge, string? code, string? recoveryCode, CancellationToken cancellationToken);
    Task<ArturRios.Cerberus.Domain.Accounts.RegistrationIdentity> EstablishRegistrationIdentityAsync(
        HeimdallRegistration registration, string? proofToken, CancellationToken cancellationToken);
    Task<HeimdallLogin?> LoginAsync(string email, string password, CancellationToken cancellationToken);
    Task<HeimdallLogin?> CompleteChallengeAsync(string challenge, string? code, string? recoveryCode, CancellationToken cancellationToken);
    Task<HeimdallPerson?> RegisterAsync(HeimdallRegistration registration, CancellationToken cancellationToken);
    Task<HeimdallAuthorization> RevalidateAsync(string token, CancellationToken cancellationToken);
    Task<bool> VerifyScopeAsync(CancellationToken cancellationToken);
}

public enum HeimdallAuthorization { Unavailable, Denied, Authorized }
