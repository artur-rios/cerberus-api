using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Protection;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class ChangeVaultProtectionCommand : BaseCommand
{
    public Guid AccountId { get; set; }
    public long ExpectedProtectionRevision { get; set; }
    public long ExpectedAccountRevision { get; set; }
    public string Mode { get; set; } = null!;
    public ProtectionMaterial Material { get; set; } = null!;
    public ContentReplacement[] ContentReplacements { get; set; } = null!;
    internal Guid Actor { get; private set; }
    internal string? Access { get; private set; }
    internal Guid ChallengeId { get; private set; }
    internal string Proof { get; private set; } = null!;
    internal byte[] RawBody { get; private set; } = [];
    public ProtectionChange ToChange() => new(AccountId, ExpectedProtectionRevision, ExpectedAccountRevision, Mode, Material, ContentReplacements);
    public void SetContext(Guid actor, string? access, Guid challengeId, string proof, byte[] rawBody)
    { Actor = actor; Access = access; ChallengeId = challengeId; Proof = proof; RawBody = rawBody; }
}
