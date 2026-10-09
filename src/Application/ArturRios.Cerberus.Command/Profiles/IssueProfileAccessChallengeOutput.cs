using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command;
namespace ArturRios.Cerberus.Command.Profiles;
public sealed class IssueProfileAccessChallengeOutput:CommandOutput
{
    public VaultProofChallenge Challenge {get;set;}=null!;
    public ProfileAccessMaterial Profile {get;set;}=null!;
}
