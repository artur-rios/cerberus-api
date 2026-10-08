using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Mediator.Command;

namespace ArturRios.Cerberus.Command.Protection;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed class RecoverVaultCommand : BaseCommand
{
    public string Operation { get; set; } = null!;
    public Guid IdempotencyKey { get; set; }
    public long ExpectedRevision { get; set; }
    public PasswordWrapper PasswordWrapper { get; set; } = null!;
    public RecoveryWrapper RecoveryWrapper { get; set; } = null!;
    public PublicJwk NewRecoveryVerifier { get; set; } = null!;
    internal Guid Actor { get; private set; }
    internal long IdentityIssuedAt { get; private set; } = -1;
    internal Guid ChallengeId { get; private set; }
    internal string Proof { get; private set; } = null!;
    internal byte[] RawBody { get; private set; } = [];
    public RecoveryReplacement ToReplacement() => new(Operation, IdempotencyKey, ExpectedRevision, PasswordWrapper, RecoveryWrapper, NewRecoveryVerifier);
    public void SetContext(Guid actor, long identityIssuedAt, Guid challengeId, string proof, byte[] rawBody)
    { Actor = actor; IdentityIssuedAt = identityIssuedAt; ChallengeId = challengeId; Proof = proof; RawBody = rawBody; }
}
