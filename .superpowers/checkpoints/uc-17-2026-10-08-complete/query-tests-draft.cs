using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Query.Profiles;
using ArturRios.Cerberus.Query.Records;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.TestSupport;
using Moq;

namespace ArturRios.Cerberus.Query.Tests;

public class ListRecordsHandlerTests
{
    private const string Access = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string Verifier = "66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925";
    private static readonly Guid Actor = Guid.NewGuid();
    private static CerberusOptions Options() => new() { MaxPageSize = 100, RegistrationFingerprintKey = "fixture-dedicated-key-at-least-32-chars" };

    [UnitTheory]
    [InlineData("actor", "authentication_required")][InlineData("missing", "vault_access_required")]
    [InlineData("access", "validation_failed")][InlineData("emptyAccess", "validation_failed")]
    [InlineData("paddedAccess", "validation_failed")][InlineData("zero", "validation_failed")]
    [InlineData("negative", "validation_failed")][InlineData("large", "validation_failed")]
    [InlineData("empty", "validation_failed")][InlineData("cursor", "validation_failed")]
    [InlineData("huge", "validation_failed")][InlineData("paddedCursor", "validation_failed")]
    public async Task GivenInvalidContext_WhenListing_ThenRejectBeforePersistence(string kind, string error)
    {
        var store = new Mock<IRecordListStore>(MockBehavior.Strict);
        var query = new ListRecordsQuery(kind == "actor" ? Guid.Empty : Actor,
            kind switch { "missing" => null, "access" => "bad", "emptyAccess" => "", "paddedAccess" => Access + "=", _ => Access },
            kind switch { "zero" => 0, "negative" => -1, "large" => 101, _ => null },
            kind switch { "empty" => "", "cursor" => "invalid", "huge" => new string('A', 2049), "paddedCursor" => "AAAA=", _ => null });
        var result = await Handler(store).HandleAsync(query);
        Assert.Contains(error, result.Errors); Assert.Null(result.Data); store.VerifyNoOtherCalls();
    }

    [UnitTheory][InlineData(null, 100, 50)][InlineData(null, 2, 2)][InlineData(1, 100, 1)]
    [InlineData(100, 100, 100)][InlineData(int.MaxValue, int.MaxValue, int.MaxValue)]
    public async Task GivenValidPageBound_WhenListing_ThenForwardHashBoundAndCallerToken(int? requested, int maximum, int expected)
    {
        var options = Options(); options.MaxPageSize = maximum;
        using var ct = new CancellationTokenSource(); var store = new Mock<IRecordListStore>(MockBehavior.Strict);
        store.Setup(x => x.ListAsync(new(Actor, Verifier, expected, 0, null), ct.Token))
            .ReturnsAsync(new VaultResult<RecordListPage>(new([], 0, false)));
        var result = await new ListRecordsHandler(store.Object, options, new(options)).HandleAsync(new(Actor, Access, requested, null), ct.Token);
        Assert.True(result.Success); Assert.Contains("records_found", result.Messages);
        Assert.Empty(result.Data!.Items); Assert.Null(result.Data.NextCursor); store.VerifyAll();
    }

    [UnitFact]
    public async Task GivenPermittedPage_WhenContinuing_ThenReturnOnlyFiveItemFieldsAndBoundOpaqueCursor()
    {
        var row = Row(1); var store = new Mock<IRecordListStore>(MockBehavior.Strict);
        store.Setup(x => x.ListAsync(new(Actor, Verifier, 1, 0, null), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VaultResult<RecordListPage>(new([row], 3, true)));
        store.Setup(x => x.ListAsync(new(Actor, Verifier, 1, 1, 3), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VaultResult<RecordListPage>(new([Row(3)], 3, false)));
        var handler = Handler(store); var result = await handler.HandleAsync(new(Actor, Access, 1, null));
        Assert.True(result.Success); var item = Assert.Single(result.Data!.Items);
        Assert.Equal(row.RecordId, item.RecordId); Assert.Equal(row.Revision, item.Revision);
        Assert.Equal(row.ServerSequence, item.ServerSequence); Assert.Equal(row.EditedAt, item.EditedAt);
        Assert.Equal(JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope, ProtectionFixture.Json), item.Envelope);
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(result.Data, ProtectionFixture.Json));
        Assert.Equal(new[] { "items", "nextCursor" }, json.RootElement.EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal(new[] { "editedAt", "envelope", "recordId", "revision", "serverSequence" },
            json.RootElement.GetProperty("items")[0].EnumerateObject().Select(x => x.Name).Order());
        var cursor = Assert.IsType<string>(result.Data.NextCursor);
        Assert.DoesNotContain(Actor.ToString(), cursor); Assert.DoesNotContain(Verifier, cursor);
        Assert.True(new RecordListCursor(Options()).TryDecode(cursor, out var decoded));
        Assert.Equal(new RecordListContinuation(Actor, Verifier, 1, 1, 3), decoded);
        var next = await handler.HandleAsync(new(Actor, Access, 1, cursor));
        Assert.True(next.Success); Assert.Equal(3, Assert.Single(next.Data!.Items).ServerSequence); Assert.Null(next.Data.NextCursor);
    }

    [UnitTheory][InlineData("actor")][InlineData("access")][InlineData("size")][InlineData("tamper")]
    [InlineData("key")][InlineData("profilePurpose")]
    public async Task GivenCursorSubstitution_WhenContinuing_ThenRejectBeforePersistence(string kind)
    {
        var options = Options(); var codec = new RecordListCursor(options);
        var cursor = kind == "profilePurpose" ? new ProfileListCursor(options).Encode(new(Actor, Verifier, 1, 1, 3)) : codec.Encode(new(Actor, Verifier, 1, 1, 3));
        if (kind == "tamper") { Assert.True(ProtocolBinary.TryDecode(cursor, null, out var bytes)); bytes[^1] ^= 1; cursor = ProtocolBinary.Encode(bytes); }
        if (kind == "key") options.RegistrationFingerprintKey = new string('z', 40);
        var store = new Mock<IRecordListStore>(MockBehavior.Strict);
        var result = await new ListRecordsHandler(store.Object, options, new(options)).HandleAsync(new(
            kind == "actor" ? Guid.NewGuid() : Actor,
            kind == "access" ? ProtocolBinary.Encode(Enumerable.Repeat((byte)1, 32).ToArray()) : Access,
            kind == "size" ? 2 : 1, cursor));
        Assert.Contains("validation_failed", result.Errors); Assert.Null(result.Data); store.VerifyNoOtherCalls();
    }

    [UnitTheory][InlineData("afterZero")][InlineData("afterNegative")][InlineData("afterBoundary")][InlineData("afterPast")]
    [InlineData("boundaryUnsafe")][InlineData("boundaryNegative")][InlineData("sizeZero")][InlineData("sizeNegative")]
    [InlineData("actor")][InlineData("verifierLength")][InlineData("verifierCase")][InlineData("verifierNonHex")]
    public void GivenAuthenticatedInvalidCursorTuple_WhenDecoding_ThenReject(string kind)
    {
        var tuple = new RecordListContinuation(kind == "actor" ? Guid.Empty : Actor,
            kind switch { "verifierLength" => "bad", "verifierCase" => Verifier.ToUpperInvariant(), "verifierNonHex" => new string('g', 64), _ => Verifier },
            kind switch { "sizeZero" => 0, "sizeNegative" => -1, _ => 1 },
            kind switch { "afterZero" => 0, "afterNegative" => -1, "afterBoundary" => 3, "afterPast" => 4, _ => 1 },
            kind switch { "boundaryUnsafe" => ProtocolBinary.MaxInteger + 1, "boundaryNegative" => -1, _ => 3 });
        var codec = new RecordListCursor(Options()); Assert.False(codec.TryDecode(codec.Encode(tuple), out _));
    }

    [UnitTheory][InlineData("duplicate")][InlineData("unknown")][InlineData("case")][InlineData("numericString")]
    [InlineData("unsafeInteger")][InlineData("null")][InlineData("missingActor")][InlineData("missingVerifier")]
    [InlineData("missingSize")][InlineData("missingAfter")][InlineData("missingBoundary")]
    public void GivenAuthenticatedMalformedCursorJson_WhenDecoding_ThenReject(string kind)
    {
        var json = JsonSerializer.Serialize(new RecordListContinuation(Actor, Verifier, 1, 1, 3), ProtectionFixture.Json);
        json = kind switch {
            "duplicate" => json.Replace("{", "{\"after\":1,"), "unknown" => json.Replace("{", "{\"secret\":1,"),
            "case" => json.Replace("\"actor\"", "\"Actor\""), "numericString" => json.Replace("\"after\":1", "\"after\":\"1\""),
            "unsafeInteger" => json.Replace("\"after\":1", "\"after\":9223372036854775808"), "null" => "null",
            "missingActor" => json.Replace($"\"actor\":\"{Actor}\",", ""), "missingVerifier" => json.Replace($"\"accessVerifier\":\"{Verifier}\",", ""),
            "missingSize" => json.Replace("\"pageSize\":1,", ""), "missingAfter" => json.Replace("\"after\":1,", ""),
            "missingBoundary" => json.Replace(",\"boundary\":3", ""), _ => json };
        Assert.False(new RecordListCursor(Options()).TryDecode(AuthenticatedJson(json), out _));
    }

    [UnitTheory][InlineData("not_found")][InlineData("vault_access_denied")][InlineData("persistence_unavailable")]
    public async Task GivenAllowlistedStoreError_WhenListing_ThenReturnSafeErrorWithoutPayload(string error)
    { var result = await Handler(MockStore(new(Error: error))).HandleAsync(new(Actor, Access, null, null)); Assert.Contains(error, result.Errors); Assert.Null(result.Data); }

    [UnitTheory][InlineData("nullResult")][InlineData("nullData")][InlineData("unknownError")][InlineData("errorWithData")]
    [InlineData("successAsError")][InlineData("authenticationAsError")]
    public async Task GivenBrokenStoreContract_WhenListing_ThenFailClosedWithoutLeakingErrors(string kind)
    {
        VaultResult<RecordListPage>? page = kind switch {
            "nullResult" => null, "nullData" => new(), "unknownError" => new(Error: "SELECT private table failed"),
            "errorWithData" => new(new([Row(1)], 1, false), "not_found"), "successAsError" => new(Error: "records_found"),
            _ => new(Error: "authentication_required") };
        var result = await Handler(MockStore(page!)).HandleAsync(new(Actor, Access, null, null));
        Assert.Contains("persistence_unavailable", result.Errors); Assert.Null(result.Data);
        Assert.DoesNotContain("SELECT private table failed", result.Errors);
    }

    [UnitTheory][InlineData("nullItems")][InlineData("nullRow")][InlineData("id")][InlineData("revisionZero")]
    [InlineData("revisionUnsafe")][InlineData("sequenceZero")][InlineData("sequenceUnsafe")][InlineData("defaultTime")]
    [InlineData("infinityTime")][InlineData("offset")][InlineData("precision")][InlineData("order")][InlineData("duplicate")]
    [InlineData("count")][InlineData("boundaryNegative")][InlineData("boundaryUnsafe")][InlineData("outsideBoundary")]
    [InlineData("emptyMore")][InlineData("shortMore")][InlineData("atBoundaryMore")]
    public async Task GivenCorruptPageMetadata_WhenListing_ThenRejectWholePayload(string kind)
    {
        var row = Row(1); row = kind switch {
            "id" => row with { RecordId = Guid.Empty }, "revisionZero" => row with { Revision = 0 },
            "revisionUnsafe" => row with { Revision = ProtocolBinary.MaxInteger + 1 }, "sequenceZero" => row with { ServerSequence = 0 },
            "sequenceUnsafe" => row with { ServerSequence = ProtocolBinary.MaxInteger + 1 }, "defaultTime" => row with { EditedAt = default },
            "infinityTime" => row with { EditedAt = new DateTimeOffset(5, TimeSpan.Zero) },
            "offset" => row with { EditedAt = row.EditedAt.ToOffset(TimeSpan.FromHours(1)) },
            "precision" => row with { EditedAt = row.EditedAt.AddTicks(1) }, _ => row };
        IReadOnlyList<RecordListRow> rows = kind switch {
            "nullItems" => null!, "nullRow" => [null!], "order" => [Row(2), row],
            "duplicate" => [row, row with { ServerSequence = 2 }], "count" => Enumerable.Range(1, 51).Select(x => Row(x)).ToArray(),
            "emptyMore" => [], _ => [row] };
        var boundary = kind switch { "boundaryNegative" => -1, "boundaryUnsafe" => ProtocolBinary.MaxInteger + 1, "outsideBoundary" => 0, "atBoundaryMore" => 1, _ => 100 };
        var result = await Handler(MockStore(new(new(rows, boundary, kind is "emptyMore" or "shortMore" or "atBoundaryMore"))))
            .HandleAsync(new(Actor, Access, kind == "atBoundaryMore" ? 1 : null, null));
        Assert.Contains("persistence_unavailable", result.Errors); Assert.Null(result.Data);
    }

    [UnitTheory][InlineData("nullBytes")][InlineData("badJson")][InlineData("nullJson")][InlineData("unknown")]
    [InlineData("duplicate")][InlineData("case")][InlineData("numericString")][InlineData("format")][InlineData("epochZero")]
    [InlineData("epochUnsafe")][InlineData("salt")][InlineData("nonce")][InlineData("ciphertext")][InlineData("tag")][InlineData("padded")]
    public async Task GivenCorruptNativeEnvelope_WhenListing_ThenRejectEveryItemWithoutPartialPayload(string kind)
    {
        var row = Row(2); var envelope = JsonSerializer.Deserialize<EncryptedEnvelope>(row.Envelope, ProtectionFixture.Json)!;
        envelope = kind switch {
            "format" => envelope with { Format = "foreign" }, "epochZero" => envelope with { KeyEpoch = 0 },
            "epochUnsafe" => envelope with { KeyEpoch = ProtocolBinary.MaxInteger + 1 }, "salt" => envelope with { KeySalt = "AQ" },
            "nonce" => envelope with { Nonce = "AQ" }, "ciphertext" => envelope with { Ciphertext = "" },
            "tag" => envelope with { Tag = "AQ" }, "padded" => envelope with { KeySalt = envelope.KeySalt + "=" }, _ => envelope };
        var json = JsonSerializer.Serialize(envelope, ProtectionFixture.Json);
        json = kind switch {
            "badJson" => "bad", "nullJson" => "null", "unknown" => json.Replace("{", "{\"secret\":1,"),
            "duplicate" => json.Replace("{", "{\"keyEpoch\":1,"), "case" => json.Replace("\"format\"", "\"Format\""),
            "numericString" => json.Replace("\"keyEpoch\":1", "\"keyEpoch\":\"1\""), _ => json };
        row = row with { Envelope = kind == "nullBytes" ? null! : Encoding.UTF8.GetBytes(json) };
        var result = await Handler(MockStore(new(new([Row(1), row], 2, false)))).HandleAsync(new(Actor, Access, null, null));
        Assert.Contains("persistence_unavailable", result.Errors); Assert.Null(result.Data);
    }

    [UnitTheory][InlineData("boundaryChanged")][InlineData("boundaryBehind")][InlineData("afterRepeated")]
    public async Task GivenInvalidContinuationPage_WhenListing_ThenRejectStoreViolation(string kind)
    {
        var cursor = new RecordListCursor(Options()).Encode(new(Actor, Verifier, 1, 1, 3));
        var store = new Mock<IRecordListStore>(MockBehavior.Strict);
        store.Setup(x => x.ListAsync(new(Actor, Verifier, 1, 1, 3), It.IsAny<CancellationToken>())).ReturnsAsync(
            new VaultResult<RecordListPage>(new([Row(kind == "afterRepeated" ? 1 : 2)], kind == "boundaryChanged" ? 4 : kind == "boundaryBehind" ? 0 : 3, false)));
        var result = await Handler(store).HandleAsync(new(Actor, Access, 1, cursor)); Assert.Contains("persistence_unavailable", result.Errors); Assert.Null(result.Data);
    }

    [UnitFact]
    public async Task GivenCancelledCaller_WhenListing_ThenPropagateBeforeStore()
    { using var ct = new CancellationTokenSource(); ct.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(new(MockBehavior.Strict)).HandleAsync(new(Actor, Access, null, null), ct.Token)); }

    [UnitFact]
    public async Task GivenStoreCancellation_WhenListing_ThenPropagateSameCallerToken()
    {
        using var ct = new CancellationTokenSource(); var store = new Mock<IRecordListStore>(MockBehavior.Strict);
        store.Setup(x => x.ListAsync(new(Actor, Verifier, 50, 0, null), ct.Token)).ThrowsAsync(new OperationCanceledException(ct.Token));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Handler(store).HandleAsync(new(Actor, Access, null, null), ct.Token));
    }

    private static Mock<IRecordListStore> MockStore(VaultResult<RecordListPage> result)
    {
        var store = new Mock<IRecordListStore>(MockBehavior.Strict);
        store.Setup(x => x.ListAsync(It.Is<RecordListRequest>(r => r.Actor == Actor && r.AccessVerifier == Verifier && r.After == 0 && r.Boundary == null), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return store;
    }
    private static ListRecordsHandler Handler(Mock<IRecordListStore> store) => new(store.Object, Options(), new(Options()));
    private static RecordListRow Row(long sequence) => new(Guid.NewGuid(), 1, sequence, DateTimeOffset.FromUnixTimeSeconds(1780000000),
        JsonSerializer.SerializeToUtf8Bytes(new EncryptedEnvelope("cerberus-content-v1", 1, Access, "AAAAAAAAAAAAAAAA", "AQID", "AAAAAAAAAAAAAAAAAAAAAA"), ProtectionFixture.Json));
    private static string AuthenticatedJson(string plaintext)
    {
        var purpose = "cerberus-record-list-cursor-v1"u8.ToArray();
        var key = HMACSHA256.HashData(Encoding.UTF8.GetBytes(Options().RegistrationFingerprintKey!), purpose);
        var bytes = Encoding.UTF8.GetBytes(plaintext); var encoded = new byte[28 + bytes.Length]; RandomNumberGenerator.Fill(encoded.AsSpan(0, 12));
        using var aes = new AesGcm(key, 16); aes.Encrypt(encoded.AsSpan(0, 12), bytes, encoded.AsSpan(28), encoded.AsSpan(12, 16), purpose);
        return ProtocolBinary.Encode(encoded);
    }
}
