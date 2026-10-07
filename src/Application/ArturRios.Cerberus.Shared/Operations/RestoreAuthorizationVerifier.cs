using ArturRios.Cerberus.Shared.Identity;

namespace ArturRios.Cerberus.Shared.Operations;

/// <summary>Each domain module registers deadline, grant and terminal-resource reconciliation.</summary>
public interface IRestoreVerificationStep
{
    Task<bool> VerifyAsync(CancellationToken cancellationToken);
}

public sealed class RestoreAuthorizationVerifier(IHeimdallClient identity, IEnumerable<IRestoreVerificationStep> steps) : IRestoreAuthorizationVerifier
{
    public async Task<bool> VerifyAsync(CancellationToken cancellationToken)
    {
        if (!await identity.VerifyScopeAsync(cancellationToken)) return false;
        foreach (var step in steps)
            if (!await step.VerifyAsync(cancellationToken)) return false;
        return true;
    }
}
