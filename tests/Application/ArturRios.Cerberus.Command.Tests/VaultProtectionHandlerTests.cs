using System.Security.Cryptography;
using System.Text;
using ArturRios.Cerberus.Command.Protection;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class VaultProtectionHandlerTests
{
    private static readonly Guid s_actor = Guid.NewGuid();
    private static readonly Guid s_account = Guid.NewGuid();
    private static readonly byte[] s_body = Encoding.UTF8.GetBytes("{\"expectedProtectionRevision\":1}");
    private static string Hash => ProtocolBinary.Encode(SHA256.HashData(s_body));

    [UnitTheory]
    [InlineData("actor", "authentication_required")]
    [InlineData("account", "validation_failed")]
    [InlineData("revision", "validation_failed")]
    [InlineData("unsafeRevision", "validation_failed")]
    [InlineData("material", "validation_failed")]
    [InlineData("epoch", "validation_failed")]
    [InlineData("generation", "validation_failed")]
    public async Task GivenInvalidInitialization_WhenHandling_ThenRejectBeforeStore(string invalid, string error)
    {
        using var client = new ProtectionFixture();
        var command = Init(client.Material);
        if (invalid == "actor")
            command.SetActor(Guid.Empty);
        if (invalid == "account")
            command.AccountId = Guid.Empty;
        if (invalid == "revision")
            command.ExpectedAccountRevision = 0;
        if (invalid == "unsafeRevision")
            command.ExpectedAccountRevision = 9007199254740992;
        if (invalid == "material")
            command.Material = null!;
        if (invalid == "epoch")
            command.Material = client.Material with { PasswordWrapper = client.Material.PasswordWrapper with { KeyEpoch = 2 } };
        if (invalid == "generation")
            command.Material = client.Material with { RecoveryWrapper = client.Material.RecoveryWrapper with { Generation = 2 } };
        var result = await new InitializeVaultHandler(new InitializeVaultValidator(), Strict()).HandleAsync(command);
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
    }

    [UnitFact]
    public async Task GivenValidInitialization_WhenHandling_ThenBindTrustedActorAndReturnOnlyConfirmation()
    {
        using var client = new ProtectionFixture();
        var store = new Mock<IVaultProtectionStore>(MockBehavior.Strict);
        store.Setup(x => x.InitializeAsync(s_actor, s_account, 3, client.Material, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VaultResult<VaultProtectionDetails>(new(s_account, 1, 1, 1, client.Material)));
        var result = await new InitializeVaultHandler(new InitializeVaultValidator(), store.Object).HandleAsync(Init(client.Material));
        Assert.True(result.Success);
        Assert.Equal(s_account, result.Data!.AccountId);
        Assert.Equal(1, result.Data.ProtectionRevision);
        Assert.Contains("protection_initialized", result.Messages);
    }

    [UnitTheory]
    [InlineData("not_found", 404)]
    [InlineData("revision_conflict", 409)]
    [InlineData("persistence_unavailable", 503)]
    public async Task GivenFailedInitialization_WhenHandling_ThenMapStableError(string error, int status)
    {
        using var client = new ProtectionFixture();
        var store = new Mock<IVaultProtectionStore>();
        store.Setup(x => x.InitializeAsync(s_actor, s_account, 3, client.Material, It.IsAny<CancellationToken>())).ReturnsAsync(new VaultResult<VaultProtectionDetails>(Error: error));
        var result = await new InitializeVaultHandler(new InitializeVaultValidator(), store.Object).HandleAsync(Init(client.Material));
        Assert.False(result.Success);
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
        Assert.Equal(status, VaultProtectionMessages.StatusCodes[error]);
    }

    [UnitTheory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("revision")]
    public async Task GivenIncompleteInitializationResult_WhenHandling_ThenFailClosed(string invalid)
    {
        using var client = new ProtectionFixture();
        var store = new Mock<IVaultProtectionStore>();
        store.Setup(x => x.InitializeAsync(s_actor, s_account, 3, client.Material, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VaultResult<VaultProtectionDetails>(invalid == "missing" ? null : new(invalid == "foreign" ? Guid.NewGuid() : s_account, invalid == "revision" ? 2 : 1, 1, 1, client.Material)));
        var result = await new InitializeVaultHandler(new InitializeVaultValidator(), store.Object).HandleAsync(Init(client.Material));
        Assert.Contains("persistence_unavailable", result.Errors);
        Assert.Null(result.Data);
    }

    [UnitTheory]
    [InlineData("actor", "authentication_required")]
    [InlineData("operation", "validation_failed")]
    [InlineData("hash", "validation_failed")]
    public async Task GivenInvalidChallengeRequest_WhenHandling_ThenRejectBeforeStore(string invalid, string error)
    {
        var command = new IssueVaultChallengeCommand { Operation = invalid == "operation" ? "unsupported" : "unlock-account", RequestHash = invalid == "hash" ? "bad" : Hash };
        command.SetActor(invalid == "actor" ? Guid.Empty : s_actor);
        var result = await new IssueVaultChallengeHandler(new IssueVaultChallengeValidator(), Strict()).HandleAsync(command);
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
    }

    [UnitTheory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("failed")]
    public async Task GivenChallengeStoreResult_WhenHandling_ThenAcceptOnlyBoundChallenge(string state)
    {
        var command = new IssueVaultChallengeCommand { Operation = "unlock-account", RequestHash = Hash };
        command.SetActor(s_actor);
        var c = new VaultProofChallenge("cerberus-challenge-v1", Guid.NewGuid(), ProtocolBinary.Encode(new byte[32]), "unlock-account",
            state == "foreign" ? Guid.NewGuid() : s_actor, s_account, "account", s_account, 1, 1, null, Hash, 1000, 1060);
        var store = new Mock<IVaultProtectionStore>();
        store.Setup(x => x.ChallengeAsync(s_actor, Hash, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VaultResult<VaultProofChallenge>(state == "missing" ? null : c, state == "failed" ? "not_found" : null));
        var result = await new IssueVaultChallengeHandler(new IssueVaultChallengeValidator(), store.Object).HandleAsync(command);
        if (state == "valid")
        { Assert.True(result.Success); Assert.Equal(c, result.Data!.Challenge); }
        else
        { Assert.Contains(state == "failed" ? "not_found" : "persistence_unavailable", result.Errors); Assert.Null(result.Data); }
    }

    [UnitTheory]
    [InlineData("actor", "authentication_required")]
    [InlineData("revision", "validation_failed")]
    [InlineData("challenge", "validation_failed")]
    [InlineData("proof", "validation_failed")]
    [InlineData("body", "validation_failed")]
    public async Task GivenInvalidUnlockContext_WhenHandling_ThenRejectBeforeStore(string invalid, string error)
    {
        var command = new UnlockVaultCommand { ExpectedProtectionRevision = invalid == "revision" ? 0 : 1 };
        command.SetProof(invalid == "actor" ? Guid.Empty : s_actor, invalid == "challenge" ? Guid.Empty : Guid.NewGuid(),
            invalid == "proof" ? "bad" : ProtocolBinary.Encode(new byte[64]), invalid == "body" ? [] : s_body);
        var result = await new UnlockVaultHandler(new UnlockVaultValidator(), Strict()).HandleAsync(command);
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
    }

    [UnitTheory]
    [InlineData("valid")]
    [InlineData("missing")]
    [InlineData("invalidAccess")]
    [InlineData("failed")]
    public async Task GivenUnlockStoreResult_WhenHandling_ThenReturnOnlyValidAccessContext(string state)
    {
        var id = Guid.NewGuid();
        var proof = ProtocolBinary.Encode(new byte[64]);
        var command = new UnlockVaultCommand { ExpectedProtectionRevision = 1 };
        command.SetProof(s_actor, id, proof, s_body);
        var now = DateTimeOffset.UtcNow;
        var data = new VaultAccessDetails(s_account, state == "invalidAccess" ? "bad" : ProtocolBinary.Encode(new byte[32]), now, now.AddHours(24));
        var store = new Mock<IVaultProtectionStore>();
        store.Setup(x => x.UnlockAsync(s_actor, 1, id, proof, s_body, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VaultResult<VaultAccessDetails>(state == "missing" ? null : data, state == "failed" ? "vault_proof_rejected" : null));
        var result = await new UnlockVaultHandler(new UnlockVaultValidator(), store.Object).HandleAsync(command);
        if (state == "valid")
        { Assert.True(result.Success); Assert.Equal(data.Access, result.Data!.VaultAccess); Assert.Equal(s_account, result.Data.AccountId); }
        else
        { Assert.Contains(state == "failed" ? "vault_proof_rejected" : "persistence_unavailable", result.Errors); Assert.Null(result.Data); }
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenInitializing_ThenPropagateBeforeStore()
    {
        using var client = new ProtectionFixture();
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new InitializeVaultHandler(new InitializeVaultValidator(), Strict()).HandleAsync(Init(client.Material), source.Token));
    }
    private static InitializeVaultCommand Init(ProtectionMaterial material)
    { var c = new InitializeVaultCommand { AccountId = s_account, ExpectedAccountRevision = 3, Material = material }; c.SetActor(s_actor); return c; }
    private static IVaultProtectionStore Strict() => new Mock<IVaultProtectionStore>(MockBehavior.Strict).Object;
}
