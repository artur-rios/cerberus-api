using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Cerberus.WebApi;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class SetCollectionMembershipHandlerTests
{
    private const string Access = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private readonly Guid actor = Guid.NewGuid();
    private readonly Guid target = Guid.NewGuid();
    private static readonly Guid First = Guid.Parse("11111111-1111-4111-8111-111111111111");
    private static readonly Guid Last = Guid.Parse("eeeeeeee-eeee-4eee-8eee-eeeeeeeeeeee");
    private static readonly JsonSerializerOptions Json = new(ProtectionFixture.Json)
    {
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new CanonicalGuidConverter() }
    };

    // Removing any corresponding validator branch would permit persistence of invalid input.
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
    [InlineData("defaultTime", "validation_failed")]
    [InlineData("time", "validation_failed")]
    [InlineData("offset", "validation_failed")]
    [InlineData("nullRecords", "validation_failed")]
    [InlineData("nullFolders", "validation_failed")]
    [InlineData("zeroRecord", "validation_failed")]
    [InlineData("zeroFolder", "validation_failed")]
    [InlineData("duplicateRecord", "validation_failed")]
    [InlineData("duplicateFolder", "validation_failed")]
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
            case "defaultTime": command.EditedAt = default; break;
            case "time": command.EditedAt = new(9, TimeSpan.Zero); break;
            case "offset": command.EditedAt = command.EditedAt.ToOffset(TimeSpan.FromHours(1)); break;
            case "nullRecords": command.RecordIds = null!; break;
            case "nullFolders": command.FolderIds = null!; break;
            case "zeroRecord": command.RecordIds = [Guid.Empty]; break;
            case "zeroFolder": command.FolderIds = [Guid.Empty]; break;
            case "duplicateRecord": command.RecordIds = [First, First]; break;
            case "duplicateFolder": command.FolderIds = [First, First]; break;
        }

        var result = await Handler(new Mock<ICollectionMembershipStore>(MockBehavior.Strict).Object).HandleAsync(command);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { error }, result.Errors);
        if (kind is not ("actor" or "missing" or "empty" or "handle" or "id"))
            Assert.False(command.ToInput().IsValid());
    }

    // Wrong hashing/context, collapsed typed IDs, unsorted arrays or leaked fields break this boundary.
    [UnitTheory]
    [InlineData("overlap")]
    [InlineData("empty")]
    [InlineData("minimum")]
    [InlineData("pre2000")]
    [InlineData("laterEpoch")]
    [InlineData("maximumRevision")]
    public async Task GivenValidReplacement_WhenHandling_ThenForwardTrustedContextAndReturnExactSortedSixFields(string kind)
    {
        var command = Command();
        if (kind == "empty") { command.RecordIds = []; command.FolderIds = []; }
        if (kind == "minimum") command.EditedAt = new(10, TimeSpan.Zero);
        if (kind == "pre2000") command.EditedAt = new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(19);
        if (kind == "laterEpoch") command.Envelope = command.Envelope with { KeyEpoch = 7 };
        if (kind == "maximumRevision") command.ExpectedRevision = 9007199254740990;
        var normalized = kind == "minimum" ? new DateTimeOffset(10, TimeSpan.Zero)
            : kind == "pre2000" ? new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(10)
            : DateTimeOffset.UnixEpoch.AddTicks(10);
        using var cancellation = new CancellationTokenSource();
        CollectionMembershipRequest? received = null;
        CancellationToken receivedToken = default;
        var details = new CollectionMembershipDetails(target, kind == "maximumRevision" ? 9007199254740991 : 2,
            9007199254740991, normalized, command.RecordIds.Reverse().ToArray(), command.FolderIds);
        var store = new Mock<ICollectionMembershipStore>(MockBehavior.Strict);
        store.Setup(x => x.SetAsync(It.IsAny<CollectionMembershipRequest>(), It.IsAny<CancellationToken>()))
            .Returns<CollectionMembershipRequest, CancellationToken>((request, token) =>
            {
                received = request;
                receivedToken = token;
                return Task.FromResult(new VaultResult<CollectionMembershipDetails>(details));
            });

        var result = await Handler(store.Object).HandleAsync(command, cancellation.Token);

        Assert.True(result.Success);
        Assert.Contains("collection_membership_set", result.Messages);
        Assert.Equal(target, result.Data!.CollectionId);
        Assert.Equal(kind == "maximumRevision" ? 9007199254740991 : 2, result.Data.Revision);
        Assert.Equal(9007199254740991, result.Data.ServerSequence);
        Assert.Equal(normalized, result.Data.EditedAt);
        Assert.Equal(kind == "empty" ? [] : new[] { First, Last }, result.Data.RecordIds);
        Assert.Equal(kind == "empty" ? [] : new[] { First, Last }, result.Data.FolderIds);
        Assert.Equal(actor, received!.Actor);
        Assert.Equal(target, received.CollectionId);
        Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", received.AccessVerifier);
        Assert.Equal(command.ExpectedRevision, received.Input.ExpectedRevision);
        Assert.Equal(command.Envelope, received.Input.Envelope);
        Assert.Equal(command.EditedAt, received.Input.EditedAt);
        Assert.Equal(command.RecordIds, received.Input.RecordIds);
        Assert.Equal(command.FolderIds, received.Input.FolderIds);
        Assert.True(received.Input.IsValid());
        Assert.Equal(cancellation.Token, receivedToken);
        Assert.Equal(kind == "empty" ? [] : new[] { Last, First }, command.RecordIds);
        Assert.Equal(kind == "empty" ? [] : new[] { Last, First }, details.FolderIds);
        using var output = JsonDocument.Parse(JsonSerializer.Serialize(result.Data, Json));
        Assert.Equal(new[] { "collectionId", "editedAt", "folderIds", "recordIds", "revision", "serverSequence" },
            output.RootElement.EnumerateObject().Select(x => x.Name).Order());
        using var body = JsonDocument.Parse(JsonSerializer.Serialize(command, Json));
        Assert.Equal(new[] { "editedAt", "envelope", "expectedRevision", "folderIds", "recordIds" },
            body.RootElement.EnumerateObject().Select(x => x.Name).Order());
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(received.Input, Json));
        Assert.Equal(new[] { "editedAt", "envelope", "expectedRevision", "folderIds", "recordIds" },
            input.RootElement.EnumerateObject().Select(x => x.Name).Order());
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
    [InlineData("collection_membership_set", "persistence_unavailable", 503)]
    [InlineData("", "persistence_unavailable", 503)]
    public async Task GivenStoreError_WhenHandling_ThenAllowlistOnlyExpectedPersistenceFailures(string error, string expected, int status)
    {
        var result = await Handler(Store(new(Error: error)).Object).HandleAsync(Command());

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { expected }, result.Errors);
        Assert.Equal(status, SetCollectionMembershipMessages.StatusCodes[expected]);
    }

    [UnitTheory]
    [InlineData("not_found")]
    [InlineData("validation_failed")]
    [InlineData("vault_access_denied")]
    [InlineData("revision_conflict")]
    [InlineData("persistence_unavailable")]
    [InlineData("secret_owner_123")]
    public async Task GivenStoreErrorAndData_WhenHandling_ThenFailClosedWithoutPayload(string error)
    {
        var command = Command();
        var result = await Handler(Store(new(Details(command), error)).Object).HandleAsync(command);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { "persistence_unavailable" }, result.Errors);
    }

    // Missing guards on metadata or membership equality would publish corrupt store data.
    [UnitTheory]
    [InlineData("nullResult")]
    [InlineData("nullData")]
    [InlineData("id")]
    [InlineData("revision0")]
    [InlineData("negativeRevision")]
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
    [InlineData("nullRecords")]
    [InlineData("nullFolders")]
    [InlineData("zeroRecord")]
    [InlineData("zeroFolder")]
    [InlineData("duplicateRecord")]
    [InlineData("duplicateFolder")]
    [InlineData("extraRecord")]
    [InlineData("extraFolder")]
    [InlineData("missingRecord")]
    [InlineData("missingFolder")]
    [InlineData("wrongRecord")]
    [InlineData("wrongFolder")]
    [InlineData("swappedTypes")]
    public async Task GivenMalformedStoreSuccess_WhenHandling_Then503WithoutPartialMetadata(string kind)
    {
        var command = Command();
        if (kind == "exhaustedRevision") command.ExpectedRevision = 9007199254740991;
        if (kind == "swappedTypes") command.FolderIds = [First];
        CollectionMembershipDetails? details = Details(command);
        details = kind switch
        {
            "nullData" => null,
            "id" => details with { CollectionId = Guid.NewGuid() },
            "revision0" => details with { Revision = 0 },
            "negativeRevision" => details with { Revision = -1 },
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
            "nullRecords" => details with { RecordIds = null! },
            "nullFolders" => details with { FolderIds = null! },
            "zeroRecord" => details with { RecordIds = [Guid.Empty, First] },
            "zeroFolder" => details with { FolderIds = [Guid.Empty, First] },
            "duplicateRecord" => details with { RecordIds = [First, First] },
            "duplicateFolder" => details with { FolderIds = [First, First] },
            "extraRecord" => details with { RecordIds = [.. command.RecordIds, Guid.NewGuid()] },
            "extraFolder" => details with { FolderIds = [.. command.FolderIds, Guid.NewGuid()] },
            "missingRecord" => details with { RecordIds = [First] },
            "missingFolder" => details with { FolderIds = [First] },
            "wrongRecord" => details with { RecordIds = [First, Guid.NewGuid()] },
            "wrongFolder" => details with { FolderIds = [First, Guid.NewGuid()] },
            "swappedTypes" => details with { RecordIds = command.FolderIds, FolderIds = command.RecordIds },
            _ => details
        };

        var result = await Handler(Store(kind == "nullResult" ? null! : new(details)).Object).HandleAsync(command);

        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { "persistence_unavailable" }, result.Errors);
    }

    [UnitTheory]
    [InlineData("expectedRevision")]
    [InlineData("envelope")]
    [InlineData("editedAt")]
    [InlineData("recordIds")]
    [InlineData("folderIds")]
    public void GivenOmittedRequiredMember_WhenParsing_ThenReject(string name)
    {
        var body = JsonSerializer.SerializeToNode(Command(), Json)!.AsObject();
        body.Remove(name);
        AssertRejectedJson(body.ToJsonString());
    }

    [UnitTheory]
    [InlineData("actor")]
    [InlineData("vaultAccess")]
    [InlineData("accessVerifier")]
    [InlineData("collectionId")]
    [InlineData("ownerId")]
    [InlineData("profileIds")]
    [InlineData("keyEpoch")]
    [InlineData("grants")]
    [InlineData("keyWrappers")]
    [InlineData("memberRevisions")]
    [InlineData("plaintext")]
    public void GivenForgedContextOrUnknownMember_WhenParsing_ThenReject(string name)
    {
        var body = JsonSerializer.SerializeToNode(Command(), Json)!.AsObject();
        body[name] = null;
        AssertRejectedJson(body.ToJsonString());
    }

    [UnitTheory]
    [InlineData("duplicateRevision")]
    [InlineData("duplicateRecords")]
    [InlineData("case")]
    [InlineData("numericRevision")]
    [InlineData("numericEpoch")]
    [InlineData("scalarRecords")]
    [InlineData("objectFolders")]
    [InlineData("numberRecord")]
    [InlineData("uppercaseRecord")]
    [InlineData("compactFolder")]
    [InlineData("zeroRecord")]
    public void GivenAlternateJsonShape_WhenParsing_ThenReject(string kind)
    {
        var body = JsonSerializer.Serialize(Command(), Json);
        body = kind switch
        {
            "duplicateRevision" => body.Insert(1, "\"expectedRevision\":1,"),
            "duplicateRecords" => body.Insert(1, "\"recordIds\":[],"),
            "case" => body.Replace("expectedRevision", "ExpectedRevision"),
            "numericRevision" => body.Replace("\"expectedRevision\":1", "\"expectedRevision\":\"1\""),
            "numericEpoch" => body.Replace("\"keyEpoch\":1", "\"keyEpoch\":\"1\""),
            "uppercaseRecord" => body.Replace(Last.ToString(), Last.ToString().ToUpperInvariant()),
            "compactFolder" => body.Replace(First.ToString(), First.ToString("N")),
            "zeroRecord" => body.Replace(First.ToString(), Guid.Empty.ToString()),
            _ => ReplaceArray(body, kind == "objectFolders" ? "folderIds" : "recordIds",
                kind == "scalarRecords" ? "\"bad\"" : kind == "objectFolders" ? "{}" : "[7]")
        };
        AssertRejectedJson(body);
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenHandling_ThenPropagateBeforePersistence()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(new Mock<ICollectionMembershipStore>(MockBehavior.Strict).Object).HandleAsync(Command(), cancellation.Token));
    }

    [UnitFact]
    public async Task GivenStoreCancellation_WhenHandling_ThenPropagateExactCallerToken()
    {
        using var cancellation = new CancellationTokenSource();
        var store = new Mock<ICollectionMembershipStore>();
        store.Setup(x => x.SetAsync(It.IsAny<CollectionMembershipRequest>(), cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(store.Object).HandleAsync(Command(), cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [UnitTheory]
    [InlineData("collection_membership_set", 200)]
    [InlineData("authentication_required", 401)]
    [InlineData("vault_access_required", 401)]
    [InlineData("identity_unavailable", 503)]
    public void GivenBoundaryMessage_WhenMapping_ThenUseExpectedStatus(string message, int status) =>
        Assert.Equal(status, SetCollectionMembershipMessages.StatusCodes[message]);

    private SetCollectionMembershipCommand Command()
    {
        var command = new SetCollectionMembershipCommand
        {
            ExpectedRevision = 1,
            Envelope = new("cerberus-content-v1", 1, Access, "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA"),
            EditedAt = DateTimeOffset.UnixEpoch.AddTicks(19),
            RecordIds = [Last, First],
            FolderIds = [Last, First]
        };
        command.SetContext(actor, Access, target);
        return command;
    }

    private CollectionMembershipDetails Details(SetCollectionMembershipCommand command) =>
        new(target, 2, 7, DateTimeOffset.UnixEpoch.AddTicks(10), command.RecordIds, command.FolderIds);

    private static SetCollectionMembershipHandler Handler(ICollectionMembershipStore store) =>
        new(new SetCollectionMembershipValidator(), store);

    private static Mock<ICollectionMembershipStore> Store(VaultResult<CollectionMembershipDetails> result)
    {
        var store = new Mock<ICollectionMembershipStore>();
        store.Setup(x => x.SetAsync(It.IsAny<CollectionMembershipRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return store;
    }

    private static void AssertRejectedJson(string body)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SetCollectionMembershipCommand>(body, Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CollectionMembershipInput>(body, Json));
    }

    private static string ReplaceArray(string body, string field, string value)
    {
        var start = body.IndexOf("\"" + field + "\":[", StringComparison.Ordinal) + field.Length + 3;
        var end = body.IndexOf(']', start);
        return body[..start] + value + body[(end + 1)..];
    }
}
