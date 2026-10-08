using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Protection;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class InitializeVaultCommand : BaseCommand
{
    public Guid AccountId { get; set; }
    public long ExpectedAccountRevision { get; set; }
    public ProtectionMaterial Material { get; set; } = null!;
    internal Guid Actor { get; private set; }
    public void SetActor(Guid actor) => Actor = actor;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed class IssueVaultChallengeCommand : BaseCommand
{
    public string Operation { get; set; } = null!;
    public string RequestHash { get; set; } = null!;
    internal Guid Actor { get; private set; }
    public void SetActor(Guid actor) => Actor = actor;
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class UnlockVaultCommand : BaseCommand
{
    public long ExpectedProtectionRevision { get; set; }
    internal Guid Actor { get; private set; }
    internal Guid ChallengeId { get; private set; }
    internal string Proof { get; private set; } = null!;
    internal byte[] RawBody { get; private set; } = [];
    public void SetProof(Guid actor, Guid challengeId, string proof, byte[] rawBody)
    { Actor = actor; ChallengeId = challengeId; Proof = proof; RawBody = rawBody; }
}
