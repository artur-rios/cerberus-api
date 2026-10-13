using System.Text.Json;
using ArturRios.Cerberus.Command.Software;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.TestSupport;
using FluentValidation;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class RevokeSoftwareAccessHandlerTests
{
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid Actor = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Grant = Guid.Parse("33333333-3333-4333-8333-333333333333");

    // Removing a route/revision gate would allow rejected input to reach persistence.
    [UnitTheory]
    [InlineData("grant", 1L)]
    [InlineData("revision", 0L)]
    [InlineData("revision", -1L)]
    [InlineData("revision", long.MinValue)]
    [InlineData("revision", 9007199254740991L)]
    [InlineData("revision", 9007199254740992L)]
    [InlineData("revision", long.MaxValue)]
    public async Task GivenInvalidRouteOrRevision_WhenHandling_ThenRejectBeforeStore(string kind, long revision)
    {
        var command = Command(revision);
        command.SetContext(Actor, Handle, kind == "grant" ? Guid.Empty : Grant);
        Assert.False((await new RevokeSoftwareAccessValidator().ValidateAsync(command)).IsValid);
        AssertFailure(await Handler(RejectingStore()).HandleAsync(command), "validation_failed", 400);
    }

    // Authentication/header/hash must precede validation, even with invalid route/body context.
    [UnitTheory]
    [InlineData("actor", "authentication_required", 401)]
    [InlineData("missingAccess", "vault_access_required", 401)]
    [InlineData("emptyAccess", "validation_failed", 400)]
    [InlineData("malformedAccess", "validation_failed", 400)]
    [InlineData("paddedAccess", "validation_failed", 400)]
    [InlineData("noncanonicalAccess", "validation_failed", 400)]
    [InlineData("whitespaceAccess", "validation_failed", 400)]
    public async Task GivenMissingAuthorityOrInvalidHandle_WhenHandling_ThenRejectBeforeValidation(string kind, string error, int status)
    {
        var command = Command(0);
        command.SetContext(kind == "actor" ? Guid.Empty : Actor, kind switch
        {
            "actor" or "missingAccess" => null,
            "emptyAccess" => "",
            "malformedAccess" => "bad",
            "paddedAccess" => Handle + "=",
            "whitespaceAccess" => " " + Handle,
            _ => new string('A', 42) + "B"
        }, Guid.Empty);
        var validator = new Mock<IValidator<RevokeSoftwareAccessCommand>>(MockBehavior.Strict);
        AssertFailure(await new RevokeSoftwareAccessHandler(validator.Object, RejectingStore()).HandleAsync(command), error, status);
    }

    // Wrong authority/hash/token forwarding, unadvanced revision or leaked output breaks this boundary.
    [UnitTheory]
    [InlineData(1L, 2L)]
    [InlineData(8L, 9L)]
    [InlineData(9007199254740990L, 9007199254740991L)]
    public async Task GivenValidRevocation_WhenHandling_ThenForwardTrustedContextAndReturnExactTwoFields(long expected, long revision)
    {
        using var cancellation = new CancellationTokenSource();
        SoftwareGrantRevocationRequest? received = null;
        CancellationToken receivedToken = default;
        var store = new Store((request, token) =>
        {
            received = request;
            receivedToken = token;
            return Task.FromResult(new VaultResult<SoftwareGrantRevocationDetails>(new(Grant, revision)));
        });
        var result = await Handler(store).HandleAsync(Command(expected), cancellation.Token);
        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "software_access_revoked" }, result.Messages);
        Assert.Equal(Grant, result.Data!.GrantId);
        Assert.Equal(revision, result.Data.Revision);
        Assert.Equal(new[] { "GrantId", "Revision" }, typeof(RevokeSoftwareAccessOutput).GetProperties().Select(x => x.Name).Order().ToArray());
        Assert.Equal("{\"grantId\":\"33333333-3333-4333-8333-333333333333\",\"revision\":" + revision + "}",
            JsonSerializer.Serialize(result.Data, ProtectionFixture.Json));
        Assert.Equal(200, SoftwareGrantRevocationMessages.StatusCodes[result.Messages.Single()]);
        Assert.Equal(Actor, received!.Actor);
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
    [InlineData("software_access_revoked", "persistence_unavailable", 503)]
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
    [InlineData("authentication_required")]
    [InlineData("vault_access_required")]
    [InlineData("")]
    public async Task GivenStoreErrorWithData_WhenHandling_ThenFailClosed(string error) =>
        AssertFailure(await Handler(ReturningStore(new(new(Grant, 2), error))).HandleAsync(Command()), "persistence_unavailable", 503);

    // Acknowledging malformed or unadvanced store evidence would falsely report revocation.
    [UnitTheory]
    [InlineData("nullResult")]
    [InlineData("nullData")]
    [InlineData("grant")]
    [InlineData("zeroGrant")]
    [InlineData("zeroRevision")]
    [InlineData("negativeRevision")]
    [InlineData("unadvancedRevision")]
    [InlineData("wrongRevision")]
    [InlineData("unsafeRevision")]
    [InlineData("overflowRevision")]
    public async Task GivenMalformedStoreSuccess_WhenHandling_Then503WithoutPayload(string kind)
    {
        SoftwareGrantRevocationDetails? details = new(Grant, 2);
        details = kind switch
        {
            "nullData" => null,
            "grant" => details with { GrantId = Actor },
            "zeroGrant" => details with { GrantId = Guid.Empty },
            "zeroRevision" => details with { Revision = 0 },
            "negativeRevision" => details with { Revision = -1 },
            "unadvancedRevision" => details with { Revision = 1 },
            "wrongRevision" => details with { Revision = 3 },
            "unsafeRevision" => details with { Revision = 9007199254740992 },
            "overflowRevision" => details with { Revision = long.MaxValue },
            _ => details
        };
        AssertFailure(await Handler(ReturningStore(kind == "nullResult" ? null! : new(details))).HandleAsync(Command()), "persistence_unavailable", 503);
    }

    [UnitFact]
    public async Task GivenCancelledCallerAndMissingAuthority_WhenHandling_ThenCancellationTakesPrecedence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var command = Command(0);
        command.SetContext(Guid.Empty, null, Guid.Empty);
        var validator = new Mock<IValidator<RevokeSoftwareAccessCommand>>(MockBehavior.Strict);
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new RevokeSoftwareAccessHandler(validator.Object, RejectingStore()).HandleAsync(command, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [UnitFact]
    public async Task GivenStoreCancellation_WhenHandling_ThenPropagateExactCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Store((_, token) => Task.FromException<VaultResult<SoftwareGrantRevocationDetails>>(new OperationCanceledException(token)));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(store).HandleAsync(Command(), cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    private static RevokeSoftwareAccessCommand Command(long revision = 1)
    {
        var command = new RevokeSoftwareAccessCommand { ExpectedRevision = revision };
        command.SetContext(Actor, Handle, Grant);
        return command;
    }

    private static RevokeSoftwareAccessHandler Handler(ISoftwareGrantRevocationStore store) => new(new RevokeSoftwareAccessValidator(), store);
    private static Store ReturningStore(VaultResult<SoftwareGrantRevocationDetails> result) => new((_, _) => Task.FromResult(result));
    private static Store RejectingStore() => new((_, _) => throw new InvalidOperationException("Rejected input reached persistence"));
    private static void AssertFailure(ArturRios.Output.DataOutput<RevokeSoftwareAccessOutput?> result, string error, int status)
    {
        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Empty(result.Messages);
        Assert.Equal(new[] { error }, result.Errors);
        Assert.Equal(status, SoftwareGrantRevocationMessages.StatusCodes[result.Errors.Single()]);
    }

    // Only external persistence is substituted; parsing, hashing, validation and mapping execute real code.
    private sealed class Store(Func<SoftwareGrantRevocationRequest, CancellationToken, Task<VaultResult<SoftwareGrantRevocationDetails>>> revoke)
        : ISoftwareGrantRevocationStore
    {
        public Task<VaultResult<SoftwareGrantRevocationDetails>> RevokeAsync(SoftwareGrantRevocationRequest request, CancellationToken cancellationToken) =>
            revoke(request, cancellationToken);
    }
}
