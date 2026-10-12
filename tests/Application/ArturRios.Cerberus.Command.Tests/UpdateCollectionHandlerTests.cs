using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class UpdateCollectionHandlerTests
{
    private const string Access = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Guid actor = Guid.NewGuid();
    private readonly Guid target = Guid.NewGuid();
    private static readonly JsonSerializerOptions Json = new(ProtectionFixture.Json)
    { NumberHandling = JsonNumberHandling.Strict };

    [UnitTheory]
    [InlineData("actor", "authentication_required")]
    [InlineData("missing", "vault_access_required")]
    [InlineData("empty", "validation_failed")]
    [InlineData("handle", "validation_failed")]
    [InlineData("id", "validation_failed")]
    [InlineData("revision", "validation_failed")]
    [InlineData("negativeRevision", "validation_failed")]
    [InlineData("unsafeRevision", "validation_failed")]
    [InlineData("envelope", "validation_failed")]
    [InlineData("format", "validation_failed")]
    [InlineData("epoch", "validation_failed")]
    [InlineData("unsafeEpoch", "validation_failed")]
    [InlineData("salt", "validation_failed")]
    [InlineData("nonce", "validation_failed")]
    [InlineData("tag", "validation_failed")]
    [InlineData("cipher", "validation_failed")]
    [InlineData("time", "validation_failed")]
    [InlineData("offset", "validation_failed")]
    public async Task GivenInvalidAuthorityOrInput_WhenHandling_ThenRejectBeforePersistence(string kind, string error)
    {
        var command = Command();
        command.SetContext(kind == "actor" ? Guid.Empty : actor,
            kind == "missing" ? null : kind == "empty" ? "" : kind == "handle" ? "bad" : Access,
            kind == "id" ? Guid.Empty : target);
        switch (kind)
        {
            case "revision": command.ExpectedRevision = 0; break;
            case "negativeRevision": command.ExpectedRevision = -1; break;
            case "unsafeRevision": command.ExpectedRevision = 9007199254740992; break;
            case "envelope": command.Envelope = null!; break;
            case "format": command.Envelope = command.Envelope with { Format = "unknown" }; break;
            case "epoch": command.Envelope = command.Envelope with { KeyEpoch = 0 }; break;
            case "unsafeEpoch": command.Envelope = command.Envelope with { KeyEpoch = 9007199254740992 }; break;
            case "salt": command.Envelope = command.Envelope with { KeySalt = "bad" }; break;
            case "nonce": command.Envelope = command.Envelope with { Nonce = "bad" }; break;
            case "tag": command.Envelope = command.Envelope with { Tag = "bad" }; break;
            case "cipher": command.Envelope = command.Envelope with { Ciphertext = "AQID=" }; break;
            case "time": command.EditedAt = new(9, TimeSpan.Zero); break;
            case "offset": command.EditedAt = command.EditedAt.ToOffset(TimeSpan.FromHours(1)); break;
        }

        var result = await Handler(new Mock<ICollectionUpdateStore>(MockBehavior.Strict).Object).HandleAsync(command);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Contains(error, result.Errors);
    }

    [UnitTheory]
    [InlineData("minimum")]
    [InlineData("pre2000")]
    [InlineData("precision")]
    [InlineData("laterEpoch")]
    [InlineData("maximumRevision")]
    public async Task GivenValidReplacement_WhenHandling_ThenForwardTrustedContextHashAndReturnExactFourMetadataFields(string kind)
    {
        var command = Command();
        command.EditedAt = kind == "minimum" ? new(10, TimeSpan.Zero)
            : kind == "pre2000" ? new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(19)
            : DateTimeOffset.UnixEpoch.AddTicks(19);
        if (kind == "laterEpoch") command.Envelope = command.Envelope with { KeyEpoch = 7 };
        if (kind == "maximumRevision") command.ExpectedRevision = 9007199254740990;
        using var cancellation = new CancellationTokenSource();
        CollectionUpdateRequest? received = null;
        CancellationToken token = default;
        var store = new Mock<ICollectionUpdateStore>(MockBehavior.Strict);
        store.Setup(x => x.UpdateAsync(It.IsAny<CollectionUpdateRequest>(), It.IsAny<CancellationToken>()))
            .Returns<CollectionUpdateRequest, CancellationToken>((request, ct) =>
            {
                received = request;
                token = ct;
                return Task.FromResult(new VaultResult<CollectionCreateDetails>(
                    new(target, kind == "maximumRevision" ? 9007199254740991 : 2,
                        9007199254740991, Normalize(command.EditedAt))));
            });

        var result = await Handler(store.Object).HandleAsync(command, cancellation.Token);

        Assert.True(result.Success);
        Assert.Contains("collection_updated", result.Messages);
        Assert.Equal(target, result.Data!.CollectionId);
        Assert.Equal(kind == "maximumRevision" ? 9007199254740991 : 2, result.Data.Revision);
        Assert.Equal(9007199254740991, result.Data.ServerSequence);
        Assert.Equal(Normalize(command.EditedAt), result.Data.EditedAt);
        Assert.Equal(actor, received!.Actor);
        Assert.Equal(target, received.CollectionId);
        Assert.Equal(command.ExpectedRevision, received.Input.ExpectedRevision);
        Assert.Equal(command.Envelope, received.Input.Envelope);
        Assert.Equal(command.EditedAt, received.Input.EditedAt);
        Assert.Equal(cancellation.Token, token);
        Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", received.AccessVerifier);
        using var data = JsonDocument.Parse(JsonSerializer.Serialize(result.Data, Json));
        Assert.Equal(new[] { "collectionId", "editedAt", "revision", "serverSequence" },
            data.RootElement.EnumerateObject().Select(x => x.Name).Order());
        using var wire = JsonDocument.Parse(JsonSerializer.Serialize(command, Json));
        Assert.Equal(new[] { "editedAt", "envelope", "expectedRevision" },
            wire.RootElement.EnumerateObject().Select(x => x.Name).Order());
    }

    [UnitTheory]
    [InlineData("validation_failed", "validation_failed", 400)]
    [InlineData("not_found", "not_found", 404)]
    [InlineData("vault_access_denied", "vault_access_denied", 403)]
    [InlineData("revision_conflict", "revision_conflict", 409)]
    [InlineData("persistence_unavailable", "persistence_unavailable", 503)]
    [InlineData("secret_owner_123", "persistence_unavailable", 503)]
    [InlineData("authentication_required", "persistence_unavailable", 503)]
    [InlineData("vault_access_required", "persistence_unavailable", 503)]
    [InlineData("identity_unavailable", "persistence_unavailable", 503)]
    [InlineData("collection_updated", "persistence_unavailable", 503)]
    [InlineData("", "persistence_unavailable", 503)]
    public async Task GivenStoreError_WhenHandling_ThenAllowlistOnlyExpectedPersistenceFailures(string error, string expected, int status)
    {
        var store = new Mock<ICollectionUpdateStore>();
        store.Setup(x => x.UpdateAsync(It.IsAny<CollectionUpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VaultResult<CollectionCreateDetails>(Error: error));

        var result = await Handler(store.Object).HandleAsync(Command());

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { expected }, result.Errors);
        Assert.Equal(status, CollectionUpdateMessages.StatusCodes[expected]);
    }

    [UnitTheory]
    [InlineData("not_found")]
    [InlineData("validation_failed")]
    [InlineData("vault_access_denied")]
    [InlineData("revision_conflict")]
    [InlineData("persistence_unavailable")]
    [InlineData("secret_owner_123")]
    public async Task GivenStoreErrorAndData_WhenHandling_ThenBrokenContract503WithoutPayload(string error)
    {
        var command = Command();
        var store = new Mock<ICollectionUpdateStore>();
        store.Setup(x => x.UpdateAsync(It.IsAny<CollectionUpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VaultResult<CollectionCreateDetails>(new(target, 2, 7, Normalize(command.EditedAt)), error));

        var result = await Handler(store.Object).HandleAsync(command);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Contains("persistence_unavailable", result.Errors);
    }

    [UnitTheory]
    [InlineData("nullResult")]
    [InlineData("nullData")]
    [InlineData("id")]
    [InlineData("revision0")]
    [InlineData("revisionOld")]
    [InlineData("revisionAhead")]
    [InlineData("unsafeRevision")]
    [InlineData("exhaustedRevision")]
    [InlineData("sequence0")]
    [InlineData("negativeSequence")]
    [InlineData("unsafeSequence")]
    [InlineData("time")]
    [InlineData("offset")]
    [InlineData("defaultTime")]
    [InlineData("precision")]
    public async Task GivenMalformedStoreSuccess_WhenHandling_Then503WithoutPartialMetadata(string kind)
    {
        var command = Command();
        if (kind == "exhaustedRevision") command.ExpectedRevision = 9007199254740991;
        CollectionCreateDetails? details = new(target, 2, 7, Normalize(command.EditedAt));
        details = kind switch
        {
            "nullData" => null,
            "id" => details with { CollectionId = Guid.NewGuid() },
            "revision0" => details with { Revision = 0 },
            "revisionOld" => details with { Revision = 1 },
            "revisionAhead" => details with { Revision = 3 },
            "unsafeRevision" or "exhaustedRevision" => details with { Revision = 9007199254740992 },
            "sequence0" => details with { ServerSequence = 0 },
            "negativeSequence" => details with { ServerSequence = -1 },
            "unsafeSequence" => details with { ServerSequence = 9007199254740992 },
            "time" => details with { EditedAt = details.EditedAt.AddTicks(10) },
            "offset" => details with { EditedAt = details.EditedAt.ToOffset(TimeSpan.FromHours(1)) },
            "defaultTime" => details with { EditedAt = default },
            "precision" => details with { EditedAt = details.EditedAt.AddTicks(1) },
            _ => details
        };
        var store = new Mock<ICollectionUpdateStore>();
        store.Setup(x => x.UpdateAsync(It.IsAny<CollectionUpdateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(kind == "nullResult" ? null! : new VaultResult<CollectionCreateDetails>(details));

        var result = await Handler(store.Object).HandleAsync(command);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Contains("persistence_unavailable", result.Errors);
    }

    [UnitTheory]
    [InlineData("expectedRevision")]
    [InlineData("envelope")]
    [InlineData("editedAt")]
    public void GivenOmittedRequiredMember_WhenParsing_ThenReject(string name)
    {
        var body = JsonSerializer.SerializeToNode(Command(), Json)!.AsObject();
        body.Remove(name);
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateCollectionCommand>(body.ToJsonString(), Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CollectionUpdateInput>(body.ToJsonString(), Json));
    }

    [UnitTheory]
    [InlineData("actor")]
    [InlineData("vaultAccess")]
    [InlineData("collectionId")]
    [InlineData("ownerId")]
    [InlineData("profileIds")]
    [InlineData("recordIds")]
    [InlineData("folderIds")]
    [InlineData("keyEpoch")]
    [InlineData("grants")]
    [InlineData("keyWrappers")]
    public void GivenForgedContextOrRelationshipMember_WhenParsing_ThenReject(string name)
    {
        var body = JsonSerializer.SerializeToNode(Command(), Json)!.AsObject();
        body[name] = null;
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateCollectionCommand>(body.ToJsonString(), Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CollectionUpdateInput>(body.ToJsonString(), Json));
    }

    [UnitTheory]
    [InlineData("duplicate")]
    [InlineData("case")]
    [InlineData("numericRevision")]
    [InlineData("numericEpoch")]
    public void GivenAlternateJsonShape_WhenParsing_ThenReject(string kind)
    {
        var body = JsonSerializer.Serialize(Command(), Json);
        body = kind switch
        {
            "duplicate" => body.Insert(1, "\"expectedRevision\":1,"),
            "case" => body.Replace("expectedRevision", "ExpectedRevision"),
            "numericRevision" => body.Replace("\"expectedRevision\":1", "\"expectedRevision\":\"1\""),
            _ => body.Replace("\"keyEpoch\":1", "\"keyEpoch\":\"1\"")
        };
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<UpdateCollectionCommand>(body, Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CollectionUpdateInput>(body, Json));
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenHandling_ThenPropagateBeforePersistence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(new Mock<ICollectionUpdateStore>(MockBehavior.Strict).Object).HandleAsync(Command(), cancellation.Token));
    }

    [UnitFact]
    public async Task GivenStoreCancellation_WhenHandling_ThenPropagateExactCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Mock<ICollectionUpdateStore>();
        store.Setup(x => x.UpdateAsync(It.IsAny<CollectionUpdateRequest>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(store.Object).HandleAsync(Command(), cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [UnitTheory]
    [InlineData("collection_updated", 200)]
    [InlineData("authentication_required", 401)]
    [InlineData("vault_access_required", 401)]
    [InlineData("identity_unavailable", 503)]
    public void GivenBoundaryMessage_WhenMapping_ThenUseExpectedStatus(string message, int status) =>
        Assert.Equal(status, CollectionUpdateMessages.StatusCodes[message]);

    private UpdateCollectionCommand Command()
    {
        var command = new UpdateCollectionCommand
        {
            ExpectedRevision = 1,
            Envelope = new("cerberus-content-v1", 1, Access, "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA"),
            EditedAt = DateTimeOffset.UnixEpoch.AddTicks(19)
        };
        command.SetContext(actor, Access, target);
        return command;
    }

    private static DateTimeOffset Normalize(DateTimeOffset time) => time.AddTicks(-(time.Ticks % 10));
    private static UpdateCollectionHandler Handler(ICollectionUpdateStore store) => new(new UpdateCollectionValidator(), store);
}
