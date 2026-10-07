namespace ArturRios.Cerberus.Shared.Identity;

public sealed record HeimdallLogin(string? Token, DateTimeOffset? ExpiresAt, bool? EmailVerified,
    bool RequiresTwoFactor, string? ChallengeToken, IReadOnlyList<string>? AvailableMethods);
public sealed record HeimdallPerson(Guid Id, Guid? ScopeId, int Role, bool IsDeleted, IReadOnlyList<Guid>? OwnedScopeIds);
public sealed record HeimdallRegistration(string Name, string Email, string Password);

public interface IHeimdallClient
{
    Task<HeimdallLogin?> LoginAsync(string email, string password, CancellationToken cancellationToken);
    Task<HeimdallLogin?> CompleteChallengeAsync(string challenge, string? code, string? recoveryCode, CancellationToken cancellationToken);
    Task<HeimdallPerson?> RegisterAsync(HeimdallRegistration registration, CancellationToken cancellationToken);
    Task<bool> RevalidateAsync(string token, CancellationToken cancellationToken);
}
