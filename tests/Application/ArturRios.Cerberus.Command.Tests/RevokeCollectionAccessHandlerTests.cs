using System.Text.Json;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using FluentValidation;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class RevokeCollectionAccessHandlerTests
{
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid Actor = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Collection = Guid.Parse("22222222-2222-4222-8222-222222222222");
    private static readonly Guid Grant = Guid.Parse("33333333-3333-4333-8333-333333333333");

    // Omitting any validation gate would reach the rejecting store instead of returning 400.
    [UnitTheory]
    [InlineData("collection")]
    [InlineData("grant")]
    [InlineData("zeroRevision")]
    [InlineData("negativeRevision")]
    [InlineData("exhaustedRevision")]
    [InlineData("unsafeRevision")]
    [InlineData("overflowRevision")]
    public async Task GivenInvalidPathOrRevision_WhenHandling_ThenRejectBeforeStore(string kind)
    {
        var command = Command(kind switch
        {
            "zeroRevision" => 0, "negativeRevision" => -1,
            "exhaustedRevision" => 9007199254740991, "unsafeRevision" => 9007199254740992,
            "overflowRevision" => long.MaxValue, _ => 1
        });
        command.SetContext(Actor, Handle, kind == "collection" ? Guid.Empty : Collection, kind == "grant" ? Guid.Empty : Grant);
        Assert.False((await new RevokeCollectionAccessValidator().ValidateAsync(command)).IsValid);
        AssertFailure(await Handler(RejectingStore()).HandleAsync(command), "validation_failed", 400);
    }

    // Reordering authority/header/hash after validation would invoke the strict validator.
    [UnitTheory]
    [InlineData("actor", "authentication_required", 401)]
    [InlineData("missingAccess", "vault_access_required", 401)]
    [InlineData("emptyAccess", "validation_failed", 400)]
    [InlineData("malformedAccess", "validation_failed", 400)]
    [InlineData("paddedAccess", "validation_failed", 400)]
    [InlineData("noncanonicalAccess", "validation_failed", 400)]
    public async Task GivenMissingAuthorityOrInvalidHandle_WhenHandling_ThenRejectBeforeValidation(string kind, string error, int status)
    {
        var command = Command(0);
        command.SetContext(kind == "actor" ? Guid.Empty : Actor, kind switch
        {
            "actor" or "missingAccess" => null, "emptyAccess" => "", "malformedAccess" => "bad",
            "paddedAccess" => Handle + "=", _ => new string('A', 42) + "B"
        }, Guid.Empty, Guid.Empty);
        var validator = new Mock<IValidator<RevokeCollectionAccessCommand>>(MockBehavior.Strict);
        AssertFailure(await new RevokeCollectionAccessHandler(validator.Object, RejectingStore()).HandleAsync(command), error, status);
    }

    // Wrong actor/path/hash/token forwarding or a default/leaky output fails these literal expectations.
    [UnitTheory]
    [InlineData(1L, 2L)]
    [InlineData(8L, 9L)]
    [InlineData(9007199254740990L, 9007199254740991L)]
    public async Task GivenValidRevocation_WhenHandling_ThenForwardTrustedContextAndReturnExactThreeFields(long expected, long revision)
    {
        using var cancellation = new CancellationTokenSource();
        var command = Command(expected);
        CollectionGrantRevocationRequest? received = null;
        CancellationToken receivedToken = default;
        var store = new Store((request, token) =>
        {
            received = request;
            receivedToken = token;
            return Task.FromResult(new VaultResult<CollectionGrantRevocationDetails>(new(Collection, Grant, revision)));
        });
        var result = await Handler(store).HandleAsync(command, cancellation.Token);
        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "collection_access_revoked" }, result.Messages);
        Assert.Equal(Collection, result.Data!.CollectionId);
        Assert.Equal(Grant, result.Data.GrantId);
        Assert.Equal(command.ExpectedRevision + 1, result.Data.Revision);
        Assert.Equal(revision, result.Data.Revision);
        Assert.Equal(new[] { "CollectionId", "GrantId", "Revision" },
            typeof(RevokeCollectionAccessOutput).GetProperties().Select(x => x.Name).Order().ToArray());
        Assert.Equal("{\"collectionId\":\"22222222-2222-4222-8222-222222222222\",\"grantId\":\"33333333-3333-4333-8333-333333333333\",\"revision\":" + revision + "}",
            JsonSerializer.Serialize(result.Data, ProtectionFixture.Json));
        Assert.Equal(200, CollectionGrantRevocationMessages.StatusCodes[result.Messages.Single()]);
        Assert.Equal(Actor, received!.Actor);
        Assert.Equal(Collection, received.CollectionId);
        Assert.Equal(Grant, received.GrantId);
        Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", received.AccessVerifier);
        Assert.Equal(expected, received.Input.ExpectedRevision);
        Assert.Equal(cancellation.Token, receivedToken);
    }

    [UnitTheory]
    [InlineData("validation_failed", "validation_failed", 400)]
    [InlineData("vault_access_denied", "vault_access_denied", 403)]
    [InlineData("not_found", "not_found", 404)]
    [InlineData("revision_conflict", "revision_conflict", 409)]
    [InlineData("persistence_unavailable", "persistence_unavailable", 503)]
    [InlineData("secret_recipient_123", "persistence_unavailable", 503)]
    [InlineData("authentication_required", "persistence_unavailable", 503)]
    [InlineData("vault_access_required", "persistence_unavailable", 503)]
    [InlineData("identity_unavailable", "persistence_unavailable", 503)]
    [InlineData("collection_access_revoked", "persistence_unavailable", 503)]
    [InlineData("", "persistence_unavailable", 503)]
    public async Task GivenStoreError_WhenHandling_ThenExposeOnlyAllowlistedFailures(string error, string expected, int status) =>
        AssertFailure(await Handler(ReturningStore(new(Error: error))).HandleAsync(Command()), expected, status);

    [UnitTheory]
    [InlineData("not_found")]
    [InlineData("validation_failed")]
    [InlineData("vault_access_denied")]
    [InlineData("revision_conflict")]
    [InlineData("persistence_unavailable")]
    [InlineData("secret_recipient_123")]
    [InlineData("")]
    public async Task GivenStoreErrorWithData_WhenHandling_ThenFailClosed(string error) =>
        AssertFailure(await Handler(ReturningStore(new(new(Collection, Grant, 2), error))).HandleAsync(Command()), "persistence_unavailable", 503);

    // Trusting null/corrupt store evidence or an unadvanced revision would falsely acknowledge revocation.
    [UnitTheory]
    [InlineData("nullResult")]
    [InlineData("nullData")]
    [InlineData("collection")]
    [InlineData("zeroCollection")]
    [InlineData("grant")]
    [InlineData("zeroGrant")]
    [InlineData("zeroRevision")]
    [InlineData("negativeRevision")]
    [InlineData("unadvancedRevision")]
    [InlineData("wrongRevision")]
    [InlineData("unsafeRevision")]
    public async Task GivenMalformedStoreSuccess_WhenHandling_Then503WithoutPayload(string kind)
    {
        CollectionGrantRevocationDetails? details = new(Collection, Grant, 2);
        details = kind switch
        {
            "nullData" => null, "collection" => details with { CollectionId = Actor },
            "zeroCollection" => details with { CollectionId = Guid.Empty },
            "grant" => details with { GrantId = Actor }, "zeroGrant" => details with { GrantId = Guid.Empty },
            "zeroRevision" => details with { Revision = 0 }, "negativeRevision" => details with { Revision = -1 },
            "unadvancedRevision" => details with { Revision = 1 }, "wrongRevision" => details with { Revision = 3 },
            "unsafeRevision" => details with { Revision = 9007199254740992 }, _ => details
        };
        AssertFailure(await Handler(ReturningStore(kind == "nullResult" ? null! : new(details))).HandleAsync(Command()), "persistence_unavailable", 503);
    }

    [UnitFact]
    public async Task GivenCancelledCallerAndMissingAuthority_WhenHandling_ThenCancellationTakesPrecedence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var command = Command(0);
        command.SetContext(Guid.Empty, null, Guid.Empty, Guid.Empty);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(RejectingStore()).HandleAsync(command, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [UnitFact]
    public async Task GivenStoreCancellation_WhenHandling_ThenPropagateExactCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Store((_, token) => Task.FromException<VaultResult<CollectionGrantRevocationDetails>>(new OperationCanceledException(token)));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(store).HandleAsync(Command(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private static RevokeCollectionAccessCommand Command(long revision = 1)
    {
        var command = new RevokeCollectionAccessCommand { ExpectedRevision = revision };
        command.SetContext(Actor, Handle, Collection, Grant);
        return command;
    }

    private static RevokeCollectionAccessHandler Handler(ICollectionGrantRevocationStore store) => new(new RevokeCollectionAccessValidator(), store);
    private static Store ReturningStore(VaultResult<CollectionGrantRevocationDetails> result) => new((_, _) => Task.FromResult(result));
    private static Store RejectingStore() => new((_, _) => throw new InvalidOperationException("Rejected input reached persistence"));
    private static void AssertFailure(ArturRios.Output.DataOutput<RevokeCollectionAccessOutput?> result, string error, int status)
    {
        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Empty(result.Messages);
        Assert.Equal(new[] { error }, result.Errors);
        Assert.Equal(status, CollectionGrantRevocationMessages.StatusCodes[result.Errors.Single()]);
    }

    // The external store is the only double; parsing, hashing, validation and mapping execute real code.
    private sealed class Store(Func<CollectionGrantRevocationRequest, CancellationToken, Task<VaultResult<CollectionGrantRevocationDetails>>> revoke)
        : ICollectionGrantRevocationStore
    {
        public Task<VaultResult<CollectionGrantRevocationDetails>> RevokeAsync(CollectionGrantRevocationRequest request, CancellationToken cancellationToken) =>
            revoke(request, cancellationToken);
    }
}
