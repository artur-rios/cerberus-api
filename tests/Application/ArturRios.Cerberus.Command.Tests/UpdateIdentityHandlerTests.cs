using System.Text.Json;
using ArturRios.Cerberus.Command.Identity;
using ArturRios.Cerberus.Domain.Identity;
using ArturRios.Cerberus.Shared.Identity;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class UpdateIdentityHandlerTests
{
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Verifier = "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925";
    private static readonly Guid Actor = Guid.NewGuid();

    [UnitTheory]
    [InlineData("actor", "authentication_required")]
    [InlineData("token", "authentication_required")]
    [InlineData("access", "vault_access_required")]
    [InlineData("emptyAccess", "validation_failed")]
    [InlineData("badAccess", "validation_failed")]
    [InlineData("name", "validation_failed")]
    [InlineData("longName", "validation_failed")]
    [InlineData("email", "validation_failed")]
    [InlineData("badEmail", "validation_failed")]
    public async Task GivenInvalidInputOrContext_WhenUpdating_ThenNeverReachStoreOrProvider(string invalid, string error)
    {
        var command = Valid();
        command.SetActor(invalid == "actor" ? Guid.Empty : Actor, invalid == "token" ? "" : "original-token",
            invalid == "access" ? null : invalid == "emptyAccess" ? "" : invalid == "badAccess" ? "bad" : Handle);
        if (invalid == "name") command.Name = " ";
        if (invalid == "longName") command.Name = new string('x', 201);
        if (invalid == "email") command.Email = null!;
        if (invalid == "badEmail") command.Email = "invalid";
        var result = await Handler(new Mock<IIdentityUpdateStore>(MockBehavior.Strict).Object,
            new Mock<IHeimdallClient>(MockBehavior.Strict).Object).HandleAsync(command);
        Assert.False(result.Success);
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
    }

    [UnitFact]
    public async Task GivenAuthorizedStoreCallback_WhenUpdating_ThenForwardOnlyOriginalActorAndReturnCuratedIdentity()
    {
        var insideGuard = false;
        using var source = new CancellationTokenSource();
        var provider = new Mock<IHeimdallClient>(MockBehavior.Strict);
        provider.Setup(x => x.UpdateIdentityAsync("original-token", Actor, "New Name", "new@example.test", source.Token))
            .Returns(() => { Assert.True(insideGuard); return Task.FromResult(new IdentityUpdateResult(new(Actor, "New Name", "new@example.test", false))); });
        var store = new Mock<IIdentityUpdateStore>(MockBehavior.Strict);
        store.Setup(x => x.UpdateAsync(Actor, Verifier, It.IsAny<Func<CancellationToken, Task<IdentityUpdateResult>>>(), source.Token))
            .Returns<Guid, string, Func<CancellationToken, Task<IdentityUpdateResult>>, CancellationToken>(async (_, _, update, ct) =>
            { insideGuard = true; try { return await update(ct); } finally { insideGuard = false; } });
        var command = Valid();
        var result = await Handler(store.Object, provider.Object).HandleAsync(command, source.Token);
        Assert.True(result.Success);
        Assert.Equal(Actor, result.Data!.Id);
        Assert.Equal("New Name", result.Data.Name);
        Assert.Equal("new@example.test", result.Data.Email);
        Assert.False(result.Data.EmailVerified);
        Assert.Contains("identity_updated", result.Messages);
        provider.VerifyAll();
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(command, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        Assert.Equal(new[] { "email", "name" }, body.RootElement.EnumerateObject().Select(x => x.Name).Order().ToArray());
    }

    [UnitTheory]
    [InlineData("not_found", 404)]
    [InlineData("vault_access_denied", 403)]
    [InlineData("persistence_unavailable", 503)]
    [InlineData("validation_failed", 400)]
    [InlineData("authentication_required", 401)]
    [InlineData("identity_update_forbidden", 403)]
    [InlineData("identity_conflict", 409)]
    [InlineData("identity_unavailable", 503)]
    public async Task GivenFailedGuardOrProvider_WhenUpdating_ThenReturnStableErrorWithoutData(string error, int status)
    {
        var store = new Mock<IIdentityUpdateStore>();
        store.Setup(x => x.UpdateAsync(Actor, Verifier, It.IsAny<Func<CancellationToken, Task<IdentityUpdateResult>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityUpdateResult(Error: error));
        var result = await Handler(store.Object, new Mock<IHeimdallClient>(MockBehavior.Strict).Object).HandleAsync(Valid());
        Assert.False(result.Success);
        Assert.Contains(error, result.Errors);
        Assert.Null(result.Data);
        Assert.Equal(status, UpdateIdentityMessages.StatusCodes[error]);
    }

    [UnitTheory]
    [InlineData("missing")]
    [InlineData("foreign")]
    [InlineData("name")]
    [InlineData("email")]
    public async Task GivenIncompatibleStoreResult_WhenUpdating_ThenFailClosed(string invalid)
    {
        IdentityDetails? identity = invalid == "missing" ? null : new(invalid == "foreign" ? Guid.NewGuid() : Actor,
            invalid == "name" ? "Other" : "New Name", invalid == "email" ? "other@example.test" : "new@example.test", false);
        var store = new Mock<IIdentityUpdateStore>();
        store.Setup(x => x.UpdateAsync(Actor, Verifier, It.IsAny<Func<CancellationToken, Task<IdentityUpdateResult>>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new IdentityUpdateResult(identity));
        var result = await Handler(store.Object, new Mock<IHeimdallClient>(MockBehavior.Strict).Object).HandleAsync(Valid());
        Assert.Contains("identity_unavailable", result.Errors);
        Assert.Null(result.Data);
    }

    [UnitFact]
    public async Task GivenCallerCancellation_WhenUpdating_ThenPropagateBeforeStore()
    {
        using var source = new CancellationTokenSource();
        source.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(new Mock<IIdentityUpdateStore>(MockBehavior.Strict).Object,
            new Mock<IHeimdallClient>(MockBehavior.Strict).Object).HandleAsync(Valid(), source.Token));
    }

    private static UpdateIdentityCommand Valid()
    {
        var command = new UpdateIdentityCommand { Name = "New Name", Email = "new@example.test" };
        command.SetActor(Actor, "original-token", Handle);
        return command;
    }
    private static UpdateIdentityHandler Handler(IIdentityUpdateStore store, IHeimdallClient provider) => new(new UpdateIdentityValidator(), store, provider);
}
