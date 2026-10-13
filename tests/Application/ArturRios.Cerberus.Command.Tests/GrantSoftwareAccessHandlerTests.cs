using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Command.Software;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Cerberus.WebApi;
using Moq;

namespace ArturRios.Cerberus.Command.Tests;

public class GrantSoftwareAccessHandlerTests
{
    private const string Handle = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private static readonly Guid Actor = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
    private static readonly Guid Grant = Guid.Parse("bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb");
    private static readonly Guid Identity = Guid.Parse("cccccccc-cccc-4ccc-8ccc-cccccccccccc");
    private static readonly Guid Target = Guid.Parse("dddddddd-dddd-4ddd-8ddd-dddddddddddd");
    private static readonly JsonSerializerOptions Json = new(ProtectionFixture.Json)
    {
        NumberHandling = JsonNumberHandling.Strict,
        Converters = { new CanonicalGuidConverter() }
    };

    // Omitting a required member or weakening the strict request boundary must fail closed.
    public static IEnumerable<object[]> RequiredMembers()
    {
        foreach (var member in new[] { "grantId", "softwareIdentityId", "expectedRevision", "recipientKey", "targets" })
            yield return ["body", member];
        foreach (var member in new[] { "crv", "kty", "x", "y" }) yield return ["key", member];
        foreach (var member in new[] { "kind", "id", "keyEnvelope" }) yield return ["target", member];
    }

    [UnitTheory]
    [MemberData(nameof(RequiredMembers))]
    public void GivenRequiredMember_WhenOmittedOrCaseForgedOrDuplicated_ThenRejectJson(string path, string member)
    {
        foreach (var mutation in new[] { "omitted", "case", "duplicate" })
        {
            var body = Body();
            var node = Node(body, path);
            var value = node[member]?.DeepClone();
            node.Remove(member);
            if (mutation == "case") node[char.ToUpperInvariant(member[0]) + member[1..]] = value;
            if (mutation == "duplicate") node[member] = value;
            var json = body.ToJsonString();
            if (mutation == "duplicate")
            {
                var original = JsonSerializer.Serialize(member) + ":" + (value?.ToJsonString() ?? "null");
                json = json.Replace(original, original + "," + original, StringComparison.Ordinal);
            }
            AssertRejected(json);
        }
    }

    [UnitTheory]
    [InlineData("body")]
    [InlineData("key")]
    [InlineData("target")]
    [InlineData("envelope")]
    public void GivenUnknownOrForgedMember_WhenParsing_ThenRejectJson(string path)
    {
        foreach (var member in new[] { "ownerId", "actor", "access", "vaultAccess", "accessVerifier", "plaintext", "d" })
        {
            var body = Body();
            Node(body, path)[member] = null;
            AssertRejected(body.ToJsonString());
        }
    }

    [UnitTheory]
    [InlineData("expectedRevision", "body")]
    [InlineData("keyEpoch", "envelope")]
    [InlineData("grantRevision", "envelope")]
    public void GivenStringOrFractionalNumber_WhenParsing_ThenRejectJson(string member, string path)
    {
        foreach (var value in new JsonNode[] { JsonValue.Create("1")!, JsonValue.Create(1.5)! })
        {
            var body = Body();
            Node(body, path)[member] = value;
            AssertRejected(body.ToJsonString());
        }
    }

    [UnitTheory]
    [InlineData("grantId", "body")]
    [InlineData("softwareIdentityId", "body")]
    [InlineData("id", "target")]
    [InlineData("grantId", "envelope")]
    [InlineData("recipientIdentityId", "envelope")]
    public void GivenNoncanonicalOrZeroUuid_WhenParsing_ThenRejectJson(string member, string path)
    {
        foreach (var value in new[] { Target.ToString().ToUpperInvariant(), Target.ToString("N"), Target.ToString("B"),
                     " " + Target, Target + " ", Guid.Empty.ToString(), "invalid" })
        {
            var body = Body();
            Node(body, path)[member] = value;
            AssertRejected(body.ToJsonString());
        }
        var numeric = Body();
        Node(numeric, path)[member] = 1;
        AssertRejected(numeric.ToJsonString());
    }

    // Missing legacy envelope members are rejected semantically, matching the native envelope contract.
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
    public void GivenEnvelopeMember_WhenOmittedOrCaseForgedOrDuplicated_ThenReject(string member)
    {
        var body = Body();
        var envelope = Node(body, "envelope");
        var value = envelope[member]!.DeepClone();
        envelope.Remove(member);
        Assert.False(JsonSerializer.Deserialize<SoftwareGrantInput>(body.ToJsonString(), Json)!.IsValid());
        Assert.False(JsonSerializer.Deserialize<GrantSoftwareAccessCommand>(body.ToJsonString(), Json)!.ToInput().IsValid());
        envelope[char.ToUpperInvariant(member[0]) + member[1..]] = value;
        AssertRejected(body.ToJsonString());
        envelope.Remove(char.ToUpperInvariant(member[0]) + member[1..]);
        envelope[member] = value;
        var original = JsonSerializer.Serialize(member) + ":" + value.ToJsonString();
        AssertRejected(body.ToJsonString().Replace(original, original + "," + original, StringComparison.Ordinal));
    }

    [UnitTheory]
    [InlineData("grant")]
    [InlineData("identity")]
    [InlineData("zeroRevision")]
    [InlineData("negativeRevision")]
    [InlineData("exhaustedRevision")]
    [InlineData("unsafeRevision")]
    [InlineData("nullKey")]
    [InlineData("curve")]
    [InlineData("keyType")]
    [InlineData("coordinate")]
    [InlineData("offCurve")]
    [InlineData("nullTargets")]
    [InlineData("emptyTargets")]
    [InlineData("nullTarget")]
    [InlineData("duplicate")]
    [InlineData("kindCase")]
    [InlineData("kindWhitespace")]
    [InlineData("kindUnknown")]
    [InlineData("zeroTarget")]
    [InlineData("nullEnvelope")]
    [InlineData("format")]
    [InlineData("epoch")]
    [InlineData("unsafeEpoch")]
    [InlineData("envelopeGrant")]
    [InlineData("envelopeIdentity")]
    [InlineData("envelopeKey")]
    [InlineData("envelopeRevision")]
    [InlineData("unsafeEnvelopeRevision")]
    [InlineData("authorPin")]
    [InlineData("enc")]
    [InlineData("ciphertext")]
    [InlineData("signature")]
    public void GivenInvalidInput_WhenValidating_ThenReject(string mutation)
    {
        var command = Command();
        var key = command.RecipientKey;
        var target = command.Targets[0];
        var envelope = target.KeyEnvelope;
        switch (mutation)
        {
            case "grant": command.GrantId = Guid.Empty; break;
            case "identity": command.SoftwareIdentityId = Guid.Empty; break;
            case "zeroRevision": command.ExpectedRevision = 0; break;
            case "negativeRevision": command.ExpectedRevision = -1; break;
            case "exhaustedRevision": command.ExpectedRevision = 9007199254740991; break;
            case "unsafeRevision": command.ExpectedRevision = long.MaxValue; break;
            case "nullKey": command.RecipientKey = null!; break;
            case "curve": command.RecipientKey = key with { Crv = "p-256" }; break;
            case "keyType": command.RecipientKey = key with { Kty = "ec" }; break;
            case "coordinate": command.RecipientKey = key with { X = key.X + "=" }; break;
            case "offCurve": command.RecipientKey = key with { X = Handle, Y = Handle }; break;
            case "nullTargets": command.Targets = null!; break;
            case "emptyTargets": command.Targets = []; break;
            case "nullTarget": command.Targets = [null!]; break;
            case "duplicate": command.Targets = [target, target]; break;
            case "kindCase": command.Targets = [target with { Kind = "Record" }]; break;
            case "kindWhitespace": command.Targets = [target with { Kind = "record " }]; break;
            case "kindUnknown": command.Targets = [target with { Kind = "folder" }]; break;
            case "zeroTarget": command.Targets = [target with { Id = Guid.Empty }]; break;
            default:
                envelope = mutation switch
                {
                    "nullEnvelope" => null!,
                    "format" => envelope with { Format = "other" },
                    "epoch" => envelope with { KeyEpoch = 0 },
                    "unsafeEpoch" => envelope with { KeyEpoch = 9007199254740992 },
                    "envelopeGrant" => envelope with { GrantId = Target },
                    "envelopeIdentity" => envelope with { RecipientIdentityId = Actor },
                    "envelopeKey" => envelope with { RecipientKeyFingerprint = Handle },
                    "envelopeRevision" => envelope with { GrantRevision = 2 },
                    "unsafeEnvelopeRevision" => envelope with { GrantRevision = 9007199254740992 },
                    "authorPin" => envelope with { AuthorKeyFingerprint = "bad" },
                    "enc" => envelope with { Enc = ProtectionFixture.Encode([4, .. new byte[64]]) },
                    "ciphertext" => envelope with { Ciphertext = "AQID=" },
                    "signature" => envelope with { Signature = "bad" },
                    _ => throw new ArgumentOutOfRangeException(nameof(mutation))
                };
                // Put invalid material second to catch validation of only the first target.
                command.Targets = [target, target with { Kind = "collection", KeyEnvelope = envelope }];
                break;
        }
        Assert.False(command.ToInput().IsValid());
        Assert.False(new GrantSoftwareAccessValidator().Validate(command).IsValid);
    }

    [UnitTheory]
    [InlineData(null, 1)]
    [InlineData(1L, 2)]
    [InlineData(8L, 9)]
    [InlineData(9007199254740990, 9007199254740991)]
    public void GivenValidRevisionAndMixedTargets_WhenParsing_ThenAcceptExactShape(long? expected, long revision)
    {
        var command = Command(expected, revision);
        command.Targets = [command.Targets[0], command.Targets[0] with { Kind = "collection" }];
        var body = JsonSerializer.Serialize(command, Json);
        var parsed = JsonSerializer.Deserialize<GrantSoftwareAccessCommand>(body, Json)!;
        var input = JsonSerializer.Deserialize<SoftwareGrantInput>(body, Json)!;
        Assert.True(parsed.ToInput().IsValid());
        Assert.True(input.IsValid());
        Assert.Equal(expected, parsed.ExpectedRevision);
        AssertFields(parsed, ["expectedRevision", "grantId", "recipientKey", "softwareIdentityId", "targets"]);
        AssertFields(input, ["expectedRevision", "grantId", "recipientKey", "softwareIdentityId", "targets"]);
        AssertFields(parsed.RecipientKey, ["crv", "kty", "x", "y"]);
        AssertFields(parsed.Targets[0], ["id", "keyEnvelope", "kind"]);
    }

    [UnitFact]
    public void GivenStructurallyValidEnvelope_WhenEpochOrSignatureRequiresStoreContext_ThenDeferNativeVerification()
    {
        var command = Command();
        command.Targets[0] = command.Targets[0] with
        {
            KeyEnvelope = command.Targets[0].KeyEnvelope with { KeyEpoch = 123, Signature = ProtectionFixture.Encode(new byte[64]) }
        };
        Assert.True(command.ToInput().IsValid());
    }

    // Authority and validation take precedence over external identity lookup and persistence.
    [UnitTheory]
    [InlineData("actor", "authentication_required")]
    [InlineData("missingAccess", "vault_access_required")]
    [InlineData("emptyAccess", "validation_failed")]
    [InlineData("malformedAccess", "validation_failed")]
    [InlineData("input", "validation_failed")]
    public async Task GivenInvalidBoundary_WhenHandling_ThenRejectBeforeIdentityLookup(string mutation, string error)
    {
        var command = Command();
        if (mutation is "actor" or "missingAccess" or "input") command.Targets = null!;
        command.SetContext(mutation == "actor" ? Guid.Empty : Actor, mutation switch
        {
            "actor" or "missingAccess" => null,
            "emptyAccess" => "",
            "malformedAccess" => "bad",
            _ => Handle
        });
        var result = await Handler(new Mock<ISoftwareGrantStore>(MockBehavior.Strict).Object,
            new Mock<IHeimdallClient>(MockBehavior.Strict).Object).HandleAsync(command);
        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { error }, result.Errors);
        Assert.Equal(error == "validation_failed" ? 400 : 401, SoftwareGrantMessages.StatusCodes[error]);
    }

    [UnitTheory]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAB")]
    [InlineData(" AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA ")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task GivenNoncanonicalAccessHandle_WhenHandlingValidInput_ThenRejectBeforeDependencies(string access)
    {
        var command = Command();
        command.SetContext(Actor, access);
        var result = await Handler(new Mock<ISoftwareGrantStore>(MockBehavior.Strict).Object,
            new Mock<IHeimdallClient>(MockBehavior.Strict).Object).HandleAsync(command);
        Assert.Equal(new[] { "validation_failed" }, result.Errors);
        Assert.Null(result.Data);
    }

    [UnitTheory]
    [InlineData(HeimdallApplicationStatus.NotFound, "not_found", 404)]
    [InlineData(HeimdallApplicationStatus.Unavailable, "identity_unavailable", 503)]
    [InlineData((HeimdallApplicationStatus)999, "identity_unavailable", 503)]
    public async Task GivenNonActiveSoftwareIdentity_WhenHandling_ThenRejectBeforeStore(
        HeimdallApplicationStatus status, string error, int code)
    {
        var result = await Handler(new Mock<ISoftwareGrantStore>(MockBehavior.Strict).Object, Heimdall(status).Object)
            .HandleAsync(Command());
        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { error }, result.Errors);
        Assert.Equal(code, SoftwareGrantMessages.StatusCodes[error]);
    }

    [UnitTheory]
    [InlineData(null, 1)]
    [InlineData(1L, 2)]
    [InlineData(8L, 9)]
    [InlineData(9007199254740990, 9007199254740991)]
    public async Task GivenActiveIdentity_WhenHandling_ThenForwardExactRequestAndReturnOnlyGrantAndRevision(long? expected, long revision)
    {
        var command = Command(expected, revision);
        command.Targets = [command.Targets[0], command.Targets[0] with { Kind = "collection" }];
        using var cancellation = new CancellationTokenSource();
        var lookedUp = false;
        var heimdall = new Mock<IHeimdallClient>(MockBehavior.Strict);
        heimdall.Setup(x => x.VerifyApplicationAsync(Identity, cancellation.Token)).Returns(() =>
        {
            lookedUp = true;
            return Task.FromResult(HeimdallApplicationStatus.Active);
        });
        SoftwareGrantRequest? received = null;
        var store = new Mock<ISoftwareGrantStore>(MockBehavior.Strict);
        store.Setup(x => x.GrantAsync(It.IsAny<SoftwareGrantRequest>(), cancellation.Token))
            .Returns<SoftwareGrantRequest, CancellationToken>((request, _) =>
            {
                Assert.True(lookedUp);
                received = request;
                return Task.FromResult(new VaultResult<SoftwareGrantDetails>(new(Grant, revision)));
            });
        var result = await Handler(store.Object, heimdall.Object).HandleAsync(command, cancellation.Token);
        Assert.True(result.Success);
        Assert.Empty(result.Errors);
        Assert.Equal(new[] { "software_access_granted" }, result.Messages);
        Assert.Equal(Grant, result.Data!.GrantId);
        Assert.Equal(revision, result.Data.Revision);
        AssertFields(result.Data, ["grantId", "revision"]);
        Assert.Equal(Actor, received!.Actor);
        Assert.Equal("66687aadf862bd776c8fc18b8e9f8e20089714856ee233b3902a591d0d5f2925", received.AccessVerifier);
        Assert.Equal(Grant, received.Input.GrantId);
        Assert.Equal(Identity, received.Input.SoftwareIdentityId);
        Assert.Equal(expected, received.Input.ExpectedRevision);
        Assert.Equal(command.RecipientKey, received.Input.RecipientKey);
        Assert.Equal(command.Targets, received.Input.Targets);
        Assert.Equal(201, SoftwareGrantMessages.StatusCodes["software_access_granted"]);
    }

    [UnitTheory]
    [InlineData("validation_failed", "validation_failed", 400)]
    [InlineData("vault_access_denied", "vault_access_denied", 403)]
    [InlineData("not_found", "not_found", 404)]
    [InlineData("revision_conflict", "revision_conflict", 409)]
    [InlineData("persistence_unavailable", "persistence_unavailable", 503)]
    [InlineData("secret_target_123", "persistence_unavailable", 503)]
    [InlineData("authentication_required", "persistence_unavailable", 503)]
    [InlineData("vault_access_required", "persistence_unavailable", 503)]
    [InlineData("identity_unavailable", "persistence_unavailable", 503)]
    [InlineData("software_access_granted", "persistence_unavailable", 503)]
    [InlineData("", "persistence_unavailable", 503)]
    public async Task GivenStoreError_WhenHandling_ThenAllowlistSafeErrorOnly(string error, string expected, int code)
    {
        var result = await Handler(Store(new(Error: error)).Object).HandleAsync(Command());
        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { expected }, result.Errors);
        Assert.Equal(code, SoftwareGrantMessages.StatusCodes[expected]);
    }

    [UnitTheory]
    [InlineData("nullResult")]
    [InlineData("nullData")]
    [InlineData("wrongGrant")]
    [InlineData("zeroGrant")]
    [InlineData("wrongRevision")]
    [InlineData("zeroRevision")]
    [InlineData("negativeRevision")]
    [InlineData("unsafeRevision")]
    [InlineData("errorWithData")]
    public async Task GivenMalformedStoreSuccess_WhenHandling_Then503WithoutPayload(string mutation)
    {
        SoftwareGrantDetails? details = mutation switch
        {
            "nullData" => null,
            "wrongGrant" => new(Target, 1),
            "zeroGrant" => new(Guid.Empty, 1),
            "wrongRevision" => new(Grant, 2),
            "zeroRevision" => new(Grant, 0),
            "negativeRevision" => new(Grant, -1),
            "unsafeRevision" => new(Grant, 9007199254740992),
            _ => new(Grant, 1)
        };
        var store = Store(mutation == "nullResult" ? null! : new(details,
            mutation == "errorWithData" ? "not_found" : null));
        var result = await Handler(store.Object).HandleAsync(Command());
        Assert.False(result.Success);
        Assert.Null(result.Data);
        Assert.Equal(new[] { "persistence_unavailable" }, result.Errors);
    }

    [UnitTheory]
    [InlineData("caller")]
    [InlineData("heimdall")]
    [InlineData("store")]
    public async Task GivenCancellation_WhenHandling_ThenPropagateExactCallerToken(string origin)
    {
        using var cancellation = new CancellationTokenSource();
        var command = Command();
        var heimdall = new Mock<IHeimdallClient>(MockBehavior.Strict);
        var store = new Mock<ISoftwareGrantStore>(MockBehavior.Strict);
        if (origin == "caller")
        {
            cancellation.Cancel();
            command.SetContext(Guid.Empty, null);
        }
        else if (origin == "heimdall")
            heimdall.Setup(x => x.VerifyApplicationAsync(Identity, cancellation.Token))
                .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        else
        {
            heimdall.Setup(x => x.VerifyApplicationAsync(Identity, cancellation.Token)).ReturnsAsync(HeimdallApplicationStatus.Active);
            store.Setup(x => x.GrantAsync(It.IsAny<SoftwareGrantRequest>(), cancellation.Token))
                .ThrowsAsync(new OperationCanceledException(cancellation.Token));
        }
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Handler(store.Object, heimdall.Object).HandleAsync(command, cancellation.Token));
        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    private static GrantSoftwareAccessHandler Handler(ISoftwareGrantStore store, IHeimdallClient? heimdall = null) =>
        new(new GrantSoftwareAccessValidator(), store, heimdall ?? Heimdall(HeimdallApplicationStatus.Active).Object);

    private static Mock<IHeimdallClient> Heimdall(HeimdallApplicationStatus status)
    {
        var heimdall = new Mock<IHeimdallClient>(MockBehavior.Strict);
        heimdall.Setup(x => x.VerifyApplicationAsync(Identity, It.IsAny<CancellationToken>())).ReturnsAsync(status);
        return heimdall;
    }

    private static Mock<ISoftwareGrantStore> Store(VaultResult<SoftwareGrantDetails> result)
    {
        var store = new Mock<ISoftwareGrantStore>(MockBehavior.Strict);
        store.Setup(x => x.GrantAsync(It.IsAny<SoftwareGrantRequest>(), It.IsAny<CancellationToken>())).ReturnsAsync(result);
        return store;
    }

    private static GrantSoftwareAccessCommand Command(long? expected = null, long revision = 1)
    {
        using var fixture = new ProtectionFixture();
        var key = fixture.Material.RecipientKey;
        var command = new GrantSoftwareAccessCommand
        {
            GrantId = Grant,
            SoftwareIdentityId = Identity,
            ExpectedRevision = expected,
            RecipientKey = new(key.Crv, key.Kty, key.X, key.Y),
            Targets = [new("record", Target, fixture.Wrap(Actor, "record", Target, Identity, revision: revision) with { GrantId = Grant })]
        };
        command.SetContext(Actor, Handle);
        return command;
    }

    private static JsonObject Body() => JsonSerializer.SerializeToNode(Command(), Json)!.AsObject();
    private static JsonObject Node(JsonObject body, string path) => path switch
    {
        "body" => body,
        "key" => body["recipientKey"]!.AsObject(),
        "target" => body["targets"]![0]!.AsObject(),
        "envelope" => body["targets"]![0]!["keyEnvelope"]!.AsObject(),
        _ => throw new ArgumentOutOfRangeException(nameof(path))
    };

    private static void AssertRejected(string body)
    {
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<GrantSoftwareAccessCommand>(body, Json));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<SoftwareGrantInput>(body, Json));
    }

    private static void AssertFields<T>(T value, string[] fields)
    {
        using var json = JsonDocument.Parse(JsonSerializer.Serialize(value, Json));
        Assert.Equal(fields, json.RootElement.EnumerateObject().Select(x => x.Name).Order());
    }
}
