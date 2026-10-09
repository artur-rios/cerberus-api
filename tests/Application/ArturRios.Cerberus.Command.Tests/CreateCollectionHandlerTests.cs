using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Cerberus.WebApi;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class CreateCollectionHandlerTests
{
    private const string Access = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Guid actor = Guid.NewGuid();
    private static readonly JsonSerializerOptions Json = Options();

    [UnitTheory]
    [InlineData("actor", "authentication_required")]
    [InlineData("missing", "vault_access_required")]
    [InlineData("empty", "validation_failed")]
    [InlineData("bad", "validation_failed")]
    [InlineData("id", "validation_failed")]
    [InlineData("envelope", "validation_failed")]
    [InlineData("epoch", "validation_failed")]
    [InlineData("defaultTime", "validation_failed")]
    [InlineData("submicro", "validation_failed")]
    [InlineData("offset", "validation_failed")]
    [InlineData("nullProfile", "validation_failed")]
    [InlineData("nullRecord", "validation_failed")]
    [InlineData("nullFolder", "validation_failed")]
    [InlineData("duplicateProfile", "validation_failed")]
    [InlineData("duplicateRecord", "validation_failed")]
    [InlineData("duplicateFolder", "validation_failed")]
    [InlineData("zeroProfile", "validation_failed")]
    [InlineData("zeroRecord", "validation_failed")]
    [InlineData("zeroFolder", "validation_failed")]
    public async Task GivenInvalidContextEnvelopeTimeOrTypedLinks_WhenCreating_ThenRejectBeforePersistence(string kind, string error)
    {
        // Removing any validation must reach the strict store and fail this test.
        var command = Command();
        command.SetContext(kind == "actor" ? Guid.Empty : actor,
            kind == "missing" ? null : kind == "empty" ? "" : kind == "bad" ? "bad" : Access);
        switch (kind)
        {
            case "id": command.CollectionId = Guid.Empty; break;
            case "envelope": command.Envelope = null!; break;
            case "epoch": command.Envelope = command.Envelope with { KeyEpoch = 2 }; break;
            case "defaultTime": command.EditedAt = default; break;
            case "submicro": command.EditedAt = new(9, TimeSpan.Zero); break;
            case "offset": command.EditedAt = command.EditedAt.ToOffset(TimeSpan.FromHours(1)); break;
            case "nullProfile": command.ProfileIds = null!; break;
            case "nullRecord": command.RecordIds = null!; break;
            case "nullFolder": command.FolderIds = null!; break;
            case "duplicateProfile": command.ProfileIds = [actor, actor]; break;
            case "duplicateRecord": command.RecordIds = [actor, actor]; break;
            case "duplicateFolder": command.FolderIds = [actor, actor]; break;
            case "zeroProfile": command.ProfileIds = [Guid.Empty]; break;
            case "zeroRecord": command.RecordIds = [Guid.Empty]; break;
            case "zeroFolder": command.FolderIds = [Guid.Empty]; break;
        }
        var result = await Handler(new Mock<ICollectionCreateStore>(MockBehavior.Strict).Object).HandleAsync(command);
        Assert.False(result.Success); Assert.Null(result.Data); Assert.Contains(error, result.Errors);
    }

    [UnitTheory]
    [InlineData("minimum", 10L)]
    [InlineData("firstSubmicro", 10L)]
    [InlineData("precision", 621355968000000010L)]
    [InlineData("pre2000", 630822815990000010L)]
    [InlineData("pre2000Last", 630822815999999990L)]
    [InlineData("maximum", 3155378975999999990L)]
    public async Task GivenValidCollection_WhenCreating_ThenReturnExactMetadataAndForwardTrustedContext(string kind, long expectedTicks)
    {
        var command = Command();
        command.EditedAt = kind switch
        {
            "minimum" => new(10, TimeSpan.Zero), "firstSubmicro" => new(19, TimeSpan.Zero),
            "precision" => DateTimeOffset.UnixEpoch.AddTicks(19),
            "pre2000" => new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(19),
            "pre2000Last" => new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(-1),
            _ => DateTimeOffset.MaxValue
        };
        using var source = new CancellationTokenSource();
        CollectionCreateRequest? captured = null; CancellationToken received = default;
        var store = new Mock<ICollectionCreateStore>(MockBehavior.Strict);
        store.Setup(x => x.CreateAsync(It.IsAny<CollectionCreateRequest>(), It.IsAny<CancellationToken>()))
            .Returns<CollectionCreateRequest, CancellationToken>((request, token) =>
            {
                captured = request; received = token;
                return Task.FromResult(new VaultResult<CollectionCreateDetails>(new(command.CollectionId, 1, 7, new(expectedTicks, TimeSpan.Zero))));
            });
        var result = await Handler(store.Object).HandleAsync(command, source.Token);
        Assert.True(result.Success); Assert.Contains("collection_created", result.Messages);
        Assert.Equal(command.CollectionId, result.Data!.CollectionId); Assert.Equal(1, result.Data.Revision);
        Assert.Equal(7, result.Data.ServerSequence); Assert.Equal(expectedTicks, result.Data.EditedAt.Ticks);
        Assert.Equal(TimeSpan.Zero, result.Data.EditedAt.Offset);
        Assert.Equal(actor, captured!.Actor);
        Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", captured.AccessVerifier);
        Assert.Equal(command.ToInput(), captured.Input); Assert.Equal(source.Token, received);
        using var output = JsonDocument.Parse(JsonSerializer.Serialize(result.Data, Json));
        Assert.Equal(new[] { "collectionId", "editedAt", "revision", "serverSequence" }, output.RootElement.EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal(201, CreateCollectionMessages.StatusCodes[result.Messages.Single()]);
    }

    [UnitTheory][InlineData("empty")][InlineData("multipleProfiles")][InlineData("typedAlias")][InlineData("mixed")]
    public async Task GivenValidTypedLinks_WhenCreating_ThenPreserveEverySubmittedKind(string kind)
    {
        var command = Command(); var id = Guid.NewGuid();
        if (kind == "multipleProfiles") command.ProfileIds = [id, actor];
        if (kind == "typedAlias") { command.CollectionId = id; command.ProfileIds = [id]; command.RecordIds = [id]; command.FolderIds = [id]; }
        if (kind == "mixed") { command.ProfileIds = [actor]; command.RecordIds = [id, Guid.NewGuid()]; command.FolderIds = [Guid.NewGuid()]; }
        CollectionCreateRequest? captured = null;
        var store = new Mock<ICollectionCreateStore>();
        store.Setup(x => x.CreateAsync(It.IsAny<CollectionCreateRequest>(), It.IsAny<CancellationToken>()))
            .Returns<CollectionCreateRequest, CancellationToken>((r, _) => { captured = r; return Task.FromResult(new VaultResult<CollectionCreateDetails>(new(command.CollectionId, 1, 7, DateTimeOffset.UnixEpoch.AddTicks(10)))); });
        var result = await Handler(store.Object).HandleAsync(command); Assert.True(result.Success);
        Assert.Equal(command.ProfileIds, captured!.Input.ProfileIds); Assert.Equal(command.RecordIds, captured.Input.RecordIds); Assert.Equal(command.FolderIds, captured.Input.FolderIds);
    }

    [UnitTheory]
    [InlineData("validation_failed", 400)][InlineData("authentication_required", 401)][InlineData("vault_access_required", 401)]
    [InlineData("vault_access_denied", 403)][InlineData("not_found", 404)][InlineData("revision_conflict", 409)]
    [InlineData("persistence_unavailable", 503)][InlineData("identity_unavailable", 503)]
    [InlineData("secret_internal_error", 503)][InlineData("collection_created", 503)][InlineData("", 503)]
    public async Task GivenStoreErrorWithData_WhenCreating_ThenAllowlistErrorAndDiscardData(string error, int status)
    {
        var command = Command(); var store = new Mock<ICollectionCreateStore>();
        store.Setup(x => x.CreateAsync(It.IsAny<CollectionCreateRequest>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VaultResult<CollectionCreateDetails>(new(command.CollectionId, 1, 7, DateTimeOffset.UnixEpoch.AddTicks(10)), error));
        var result = await Handler(store.Object).HandleAsync(command);
        var safe = error is "secret_internal_error" or "collection_created" or "" ? "persistence_unavailable" : error;
        Assert.False(result.Success); Assert.Null(result.Data); Assert.Contains(safe, result.Errors); Assert.Equal(status, CreateCollectionMessages.StatusCodes[safe]);
    }

    [UnitTheory]
    [InlineData("nullResult")][InlineData("nullData")][InlineData("id")][InlineData("revision0")][InlineData("revision2")]
    [InlineData("sequence0")][InlineData("unsafe")][InlineData("time")][InlineData("offset")][InlineData("defaultTime")][InlineData("unnormalized")]
    public async Task GivenMalformedStoreDetails_WhenCreating_ThenFailClosed(string kind)
    {
        var command = Command(); var data = new CollectionCreateDetails(command.CollectionId, 1, 7, DateTimeOffset.UnixEpoch.AddTicks(10));
        data = kind switch
        {
            "nullData" => null!, "id" => data with { CollectionId = Guid.NewGuid() }, "revision0" => data with { Revision = 0 },
            "revision2" => data with { Revision = 2 }, "sequence0" => data with { ServerSequence = 0 }, "unsafe" => data with { ServerSequence = 9007199254740992 },
            "time" => data with { EditedAt = data.EditedAt.AddTicks(10) }, "offset" => data with { EditedAt = data.EditedAt.ToOffset(TimeSpan.FromHours(1)) },
            "defaultTime" => data with { EditedAt = default }, "unnormalized" => data with { EditedAt = data.EditedAt.AddTicks(1) }, _ => data
        };
        var store = new Mock<ICollectionCreateStore>();
        store.Setup(x => x.CreateAsync(It.IsAny<CollectionCreateRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(kind == "nullResult" ? null! : new VaultResult<CollectionCreateDetails>(data));
        var result = await Handler(store.Object).HandleAsync(command);
        Assert.False(result.Success); Assert.Null(result.Data); Assert.Contains("persistence_unavailable", result.Errors);
    }

    [UnitTheory]
    [InlineData("collectionId")][InlineData("envelope")][InlineData("editedAt")][InlineData("profileIds")][InlineData("recordIds")][InlineData("folderIds")]
    public void GivenOmittedRequiredField_WhenParsing_ThenReject(string name)
    { var body = Body(); body.Remove(name); Assert.Throws<JsonException>(() => Parse(body.ToJsonString())); }

    [UnitTheory]
    [InlineData("actor")][InlineData("accountId")][InlineData("vaultAccess")][InlineData("name")][InlineData("fields")][InlineData("keyWrappers")]
    [InlineData("expectedRevision")][InlineData("grants")][InlineData("proof")][InlineData("sourceKind")][InlineData("unknown")]
    public void GivenForgedOrUnknownField_WhenParsing_ThenReject(string name)
    { var body = Body(); body[name] = null; Assert.Throws<JsonException>(() => Parse(body.ToJsonString())); }

    [UnitTheory]
    [InlineData("collectionId", "upper")][InlineData("collectionId", "compact")][InlineData("collectionId", "zero")][InlineData("collectionId", "numeric")]
    [InlineData("profileIds", "upper")][InlineData("profileIds", "compact")][InlineData("profileIds", "zero")][InlineData("profileIds", "numeric")]
    [InlineData("recordIds", "upper")][InlineData("recordIds", "compact")][InlineData("recordIds", "zero")][InlineData("recordIds", "numeric")]
    [InlineData("folderIds", "upper")][InlineData("folderIds", "compact")][InlineData("folderIds", "zero")][InlineData("folderIds", "numeric")]
    public void GivenNoncanonicalTypedId_WhenParsing_ThenReject(string name, string kind)
    {
        var body = Body(); var id = kind switch { "upper" => "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA", "compact" => "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa", "zero" => "00000000-0000-0000-0000-000000000000", _ => null };
        JsonNode value = kind == "numeric" ? JsonValue.Create(1)! : JsonValue.Create(id)!;
        body[name] = name == "collectionId" ? value : new JsonArray(value);
        Assert.Throws<JsonException>(() => Parse(body.ToJsonString()));
    }

    [UnitTheory][InlineData("profileIds")][InlineData("recordIds")][InlineData("folderIds")][InlineData("envelope")]
    public void GivenNullRequiredValue_WhenValidating_ThenReject(string name)
    { var body = Body(); body[name] = null; Assert.False(new CreateCollectionValidator().Validate(Parse(body.ToJsonString())).IsValid); }

    [UnitTheory][InlineData("duplicate")][InlineData("case")][InlineData("numeric")][InlineData("nestedUnknown")][InlineData("nestedMissing")][InlineData("nestedCase")][InlineData("nestedDuplicate")]
    public void GivenAmbiguousBodyOrEnvelope_WhenParsing_ThenReject(string kind)
    {
        var body = JsonSerializer.Serialize(Command(), Json);
        body = kind switch
        {
            "duplicate" => body.Insert(1, "\"profileIds\":[],"), "case" => body.Replace("profileIds", "ProfileIds"),
            "numeric" => body.Replace("\"keyEpoch\":1", "\"keyEpoch\":\"1\""),
            "nestedUnknown" => body.Replace("\"envelope\":{", "\"envelope\":{\"name\":null,"),
            "nestedMissing" => body.Replace("\"keyEpoch\":1,", ""), "nestedCase" => body.Replace("keyEpoch", "KeyEpoch"),
            _ => body.Replace("\"envelope\":{", "\"envelope\":{\"keyEpoch\":1,")
        };
        if (kind == "nestedMissing")
            Assert.False(new CreateCollectionValidator().Validate(Parse(body)).IsValid);
        else
            Assert.Throws<JsonException>(() => Parse(body));
    }

    [UnitFact]
    public void GivenUnmodifiedValidBody_WhenParsing_ThenAcceptExactSixFields()
    {
        var body = Body(); var command = Parse(body.ToJsonString());
        Assert.True(new CreateCollectionValidator().Validate(command).IsValid);
        Assert.Equal(new[] { "collectionId", "editedAt", "envelope", "folderIds", "profileIds", "recordIds" }, body.Select(x => x.Key).Order());
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenCreating_ThenPropagateBeforePersistence()
    { using var source = new CancellationTokenSource(); source.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(new Mock<ICollectionCreateStore>(MockBehavior.Strict).Object).HandleAsync(Command(), source.Token)); }

    [UnitFact]
    public async Task GivenStoreCancellation_WhenCreating_ThenPropagateCallerToken()
    {
        using var source = new CancellationTokenSource(); var store = new Mock<ICollectionCreateStore>();
        store.Setup(x => x.CreateAsync(It.IsAny<CollectionCreateRequest>(), source.Token)).ThrowsAsync(new OperationCanceledException(source.Token));
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(store.Object).HandleAsync(Command(), source.Token)); Assert.Equal(source.Token, error.CancellationToken);
    }

    private CreateCollectionCommand Command()
    {
        var command = new CreateCollectionCommand { CollectionId = Guid.NewGuid(), Envelope = new EncryptedEnvelope("cerberus-content-v1", 1, Access, "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA"), EditedAt = DateTimeOffset.UnixEpoch.AddTicks(19), ProfileIds = [], RecordIds = [], FolderIds = [] };
        command.SetContext(actor, Access); return command;
    }
    private JsonObject Body() => JsonSerializer.SerializeToNode(Command(), Json)!.AsObject();
    private static CreateCollectionCommand Parse(string value) => JsonSerializer.Deserialize<CreateCollectionCommand>(value, Json)!;
    private static CreateCollectionHandler Handler(ICollectionCreateStore store) => new(new CreateCollectionValidator(), store);
    private static JsonSerializerOptions Options()
    { var options = new JsonSerializerOptions(ProtectionFixture.Json) { NumberHandling = JsonNumberHandling.Strict }; options.Converters.Add(new CanonicalGuidConverter()); return options; }
}
