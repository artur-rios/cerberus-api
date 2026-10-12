using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Query.Collections;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Query.Tests;

public class GetCollectionHandlerTests
{
    private const string Access = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Verifier = "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925";
    private static readonly Guid Actor = Guid.Parse("10000000-0000-0000-0000-000000000001");
    private static readonly Guid Target = Guid.Parse("20000000-0000-0000-0000-000000000001");
    private static readonly Guid First = Guid.Parse("00000001-0000-0000-0000-000000000001");
    private static readonly Guid Middle = Guid.Parse("00000002-0000-0000-0000-000000000001");
    private static readonly Guid Last = Guid.Parse("00000003-0000-0000-0000-000000000001");

    [UnitTheory]
    [InlineData("actor", "authentication_required")]
    [InlineData("missing", "vault_access_required")]
    [InlineData("malformed", "validation_failed")]
    [InlineData("empty", "validation_failed")]
    [InlineData("padded", "validation_failed")]
    [InlineData("noncanonical", "validation_failed")]
    [InlineData("target", "validation_failed")]
    public async Task GivenInvalidContext_WhenGetting_ThenRejectBeforePersistence(string kind, string error)
    {
        var store = new Mock<ICollectionReadStore>(MockBehavior.Strict);
        var result = await new GetCollectionHandler(store.Object).HandleAsync(new(
            kind == "actor" ? Guid.Empty : Actor,
            kind switch { "missing" => null, "malformed" => "bad", "empty" => "", "padded" => Access + "=", "noncanonical" => Access[..^1] + "B", _ => Access },
            kind == "target" ? Guid.Empty : Target));

        Assert.Equal(new[] { error }, result.Errors);
        Assert.Null(result.Data);
        Assert.Empty(result.Messages);
        store.VerifyNoOtherCalls();
    }

    [UnitTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task GivenVisibleDetails_WhenGetting_ThenForwardVerifierIdAndTokenAndReturnExactlyEightPublicFields(bool relationships)
    {
        Guid[] profiles = relationships ? [Last, First, Middle] : [];
        Guid[] records = relationships ? [Middle, First] : [];
        Guid[] folders = relationships ? [Last, First] : [];
        using var caller = new CancellationTokenSource();
        var store = new Mock<ICollectionReadStore>(MockBehavior.Strict);
        store.Setup(x => x.ReadAsync(new(Actor, Verifier, Target), caller.Token))
            .ReturnsAsync(new VaultResult<CollectionReadDetails>(new(Row(), profiles, records, folders)));

        var result = await new GetCollectionHandler(store.Object).HandleAsync(new(Actor, Access, Target), caller.Token);

        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "collection_found" }, result.Messages);
        var item = Assert.IsType<CollectionDetailsOutput>(result.Data);
        Assert.Equal(Target, item.CollectionId);
        Assert.Equal(1, item.Revision);
        Assert.Equal(2, item.ServerSequence);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1780000000), item.EditedAt);
        Assert.Equal(new EncryptedEnvelope("cerberus-content-v1", 1, Access, "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA"), item.Envelope);
        Assert.Equal(relationships ? new[] { First, Middle, Last } : [], item.ProfileIds);
        Assert.Equal(relationships ? new[] { First, Middle } : [], item.RecordIds);
        Assert.Equal(relationships ? new[] { First, Last } : [], item.FolderIds);
        if (relationships)
        {
            Assert.Equal(new[] { Last, First, Middle }, profiles);
            Assert.Equal(new[] { Middle, First }, records);
            Assert.Equal(new[] { Last, First }, folders);
        }
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(item, ProtectionFixture.Json));
        Assert.Equal(new[] { "collectionId", "editedAt", "envelope", "folderIds", "profileIds", "recordIds", "revision", "serverSequence" },
            json.RootElement.EnumerateObject().Select(x => x.Name).Order());
        store.VerifyAll();
        store.VerifyNoOtherCalls();
    }

    [UnitTheory]
    [InlineData("nullCollection")][InlineData("targetMismatch")][InlineData("zeroId")]
    [InlineData("revisionZero")][InlineData("revisionNegative")][InlineData("revisionUnsafe")]
    [InlineData("sequenceZero")][InlineData("sequenceNegative")][InlineData("sequenceUnsafe")]
    [InlineData("epochZero")][InlineData("epochNegative")][InlineData("epochUnsafe")][InlineData("epochMismatch")]
    [InlineData("defaultTime")][InlineData("subMicrosecondTime")][InlineData("offset")][InlineData("precision")]
    public async Task GivenCorruptCollectionProjection_WhenGetting_ThenRejectWithoutPartialData(string kind)
    {
        var good = Row();
        var row = kind switch
        {
            "nullCollection" => null!, "targetMismatch" => good with { CollectionId = First }, "zeroId" => good with { CollectionId = Guid.Empty },
            "revisionZero" => good with { Revision = 0 }, "revisionNegative" => good with { Revision = -1 }, "revisionUnsafe" => good with { Revision = 9007199254740992 },
            "sequenceZero" => good with { ServerSequence = 0 }, "sequenceNegative" => good with { ServerSequence = -1 }, "sequenceUnsafe" => good with { ServerSequence = 9007199254740992 },
            "epochZero" => good with { KeyEpoch = 0 }, "epochNegative" => good with { KeyEpoch = -1 }, "epochUnsafe" => good with { KeyEpoch = 9007199254740992 },
            "epochMismatch" => good with { KeyEpoch = 2 }, "defaultTime" => good with { EditedAt = default },
            "subMicrosecondTime" => good with { EditedAt = new DateTimeOffset(5, TimeSpan.Zero) },
            "offset" => good with { EditedAt = good.EditedAt.ToOffset(TimeSpan.FromHours(1)) },
            "precision" => good with { EditedAt = good.EditedAt.AddTicks(1) }, _ => good
        };
        var result = await Handler(new(new(row, [First], [Middle], [Last]))).HandleAsync(new(Actor, Access, Target));

        Assert.Equal(new[] { "persistence_unavailable" }, result.Errors);
        Assert.Null(result.Data);
        Assert.Empty(result.Messages);
    }

    [UnitTheory]
    [InlineData(1L, 1L, 1L, 10L)]
    [InlineData(9007199254740991L, 9007199254740991L, 9007199254740991L, 3155378975999999990L)]
    public async Task GivenValidProjectionBoundaries_WhenGetting_ThenPreserveValues(long revision, long sequence, long epoch, long ticks)
    {
        var row = Row() with
        {
            Revision = revision, ServerSequence = sequence, KeyEpoch = epoch, EditedAt = new DateTimeOffset(ticks, TimeSpan.Zero),
            Envelope = JsonSerializer.SerializeToUtf8Bytes(new EncryptedEnvelope("cerberus-content-v1", epoch, Access, "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA"), ProtectionFixture.Json)
        };
        var result = await Handler(new(new(row, [], [], []))).HandleAsync(new(Actor, Access, Target));

        Assert.True(result.Success);
        Assert.Equal(new[] { "collection_found" }, result.Messages);
        Assert.Equal(revision, result.Data!.Revision);
        Assert.Equal(sequence, result.Data.ServerSequence);
        Assert.Equal(epoch, result.Data.Envelope.KeyEpoch);
        Assert.Equal(ticks, result.Data.EditedAt.Ticks);
    }

    [UnitTheory]
    [InlineData("profile", "null")][InlineData("profile", "zero")][InlineData("profile", "duplicate")]
    [InlineData("record", "null")][InlineData("record", "zero")][InlineData("record", "duplicate")]
    [InlineData("folder", "null")][InlineData("folder", "zero")][InlineData("folder", "duplicate")]
    public async Task GivenInvalidTypedRelationshipArray_WhenGetting_ThenRejectWholePayload(string resource, string corruption)
    {
        Guid[] invalid = corruption switch { "null" => null!, "zero" => [Guid.Empty], _ => [First, First] };
        var details = new CollectionReadDetails(Row(), resource == "profile" ? invalid : [First],
            resource == "record" ? invalid : [Middle], resource == "folder" ? invalid : [Last]);
        var result = await Handler(new(details)).HandleAsync(new(Actor, Access, Target));

        Assert.Equal(new[] { "persistence_unavailable" }, result.Errors);
        Assert.Null(result.Data);
        Assert.Empty(result.Messages);
    }

    [UnitTheory]
    [InlineData("nullBytes")][InlineData("badJson")][InlineData("nullJson")][InlineData("arrayJson")]
    [InlineData("unknown")][InlineData("duplicate")][InlineData("case")][InlineData("numericString")]
    [InlineData("format")][InlineData("nullFormat")][InlineData("missingFormat")]
    [InlineData("epochZero")][InlineData("epochNegative")][InlineData("epochUnsafe")][InlineData("missingEpoch")]
    [InlineData("salt")][InlineData("nullSalt")][InlineData("missingSalt")]
    [InlineData("nonce")][InlineData("nullNonce")][InlineData("missingNonce")]
    [InlineData("ciphertext")][InlineData("nullCiphertext")][InlineData("missingCiphertext")]
    [InlineData("tag")][InlineData("nullTag")][InlineData("missingTag")]
    [InlineData("paddedSalt")][InlineData("paddedNonce")][InlineData("paddedCiphertext")][InlineData("paddedTag")]
    [InlineData("noncanonicalCiphertext")][InlineData("noncanonicalTag")]
    public async Task GivenCorruptNativeEnvelope_WhenGetting_ThenRejectWithoutPartialPayload(string kind)
    {
        var envelope = new EncryptedEnvelope("cerberus-content-v1", 1, Access, "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA");
        envelope = kind switch
        {
            "format" => envelope with { Format = "foreign" }, "nullFormat" => envelope with { Format = null! },
            "epochZero" => envelope with { KeyEpoch = 0 }, "epochNegative" => envelope with { KeyEpoch = -1 }, "epochUnsafe" => envelope with { KeyEpoch = 9007199254740992 },
            "salt" => envelope with { KeySalt = "AQ" }, "nullSalt" => envelope with { KeySalt = null! },
            "nonce" => envelope with { Nonce = "AQ" }, "nullNonce" => envelope with { Nonce = null! },
            "ciphertext" => envelope with { Ciphertext = "" }, "nullCiphertext" => envelope with { Ciphertext = null! },
            "tag" => envelope with { Tag = "AQ" }, "nullTag" => envelope with { Tag = null! },
            "paddedSalt" => envelope with { KeySalt = Access + "=" }, "paddedNonce" => envelope with { Nonce = "AAAAAAAAAAAAAAAA=" },
            "paddedCiphertext" => envelope with { Ciphertext = "AQID=" }, "paddedTag" => envelope with { Tag = "AAAAAAAAAAAAAAAAAAAAAA==" },
            "noncanonicalCiphertext" => envelope with { Ciphertext = "AR" }, "noncanonicalTag" => envelope with { Tag = "AAAAAAAAAAAAAAAAAAAAAB" }, _ => envelope
        };
        var json = JsonSerializer.Serialize(envelope, ProtectionFixture.Json);
        json = kind switch
        {
            "badJson" => "bad", "nullJson" => "null", "arrayJson" => "[]", "unknown" => json.Replace("{", "{\"private\":1,"),
            "duplicate" => json.Replace("{", "{\"keyEpoch\":1,"), "case" => json.Replace("\"format\"", "\"Format\""),
            "numericString" => json.Replace("\"keyEpoch\":1", "\"keyEpoch\":\"1\""),
            "missingFormat" => json.Replace("\"format\":\"cerberus-content-v1\",", ""), "missingEpoch" => json.Replace("\"keyEpoch\":1,", ""),
            "missingSalt" => json.Replace("\"keySalt\":\"" + Access + "\",", ""), "missingNonce" => json.Replace("\"nonce\":\"AAAAAAAAAAAAAAAA\",", ""),
            "missingCiphertext" => json.Replace("\"ciphertext\":\"AQID\",", ""), "missingTag" => json.Replace(",\"tag\":\"AAAAAAAAAAAAAAAAAAAAAA\"", ""), _ => json
        };
        var row = Row() with { Envelope = kind == "nullBytes" ? null! : Encoding.UTF8.GetBytes(json) };
        var result = await Handler(new(new(row, [First], [Middle], [Last]))).HandleAsync(new(Actor, Access, Target));

        Assert.Equal(new[] { "persistence_unavailable" }, result.Errors);
        Assert.Null(result.Data);
        Assert.Empty(result.Messages);
    }

    [UnitTheory]
    [InlineData("not_found")][InlineData("vault_access_denied")][InlineData("persistence_unavailable")]
    public async Task GivenSafeStoreError_WhenGetting_ThenReturnOnlyAllowlistedError(string error)
    {
        var result = await Handler(new(Error: error)).HandleAsync(new(Actor, Access, Target));
        Assert.Equal(new[] { error }, result.Errors);
        Assert.Null(result.Data);
        Assert.Empty(result.Messages);
    }

    [UnitTheory]
    [InlineData("nullResult")][InlineData("nullData")][InlineData("unknownError")][InlineData("emptyError")]
    [InlineData("dataAndError")][InlineData("successAsError")][InlineData("authenticationAsError")][InlineData("identityAsError")]
    public async Task GivenBrokenStoreResult_WhenGetting_ThenFailClosedWithoutPrivateErrors(string kind)
    {
        VaultResult<CollectionReadDetails>? details = kind switch
        {
            "nullResult" => null, "nullData" => new(), "unknownError" => new(Error: "SELECT private table failed"), "emptyError" => new(Error: ""),
            "dataAndError" => new(new(Row(), [], [], []), "not_found"), "successAsError" => new(Error: "collection_found"),
            "identityAsError" => new(Error: "identity_unavailable"), _ => new(Error: "authentication_required")
        };
        var result = await Handler(details!).HandleAsync(new(Actor, Access, Target));
        Assert.Equal(new[] { "persistence_unavailable" }, result.Errors);
        Assert.Null(result.Data);
        Assert.Empty(result.Messages);
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenGetting_ThenPropagateBeforePersistence()
    {
        using var caller = new CancellationTokenSource();
        caller.Cancel();
        var store = new Mock<ICollectionReadStore>(MockBehavior.Strict);
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GetCollectionHandler(store.Object).HandleAsync(new(Actor, Access, Target), caller.Token));
        Assert.Equal(caller.Token, exception.CancellationToken);
        store.VerifyNoOtherCalls();
    }

    [UnitFact]
    public async Task GivenCallerCancellationDuringStore_WhenGetting_ThenPropagateCallerCancellation()
    {
        using var caller = new CancellationTokenSource();
        var store = new Mock<ICollectionReadStore>(MockBehavior.Strict);
        store.Setup(x => x.ReadAsync(new(Actor, Verifier, Target), caller.Token)).Returns(() =>
        {
            caller.Cancel();
            throw new OperationCanceledException(caller.Token);
        });
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new GetCollectionHandler(store.Object).HandleAsync(new(Actor, Access, Target), caller.Token));
        Assert.Equal(caller.Token, exception.CancellationToken);
        store.VerifyAll();
    }

    [UnitTheory]
    [InlineData("unrelated")][InlineData("none")][InlineData("uncancelledCaller")]
    public async Task GivenInternalStoreCancellation_WhenGetting_ThenReturnSafe503(string kind)
    {
        using var caller = new CancellationTokenSource();
        using var dependency = new CancellationTokenSource();
        dependency.Cancel();
        var store = new Mock<ICollectionReadStore>(MockBehavior.Strict);
        store.Setup(x => x.ReadAsync(new(Actor, Verifier, Target), caller.Token))
            .ThrowsAsync(new OperationCanceledException(kind switch { "unrelated" => dependency.Token, "uncancelledCaller" => caller.Token, _ => CancellationToken.None }));
        var result = await new GetCollectionHandler(store.Object).HandleAsync(new(Actor, Access, Target), caller.Token);
        Assert.Equal(new[] { "persistence_unavailable" }, result.Errors);
        Assert.Null(result.Data);
        Assert.Empty(result.Messages);
        store.VerifyAll();
    }

    [UnitTheory]
    [InlineData("collection_found", 200)][InlineData("validation_failed", 400)]
    [InlineData("authentication_required", 401)][InlineData("vault_access_required", 401)]
    [InlineData("vault_access_denied", 403)][InlineData("not_found", 404)]
    [InlineData("identity_unavailable", 503)][InlineData("persistence_unavailable", 503)]
    public void GivenPublicOutcome_WhenMappingToHttp_ThenUseContractStatus(string message, int status)
        => Assert.Equal(status, CollectionReadMessages.StatusCodes[message]);

    private static GetCollectionHandler Handler(VaultResult<CollectionReadDetails> result)
    {
        var store = new Mock<ICollectionReadStore>(MockBehavior.Strict);
        store.Setup(x => x.ReadAsync(new(Actor, Verifier, Target), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return new(store.Object);
    }

    private static CollectionListRow Row() => new(Target, 1, 2, DateTimeOffset.FromUnixTimeSeconds(1780000000),
        Encoding.UTF8.GetBytes("{\"format\":\"cerberus-content-v1\",\"keyEpoch\":1,\"keySalt\":\"AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA\",\"nonce\":\"AAAAAAAAAAAAAAAA\",\"ciphertext\":\"AQID\",\"tag\":\"AAAAAAAAAAAAAAAAAAAAAA\"}"), 1);
}
