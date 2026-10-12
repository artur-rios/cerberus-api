using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Cerberus.WebApi;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class GrantCollectionAccessHandlerTests
{
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Guid actor = Guid.NewGuid();
    private readonly Guid collection = Guid.NewGuid();
    private readonly Guid recipient = Guid.NewGuid();
    private readonly Guid grant = Guid.NewGuid();
    private static readonly Guid First = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Last = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
    private static readonly JsonSerializerOptions Json = new(ProtectionFixture.Json)
    {
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new CanonicalGuidConverter() }
    };

    // Every branch prevents invalid caller material from reaching the grant writer.
    [UnitTheory]
    [InlineData("actor", "authentication_required")]
    [InlineData("missingAccess", "vault_access_required")]
    [InlineData("emptyAccess", "validation_failed")]
    [InlineData("invalidAccess", "validation_failed")]
    [InlineData("collection", "validation_failed")]
    [InlineData("grant", "validation_failed")]
    [InlineData("recipient", "validation_failed")]
    [InlineData("zeroRevision", "validation_failed")]
    [InlineData("negativeRevision", "validation_failed")]
    [InlineData("exhaustedRevision", "validation_failed")]
    [InlineData("unsafeRevision", "validation_failed")]
    [InlineData("nullAccess", "validation_failed")]
    [InlineData("emptyPermission", "validation_failed")]
    [InlineData("casePermission", "validation_failed")]
    [InlineData("spacedPermission", "validation_failed")]
    [InlineData("unknownPermission", "validation_failed")]
    [InlineData("nullEnvelope", "validation_failed")]
    [InlineData("format", "validation_failed")]
    [InlineData("zeroEpoch", "validation_failed")]
    [InlineData("unsafeEpoch", "validation_failed")]
    [InlineData("zeroEnvelopeGrant", "validation_failed")]
    [InlineData("differentGrant", "validation_failed")]
    [InlineData("zeroEnvelopeRevision", "validation_failed")]
    [InlineData("unsafeEnvelopeRevision", "validation_failed")]
    [InlineData("initialRevision", "validation_failed")]
    [InlineData("regrantRevision", "validation_failed")]
    [InlineData("identity", "validation_failed")]
    [InlineData("recipientPin", "validation_failed")]
    [InlineData("authorPin", "validation_failed")]
    [InlineData("enc", "validation_failed")]
    [InlineData("encPrefix", "validation_failed")]
    [InlineData("encPoint", "validation_failed")]
    [InlineData("ciphertext", "validation_failed")]
    [InlineData("signature", "validation_failed")]
    public async Task GivenInvalidContextOrMaterial_WhenHandling_ThenRejectBeforePersistence(string kind, string error)
    {
        var command = Command();
        command.SetContext(kind == "actor" ? Guid.Empty : actor,
            kind == "missingAccess" ? null : kind == "emptyAccess" ? "" : kind == "invalidAccess" ? "bad" : Handle,
            kind == "collection" ? Guid.Empty : collection);
        switch (kind)
        {
            case "grant": command.GrantId = Guid.Empty; break;
            case "recipient": command.RecipientAccountId = Guid.Empty; break;
            case "zeroRevision": command.ExpectedRevision = 0; break;
            case "negativeRevision": command.ExpectedRevision = -1; break;
            case "exhaustedRevision": command.ExpectedRevision = 9007199254740991; break;
            case "unsafeRevision": command.ExpectedRevision = 9007199254740992; break;
            case "nullAccess": command.Access = null!; break;
            case "emptyPermission": command.Access = ""; break;
            case "casePermission": command.Access = "Read-Only"; break;
            case "spacedPermission": command.Access = "read-only "; break;
            case "unknownPermission": command.Access = "owner"; break;
            case "nullEnvelope": command.KeyEnvelope = null!; break;
            case "format": command.KeyEnvelope = command.KeyEnvelope with { Format = "other" }; break;
            case "zeroEpoch": command.KeyEnvelope = command.KeyEnvelope with { KeyEpoch = 0 }; break;
            case "unsafeEpoch": command.KeyEnvelope = command.KeyEnvelope with { KeyEpoch = 9007199254740992 }; break;
            case "zeroEnvelopeGrant": command.KeyEnvelope = command.KeyEnvelope with { GrantId = Guid.Empty }; break;
            case "differentGrant": command.KeyEnvelope = command.KeyEnvelope with { GrantId = Guid.NewGuid() }; break;
            case "zeroEnvelopeRevision": command.KeyEnvelope = command.KeyEnvelope with { GrantRevision = 0 }; break;
            case "unsafeEnvelopeRevision": command.KeyEnvelope = command.KeyEnvelope with { GrantRevision = 9007199254740992 }; break;
            case "initialRevision": command.KeyEnvelope = command.KeyEnvelope with { GrantRevision = 2 }; break;
            case "regrantRevision": command.ExpectedRevision = 1; break;
            case "identity": command.KeyEnvelope = command.KeyEnvelope with { RecipientIdentityId = Guid.Empty }; break;
            case "recipientPin": command.KeyEnvelope = command.KeyEnvelope with { RecipientKeyFingerprint = "bad" }; break;
            case "authorPin": command.KeyEnvelope = command.KeyEnvelope with { AuthorKeyFingerprint = "bad" }; break;
            case "enc": command.KeyEnvelope = command.KeyEnvelope with { Enc = "bad" }; break;
            case "encPrefix": command.KeyEnvelope = command.KeyEnvelope with { Enc = ProtectionFixture.Encode(new byte[65]) }; break;
            case "encPoint": command.KeyEnvelope = command.KeyEnvelope with { Enc = ProtectionFixture.Encode([4, .. new byte[64]]) }; break;
            case "ciphertext": command.KeyEnvelope = command.KeyEnvelope with { Ciphertext = "AQID=" }; break;
            case "signature": command.KeyEnvelope = command.KeyEnvelope with { Signature = "bad" }; break;
        }

        var result = await Handler(new Mock<ICollectionGrantStore>(MockBehavior.Strict).Object).HandleAsync(command);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { error }, result.Errors);
        if (kind is not ("actor" or "missingAccess" or "emptyAccess" or "invalidAccess" or "collection"))
            Assert.False(command.ToInput().IsValid());
    }

    [UnitTheory]
    [InlineData(true, "authentication_required")]
    [InlineData(false, "vault_access_required")]
    public async Task GivenMissingAuthorityAndMalformedInput_WhenHandling_ThenAuthorityTakesPrecedence(bool missingActor, string error)
    {
        var command = Command();
        command.KeyEnvelope = null!;
        command.SetContext(missingActor ? Guid.Empty : actor, null, Guid.Empty);
        var result = await Handler(new Mock<ICollectionGrantStore>(MockBehavior.Strict).Object).HandleAsync(command);
        Assert.Equal(new[] { error }, result.Errors);
    }

    // Wrong context/hashing, revision advancement, access or output leakage breaks this boundary.
    [UnitTheory]
    [InlineData(null, 1, "read-only", false)]
    [InlineData(null, 1, "read-write", true)]
    [InlineData(1L, 2, "read-only", true)]
    [InlineData(8L, 9, "read-write", false)]
    [InlineData(9007199254740990, 9007199254740991, "read-write", false)]
    public async Task GivenValidGrant_WhenHandling_ThenForwardTrustedContextAndReturnExactSortedSixFields(
        long? expected, long revision, string access, bool empty)
    {
        var command = Command(expected, access);
        var details = new CollectionGrantDetails(collection, grant, revision, access,
            empty ? [] : [Last, First], empty ? [] : [Last, First]);
        using var cancellation = new CancellationTokenSource();
        CollectionGrantRequest? received = null;
        CancellationToken receivedToken = default;
        var store = new Mock<ICollectionGrantStore>(MockBehavior.Strict);
        store.Setup(x => x.GrantAsync(It.IsAny<CollectionGrantRequest>(), It.IsAny<CancellationToken>()))
            .Returns<CollectionGrantRequest, CancellationToken>((request, token) =>
            {
                received = request;
                receivedToken = token;
                return Task.FromResult(new VaultResult<CollectionGrantDetails>(details));
            });

        var result = await Handler(store.Object).HandleAsync(command, cancellation.Token);

        Assert.True(result.Success);
        Assert.Equal(new[] { "collection_access_granted" }, result.Messages);
        Assert.Equal(collection, result.Data!.CollectionId);
        Assert.Equal(grant, result.Data.GrantId);
        Assert.Equal(revision, result.Data.Revision);
        Assert.Equal(access, result.Data.Access);
        Assert.Equal(empty ? [] : new[] { First, Last }, result.Data.RecordIds);
        Assert.Equal(empty ? [] : new[] { First, Last }, result.Data.FolderIds);
        Assert.Equal(actor, received!.Actor);
        Assert.Equal(collection, received.CollectionId);
        Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", received.AccessVerifier);
        Assert.Equal(grant, received.Input.GrantId);
        Assert.Equal(recipient, received.Input.RecipientAccountId);
        Assert.Equal(expected, received.Input.ExpectedRevision);
        Assert.Equal(access, received.Input.Access);
        Assert.Equal(command.KeyEnvelope, received.Input.KeyEnvelope);
        Assert.True(received.Input.IsValid());
        Assert.Equal(cancellation.Token, receivedToken);
        Assert.Equal(empty ? [] : new[] { Last, First }, details.RecordIds);
        Assert.Equal(empty ? [] : new[] { Last, First }, details.FolderIds);
        AssertFields(result.Data, ["access", "collectionId", "folderIds", "grantId", "recordIds", "revision"]);
        AssertFields(command, ["access", "expectedRevision", "grantId", "keyEnvelope", "recipientAccountId"]);
        AssertFields(received.Input, ["access", "expectedRevision", "grantId", "keyEnvelope", "recipientAccountId"]);
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
    [InlineData("collection_access_granted", "persistence_unavailable", 503)]
    [InlineData("", "persistence_unavailable", 503)]
    public async Task GivenStoreError_WhenHandling_ThenAllowlistOnlySafeFailures(string error, string expected, int status)
    {
        var result = await Handler(Store(new(Error: error)).Object).HandleAsync(Command());
        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { expected }, result.Errors);
        Assert.Equal(status, CollectionGrantMessages.StatusCodes[expected]);
    }

    [UnitTheory]
    [InlineData("not_found")]
    [InlineData("validation_failed")]
    [InlineData("vault_access_denied")]
    [InlineData("revision_conflict")]
    [InlineData("persistence_unavailable")]
    [InlineData("secret_recipient_123")]
    public async Task GivenStoreErrorWithData_WhenHandling_ThenFailClosed(string error)
    {
        var result = await Handler(Store(new(Details(), error)).Object).HandleAsync(Command());
        Assert.Equal(new[] { "persistence_unavailable" }, result.Errors);
        Assert.Null(result.Data);
    }

    // Fail closed on corrupt metadata and member identities, rather than silently repairing store evidence.
    [UnitTheory]
    [InlineData("nullResult")]
    [InlineData("nullData")]
    [InlineData("collection")]
    [InlineData("grant")]
    [InlineData("zeroRevision")]
    [InlineData("negativeRevision")]
    [InlineData("wrongRevision")]
    [InlineData("unsafeRevision")]
    [InlineData("wrongAccess")]
    [InlineData("unknownAccess")]
    [InlineData("nullAccess")]
    [InlineData("nullRecords")]
    [InlineData("nullFolders")]
    [InlineData("zeroRecord")]
    [InlineData("zeroFolder")]
    [InlineData("duplicateRecord")]
    [InlineData("duplicateFolder")]
    public async Task GivenMalformedStoreSuccess_WhenHandling_Then503WithoutPayload(string kind)
    {
        CollectionGrantDetails? details = Details();
        details = kind switch
        {
            "nullData" => null,
            "collection" => details with { CollectionId = Guid.NewGuid() },
            "grant" => details with { GrantId = Guid.NewGuid() },
            "zeroRevision" => details with { Revision = 0 },
            "negativeRevision" => details with { Revision = -1 },
            "wrongRevision" => details with { Revision = 2 },
            "unsafeRevision" => details with { Revision = 9007199254740992 },
            "wrongAccess" => details with { Access = "read-write" },
            "unknownAccess" => details with { Access = "owner" },
            "nullAccess" => details with { Access = null! },
            "nullRecords" => details with { RecordIds = null! },
            "nullFolders" => details with { FolderIds = null! },
            "zeroRecord" => details with { RecordIds = [Guid.Empty] },
            "zeroFolder" => details with { FolderIds = [Guid.Empty] },
            "duplicateRecord" => details with { RecordIds = [First, First] },
            "duplicateFolder" => details with { FolderIds = [First, First] },
            _ => details
        };
        var result = await Handler(Store(kind == "nullResult" ? null! : new(details)).Object).HandleAsync(Command());
        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { "persistence_unavailable" }, result.Errors);
    }

    [UnitTheory]
    [InlineData("grantId")]
    [InlineData("recipientAccountId")]
    [InlineData("expectedRevision")]
    [InlineData("access")]
    [InlineData("keyEnvelope")]
    public void GivenOmittedRequiredInput_WhenParsing_ThenReject(string name)
    {
        var body = JsonSerializer.SerializeToNode(Command(), Json)!.AsObject();
        body.Remove(name);
        AssertRejectedJson(body.ToJsonString());
    }

    [UnitFact]
    public void GivenExplicitNullRevision_WhenParsing_ThenAcceptInitialGrant()
    {
        var body = JsonSerializer.Serialize(Command(), Json);
        Assert.Contains("\"expectedRevision\":null", body);
        var command = JsonSerializer.Deserialize<GrantCollectionAccessCommand>(body, Json)!;
        var input = JsonSerializer.Deserialize<CollectionGrantInput>(body, Json)!;
        Assert.Null(command.ExpectedRevision);
        Assert.Null(input.ExpectedRevision);
        Assert.True(command.ToInput().IsValid());
        Assert.True(input.IsValid());
    }

    [UnitTheory]
    [InlineData("actor")]
    [InlineData("vaultAccess")]
    [InlineData("accessVerifier")]
    [InlineData("collectionId")]
    [InlineData("ownerId")]
    [InlineData("profileIds")]
    [InlineData("recipientIdentityId")]
    [InlineData("recordIds")]
    [InlineData("folderIds")]
    [InlineData("plaintext")]
    public void GivenForgedContextOrUnknownInput_WhenParsing_ThenReject(string name)
    {
        var body = JsonSerializer.SerializeToNode(Command(), Json)!.AsObject();
        body[name] = null;
        AssertRejectedJson(body.ToJsonString());
    }

    [UnitTheory]
    [InlineData("duplicateRevision")]
    [InlineData("caseRevision")]
    [InlineData("stringRevision")]
    [InlineData("fractionRevision")]
    [InlineData("uppercaseGrant")]
    [InlineData("compactRecipient")]
    [InlineData("zeroGrant")]
    [InlineData("numericRecipient")]
    [InlineData("arrayEnvelope")]
    [InlineData("unknownEnvelope")]
    [InlineData("duplicateEpoch")]
    [InlineData("caseEpoch")]
    [InlineData("stringEpoch")]
    public void GivenAlternateJsonShape_WhenParsing_ThenReject(string kind)
    {
        var body = JsonSerializer.SerializeToNode(Command(), Json)!.AsObject();
        switch (kind)
        {
            case "caseRevision": body.Remove("expectedRevision"); body["ExpectedRevision"] = null; break;
            case "stringRevision": body["expectedRevision"] = "1"; break;
            case "fractionRevision": body["expectedRevision"] = 1.5; break;
            case "uppercaseGrant": body["grantId"] = Last.ToString().ToUpperInvariant(); break;
            case "compactRecipient": body["recipientAccountId"] = recipient.ToString("N"); break;
            case "zeroGrant": body["grantId"] = Guid.Empty.ToString(); break;
            case "numericRecipient": body["recipientAccountId"] = 7; break;
            case "arrayEnvelope": body["keyEnvelope"] = new System.Text.Json.Nodes.JsonArray(); break;
            case "unknownEnvelope": body["keyEnvelope"]!["ownerId"] = actor.ToString(); break;
            case "caseEpoch": body["keyEnvelope"]!.AsObject().Remove("keyEpoch"); body["keyEnvelope"]!["KeyEpoch"] = 1; break;
            case "stringEpoch": body["keyEnvelope"]!["keyEpoch"] = "1"; break;
        }
        var json = body.ToJsonString();
        if (kind == "duplicateRevision") json = json.Insert(1, "\"expectedRevision\":null,");
        if (kind == "duplicateEpoch") json = json.Replace("\"keyEpoch\":1", "\"keyEpoch\":1,\"keyEpoch\":1");
        AssertRejectedJson(json);
    }

    [UnitTheory]
    [InlineData("format")]
    [InlineData("keyEpoch")]
    [InlineData("grantId")]
    [InlineData("grantRevision")]
    [InlineData("recipientIdentityId")]
    [InlineData("recipientKeyFingerprint")]
    [InlineData("authorKeyFingerprint")]
    [InlineData("enc")]
    [InlineData("ciphertext")]
    [InlineData("signature")]
    public async Task GivenMissingEnvelopeMember_WhenHandlingParsedInput_ThenRejectBeforePersistence(string name)
    {
        var body = JsonSerializer.SerializeToNode(Command(), Json)!.AsObject();
        body["keyEnvelope"]!.AsObject().Remove(name);
        var command = JsonSerializer.Deserialize<GrantCollectionAccessCommand>(body.ToJsonString(), Json)!;
        var input = JsonSerializer.Deserialize<CollectionGrantInput>(body.ToJsonString(), Json)!;
        command.SetContext(actor, Handle, collection);
        Assert.False(input.IsValid());
        var result = await Handler(new Mock<ICollectionGrantStore>(MockBehavior.Strict).Object).HandleAsync(command);
        Assert.Equal(new[] { "validation_failed" }, result.Errors);
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenHandling_ThenPropagateBeforePersistence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(new Mock<ICollectionGrantStore>(MockBehavior.Strict).Object).HandleAsync(Command(), cancellation.Token));
    }

    [UnitFact]
    public async Task GivenStoreCancellation_WhenHandling_ThenPropagateExactCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Mock<ICollectionGrantStore>(MockBehavior.Strict);
        store.Setup(x => x.GrantAsync(It.IsAny<CollectionGrantRequest>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(store.Object).HandleAsync(Command(), cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [UnitTheory]
    [InlineData("collection_access_granted", 201)]
    [InlineData("authentication_required", 401)]
    [InlineData("vault_access_required", 401)]
    [InlineData("identity_unavailable", 503)]
    public void GivenBoundaryMessage_WhenMapping_ThenUseExpectedStatus(string message, int status) =>
        Assert.Equal(status, CollectionGrantMessages.StatusCodes[message]);

    private GrantCollectionAccessCommand Command(long? expected = null, string access = "read-only")
    {
        using var fixture = new ProtectionFixture();
        var command = new GrantCollectionAccessCommand
        {
            GrantId = grant,
            RecipientAccountId = recipient,
            ExpectedRevision = expected,
            Access = access,
            KeyEnvelope = fixture.WrapGrant(actor, collection, grant, Guid.NewGuid(), fixture.Material.RecipientKey,
                revision: expected is null ? 1 : expected.Value + 1)
        };
        command.SetContext(actor, Handle, collection);
        return command;
    }

    private CollectionGrantDetails Details() => new(collection, grant, 1, "read-only", [Last, First], [First]);
    private static GrantCollectionAccessHandler Handler(ICollectionGrantStore store) => new(new GrantCollectionAccessValidator(), store);
    private static Mock<ICollectionGrantStore> Store(VaultResult<CollectionGrantDetails> result)
    {
        var store = new Mock<ICollectionGrantStore>();
        store.Setup(x => x.GrantAsync(It.IsAny<CollectionGrantRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return store;
    }

    private static void AssertFields<T>(T value, string[] fields)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value, Json));
        Assert.Equal(fields, json.RootElement.EnumerateObject().Select(x => x.Name).Order());
    }

    private static void AssertRejectedJson(string body)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GrantCollectionAccessCommand>(body, Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CollectionGrantInput>(body, Json));
    }
}
