using System.Text.Json;
using ArturRios.Cerberus.Command.Accounts;
using ArturRios.Cerberus.Domain.Accounts;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class UpdateAccountHandlerTests
{
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Verifier = "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925";
    private static readonly Guid Identity = Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");
    private static readonly DateTimeOffset Now = new(2026, 10, 8, 12, 0, 0, TimeSpan.Zero);

    [UnitTheory]
    [InlineData("identity", "authentication_required")]
    [InlineData("missingAccess", "vault_access_required")]
    [InlineData("emptyAccess", "validation_failed")]
    [InlineData("malformedAccess", "validation_failed")]
    [InlineData("zeroRevision", "validation_failed")]
    [InlineData("negativeRevision", "validation_failed")]
    [InlineData("nullEnvelope", "validation_failed")]
    [InlineData("invalidEnvelope", "validation_failed")]
    public async Task GivenInvalidInputOrAccess_WhenUpdating_ThenRejectBeforePersistence(string invalid, string error)
    {
        var command = Valid();
        command.SetAccess(invalid == "identity" ? Guid.Empty : Identity,
            invalid == "missingAccess" ? null : invalid == "emptyAccess" ? "" : invalid == "malformedAccess" ? "bad" : Handle);
        if (invalid == "zeroRevision") command.ExpectedRevision = 0;
        if (invalid == "negativeRevision") command.ExpectedRevision = -1;
        if (invalid == "nullEnvelope") command.Details = null!;
        if (invalid == "invalidEnvelope") command.Details = command.Details with { Format = "unsupported" };
        var result = await Handler(new Mock<IAccountUpdateStore>(MockBehavior.Strict).Object).HandleAsync(command);
        Assert.False(result.Success);
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
    }

    [UnitFact]
    public async Task GivenValidUpdate_WhenHandling_ThenPersistOnlyTrustedActorVerifierAndOpaqueEnvelope()
    {
        var id = Guid.NewGuid();
        var command = Valid();
        AccountUpdateRequest? captured = null;
        var store = new Mock<IAccountUpdateStore>(MockBehavior.Strict);
        store.Setup(x => x.UpdateAsync(It.IsAny<AccountUpdateRequest>(), It.IsAny<CancellationToken>()))
            .Returns<AccountUpdateRequest, CancellationToken>((request, _) =>
            {
                captured = request;
                return Task.FromResult(new AccountUpdateResult(id, 4));
            });
        var result = await Handler(store.Object).HandleAsync(command);
        Assert.True(result.Success);
        Assert.Equal(id, result.Data!.Id);
        Assert.Equal(4, result.Data.Revision);
        Assert.Contains("account_updated", result.Messages);
        Assert.NotNull(captured);
        Assert.Equal(Identity, captured.IdentityId);
        Assert.Equal(Verifier, captured.Verifier);
        Assert.Equal(3, captured.ExpectedRevision);
        Assert.Equal(Now, captured.Now);
        Assert.Equal(command.Details, JsonSerializer.Deserialize<EncryptedEnvelope>(captured.DetailsEnvelope, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.DoesNotContain("VaultAccess", JsonSerializer.Serialize(command));
        Assert.DoesNotContain(Identity.ToString(), JsonSerializer.Serialize(command));
    }

    [UnitTheory]
    [InlineData("not_found")]
    [InlineData("vault_access_denied")]
    [InlineData("revision_conflict")]
    [InlineData("persistence_unavailable")]
    public async Task GivenDeniedConflictingOrUnavailableStore_WhenUpdating_ThenReturnErrorWithoutData(string error)
    {
        var store = new Mock<IAccountUpdateStore>();
        store.Setup(x => x.UpdateAsync(It.IsAny<AccountUpdateRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AccountUpdateResult(Error: error));
        var result = await Handler(store.Object).HandleAsync(Valid());
        Assert.False(result.Success);
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
    }

    [UnitFact]
    public async Task GivenIncompleteStoreResult_WhenUpdating_ThenFailClosed()
    {
        var store = new Mock<IAccountUpdateStore>();
        store.Setup(x => x.UpdateAsync(It.IsAny<AccountUpdateRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(new AccountUpdateResult());
        var result = await Handler(store.Object).HandleAsync(Valid());
        Assert.Contains("persistence_unavailable", result.Errors);
        Assert.Null(result.Data);
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenUpdating_ThenDoNotReachStore()
    {
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(new Mock<IAccountUpdateStore>(MockBehavior.Strict).Object).HandleAsync(Valid(), cancelled.Token));
    }

    private static UpdateAccountCommand Valid()
    {
        var command = new UpdateAccountCommand { ExpectedRevision = 3,
            Details = new("cerberus-content-v1", 1, Handle, "AAAAAAAAAAAAAAAA", "BAUG", "AAAAAAAAAAAAAAAAAAAAAA") };
        command.SetAccess(Identity, Handle);
        return command;
    }
    private static UpdateAccountHandler Handler(IAccountUpdateStore store) => new(new UpdateAccountValidator(), store, new Clock());
    private sealed class Clock : TimeProvider { public override DateTimeOffset GetUtcNow() => Now; }
}
