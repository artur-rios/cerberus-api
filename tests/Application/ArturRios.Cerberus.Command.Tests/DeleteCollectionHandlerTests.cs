using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class DeleteCollectionHandlerTests
{
    private const string Access = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Guid actor = Guid.NewGuid(), target = Guid.NewGuid(), operation = Guid.NewGuid();
    private static readonly JsonSerializerOptions Json = new(ProtectionFixture.Json)
    {
        NumberHandling = JsonNumberHandling.Strict
    };

    [UnitTheory]
    [InlineData("actor", "authentication_required")]
    [InlineData("missing", "vault_access_required")]
    [InlineData("empty", "validation_failed")]
    [InlineData("handle", "validation_failed")]
    [InlineData("noncanonical", "validation_failed")]
    [InlineData("id", "validation_failed")]
    [InlineData("zero", "validation_failed")]
    [InlineData("negative", "validation_failed")]
    [InlineData("unsafe", "validation_failed")]
    public async Task GivenInvalidAuthorityOrRevision_WhenHandling_ThenRejectBeforePersistence(string kind, string error)
    {
        var command = Command();
        var access = kind switch
        {
            "missing" => null,
            "empty" => "",
            "handle" => "bad",
            "noncanonical" => new string('A', 42) + "B",
            _ => Access
        };
        command.SetContext(kind == "actor" ? Guid.Empty : actor, access, kind == "id" ? Guid.Empty : target);
        command.ExpectedRevision = kind switch
        {
            "zero" => 0,
            "negative" => -1,
            "unsafe" => ProtocolBinary.MaxInteger + 1,
            _ => 1
        };

        var result = await Handler(new Mock<ICollectionTrashStore>(MockBehavior.Strict).Object).HandleAsync(command);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Contains(error, result.Errors);
    }

    [UnitTheory]
    [InlineData("normal")]
    [InlineData("minimum")]
    [InlineData("pre2000")]
    [InlineData("largestRevision")]
    public async Task GivenValidTrashMetadata_WhenHandling_ThenForwardTrustedContextAndReturnExactSixFields(string kind)
    {
        var command = Command();
        if (kind == "largestRevision") command.ExpectedRevision = 9007199254740990;
        var time = kind switch
        {
            "minimum" => new DateTimeOffset(10, TimeSpan.Zero),
            "pre2000" => new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero),
            _ => DateTimeOffset.UnixEpoch
        };
        var revision = kind == "largestRevision" ? 9007199254740991 : 2;
        var details = new CollectionTrashDetails(target, operation, revision, 7, time, time.AddHours(720));
        using var cancellation = new CancellationTokenSource();
        CollectionTrashRequest? received = null;
        CancellationToken receivedToken = default;
        var store = new Mock<ICollectionTrashStore>(MockBehavior.Strict);
        store.Setup(x => x.TrashAsync(It.IsAny<CollectionTrashRequest>(), It.IsAny<CancellationToken>()))
            .Returns<CollectionTrashRequest, CancellationToken>((request, token) =>
            {
                received = request;
                receivedToken = token;
                return Task.FromResult(new VaultResult<CollectionTrashDetails>(details));
            });

        var result = await Handler(store.Object).HandleAsync(command, cancellation.Token);

        Assert.True(result.Success);
        Assert.Contains("collection_deleted", result.Messages);
        Assert.Equal(target, result.Data!.CollectionId);
        Assert.Equal(operation, result.Data.TrashOperationId);
        Assert.Equal(revision, result.Data.Revision);
        Assert.Equal(7, result.Data.ServerSequence);
        Assert.Equal(time, result.Data.DeletedAt);
        Assert.Equal(time.AddHours(720), result.Data.PurgeAt);
        Assert.Equal(actor, received!.Actor);
        Assert.Equal(target, received.CollectionId);
        Assert.Equal(command.ExpectedRevision, received.ExpectedRevision);
        Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", received.AccessVerifier);
        Assert.Equal(cancellation.Token, receivedToken);
        store.Verify(x => x.TrashAsync(It.IsAny<CollectionTrashRequest>(), cancellation.Token), Times.Once);
        using var output = JsonDocument.Parse(JsonSerializer.Serialize(result.Data, Json));
        Assert.Equal(new[] { "collectionId", "deletedAt", "purgeAt", "revision", "serverSequence", "trashOperationId" },
            output.RootElement.EnumerateObject().Select(x => x.Name).Order());
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(command, Json));
        Assert.Equal(new[] { "expectedRevision" }, input.RootElement.EnumerateObject().Select(x => x.Name));
    }

    [UnitTheory]
    [InlineData("validation_failed", 400)]
    [InlineData("not_found", 404)]
    [InlineData("vault_access_denied", 403)]
    [InlineData("revision_conflict", 409)]
    [InlineData("persistence_unavailable", 503)]
    [InlineData("secret_owner_123", 503)]
    [InlineData("authentication_required", 503)]
    [InlineData("vault_access_required", 503)]
    [InlineData("identity_unavailable", 503)]
    [InlineData("collection_deleted", 503)]
    [InlineData("", 503)]
    public async Task GivenStoreFailure_WhenHandling_ThenOnlyAllowExpectedSafeCodes(string error, int status)
    {
        var store = new Mock<ICollectionTrashStore>();
        store.Setup(x => x.TrashAsync(It.IsAny<CollectionTrashRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VaultResult<CollectionTrashDetails>(Error: error));

        var result = await Handler(store.Object).HandleAsync(Command());

        var expected = error is "validation_failed" or "not_found" or "vault_access_denied" or "revision_conflict"
            ? error : "persistence_unavailable";
        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { expected }, result.Errors);
        Assert.Equal(status, DeleteCollectionMessages.StatusCodes[expected]);
    }

    [UnitTheory]
    [InlineData("not_found")]
    [InlineData("validation_failed")]
    [InlineData("vault_access_denied")]
    [InlineData("revision_conflict")]
    [InlineData("persistence_unavailable")]
    [InlineData("secret_owner_123")]
    public async Task GivenStoreErrorAndData_WhenHandling_Then503WithoutPartialPayload(string error)
    {
        var store = new Mock<ICollectionTrashStore>();
        store.Setup(x => x.TrashAsync(It.IsAny<CollectionTrashRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VaultResult<CollectionTrashDetails>(Details(), error));

        var result = await Handler(store.Object).HandleAsync(Command());

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { "persistence_unavailable" }, result.Errors);
    }

    [UnitTheory]
    [InlineData("nullResult")]
    [InlineData("nullData")]
    [InlineData("collectionId")]
    [InlineData("emptyId")]
    [InlineData("operation")]
    [InlineData("revision0")]
    [InlineData("revisionOld")]
    [InlineData("revisionAhead")]
    [InlineData("unsafeRevision")]
    [InlineData("sequence0")]
    [InlineData("negativeSequence")]
    [InlineData("unsafeSequence")]
    [InlineData("defaultTime")]
    [InlineData("submicroTime")]
    [InlineData("timeOffset")]
    [InlineData("purgeOffset")]
    [InlineData("purgeEarlier")]
    [InlineData("purgeLate")]
    [InlineData("purgePrecision")]
    [InlineData("exhaustedRevision")]
    public async Task GivenMalformedStoreSuccess_WhenHandling_Then503WithoutAnyMetadata(string kind)
    {
        var command = Command();
        var details = Details();
        CollectionTrashDetails? returned = kind switch
        {
            "nullData" => null,
            "collectionId" => details with { CollectionId = Guid.NewGuid() },
            "emptyId" => details with { CollectionId = Guid.Empty },
            "operation" => details with { TrashOperationId = Guid.Empty },
            "revision0" => details with { Revision = 0 },
            "revisionOld" => details with { Revision = 1 },
            "revisionAhead" => details with { Revision = 3 },
            "unsafeRevision" => details with { Revision = 9007199254740992 },
            "sequence0" => details with { ServerSequence = 0 },
            "negativeSequence" => details with { ServerSequence = -1 },
            "unsafeSequence" => details with { ServerSequence = 9007199254740992 },
            "defaultTime" => details with { DeletedAt = default, PurgeAt = DateTimeOffset.MinValue.AddHours(720) },
            "submicroTime" => details with { DeletedAt = new(9, TimeSpan.Zero), PurgeAt = new DateTimeOffset(9, TimeSpan.Zero).AddHours(720) },
            "timeOffset" => details with { DeletedAt = details.DeletedAt.ToOffset(TimeSpan.FromHours(1)) },
            "purgeOffset" => details with { PurgeAt = details.PurgeAt.ToOffset(TimeSpan.FromHours(1)) },
            "purgeEarlier" => details with { PurgeAt = details.DeletedAt.AddHours(719) },
            "purgeLate" => details with { PurgeAt = details.DeletedAt.AddHours(721) },
            "purgePrecision" => details with { PurgeAt = details.PurgeAt.AddTicks(1) },
            "exhaustedRevision" => details with { Revision = 9007199254740992 },
            _ => details
        };
        if (kind == "exhaustedRevision") command.ExpectedRevision = 9007199254740991;
        var store = new Mock<ICollectionTrashStore>();
        store.Setup(x => x.TrashAsync(It.IsAny<CollectionTrashRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(kind == "nullResult" ? null! : new VaultResult<CollectionTrashDetails>(returned));

        var result = await Handler(store.Object).HandleAsync(command);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { "persistence_unavailable" }, result.Errors);
    }

    [UnitFact]
    public void GivenOmittedExpectedRevision_WhenParsing_ThenRejectRequiredMember() =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DeleteCollectionCommand>("{}", Json));

    [UnitTheory]
    [InlineData("actor")]
    [InlineData("vaultAccess")]
    [InlineData("collectionId")]
    [InlineData("ownerId")]
    [InlineData("profileIds")]
    [InlineData("recordIds")]
    [InlineData("folderIds")]
    [InlineData("grants")]
    [InlineData("keyEpoch")]
    [InlineData("keyWrappers")]
    [InlineData("envelope")]
    [InlineData("editedAt")]
    [InlineData("trashOperationId")]
    [InlineData("deletedAt")]
    [InlineData("purgeAt")]
    public void GivenForgedContextOrAlternateInput_WhenParsing_ThenReject(string field)
    {
        var body = JsonSerializer.SerializeToNode(Command(), Json)!.AsObject();
        body[field] = null;

        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DeleteCollectionCommand>(body.ToJsonString(), Json));
    }

    [UnitTheory]
    [InlineData("{\"expectedRevision\":1,\"expectedRevision\":1}")]
    [InlineData("{\"ExpectedRevision\":1}")]
    [InlineData("{\"expectedRevision\":\"1\"}")]
    [InlineData("{\"expectedRevision\":null}")]
    [InlineData("{\"expectedRevision\":1.5}")]
    [InlineData("{\"expectedRevision\":9223372036854775808}")]
    public void GivenAlternateRevisionJson_WhenParsing_ThenReject(string body) =>
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<DeleteCollectionCommand>(body, Json));

    [UnitFact]
    public async Task GivenCancelledCaller_WhenHandling_ThenPropagateBeforePersistence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(new Mock<ICollectionTrashStore>(MockBehavior.Strict).Object).HandleAsync(Command(), cancellation.Token));
    }

    [UnitFact]
    public async Task GivenPersistenceCancellation_WhenHandling_ThenPropagateExactToken()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Mock<ICollectionTrashStore>();
        store.Setup(x => x.TrashAsync(It.IsAny<CollectionTrashRequest>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(store.Object).HandleAsync(Command(), cancellation.Token));

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [UnitFact]
    public void GivenDeletedMessage_WhenMapping_Then200() =>
        Assert.Equal(200, DeleteCollectionMessages.StatusCodes["collection_deleted"]);

    private DeleteCollectionCommand Command()
    {
        var command = new DeleteCollectionCommand { ExpectedRevision = 1 };
        command.SetContext(actor, Access, target);
        return command;
    }

    private CollectionTrashDetails Details() =>
        new(target, operation, 2, 7, DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch.AddHours(720));

    private static DeleteCollectionHandler Handler(ICollectionTrashStore store) => new(new DeleteCollectionValidator(), store);
}
