using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Command.Records;
using ArturRios.Cerberus.Command.Software;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Data;
using ArturRios.Cerberus.Data.Software;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public sealed class SoftwareGrantHttpTests(RegistrationApiFixture fixture)
{
    // Only the external HTTP service is doubled: JWT validation, middleware,
    // mediator, native signatures and PostgreSQL writes remain real.
    private sealed class IdentityHandler : HttpMessageHandler
    {
        public int ApplicationCalls { get; private set; }
        public int PersonCalls { get; private set; }
        public string ApplicationState { get; set; } = "active";
        public string PersonState { get; set; } = "active";
        public string? PersonBearer { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = request.RequestUri!.AbsolutePath;
            var id = Guid.Parse(path.Split('/')[^1]);
            object data;
            if (path.StartsWith("/api/persons/", StringComparison.Ordinal))
            {
                PersonCalls++; PersonBearer = request.Headers.Authorization!.Parameter;
                if (PersonState == "unavailable") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable));
                data = new
                {
                    id,
                    name = "Fixture Owner",
                    email = "fixture@example.test",
                    role = 3,
                    scopeId = RegistrationApiFixture.Scope,
                    isDeleted = PersonState == "deleted",
                    emailVerified = true,
                    twoFactorEnabled = false,
                    ownedScopeIds = Array.Empty<Guid>()
                };
            }
            else
            {
                ApplicationCalls++;
                Assert.Equal($"/api/scopes/{RegistrationApiFixture.Scope:D}/applications/{id:D}", path);
                Assert.NotEqual(PersonBearer, request.Headers.Authorization!.Parameter);
                if (ApplicationState is "missing" or "unavailable") return Task.FromResult(new HttpResponseMessage(ApplicationState == "missing" ? HttpStatusCode.NotFound : HttpStatusCode.ServiceUnavailable));
                if (ApplicationState == "malformed") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{\"private\":true}", Encoding.UTF8, "application/json") });
                data = new
                {
                    id,
                    name = "Fixture Application",
                    scopeId = RegistrationApiFixture.Scope,
                    ownerId = Guid.Parse("527a1001-8ef5-4c9b-a565-333333333333"),
                    isDeleted = ApplicationState == "deleted",
                    createdAt = DateTime.UnixEpoch,
                    updatedAt = DateTime.UnixEpoch
                };
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Bytes(new { success = true, errors = Array.Empty<string>(), messages = Array.Empty<string>(), data }))
                { Headers = { ContentType = new("application/json") } }
            });
        }
    }

    private sealed class StoreCounter { public int Calls; }
    private sealed class CountedStore(ISoftwareGrantStore inner, StoreCounter counter) : ISoftwareGrantStore
    {
        public Task<VaultResult<SoftwareGrantDetails>> GrantAsync(SoftwareGrantRequest request, CancellationToken cancellationToken)
        { counter.Calls++; return inner.GrantAsync(request, cancellationToken); }
    }
    private static WebApplicationFactory<Program> Host(IdentityHandler identity, StoreCounter? counter = null) => new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.ConfigureServices(services =>
    {
        services.RemoveAll<IHeimdallClient>();
        services.AddScoped<IHeimdallClient>(sp => new HeimdallClient(new HttpClient(identity, false) { BaseAddress = new("http://fixture.test") },
            sp.GetRequiredService<CerberusOptions>(), sp.GetRequiredService<HeimdallTokenValidator>()));
        if (counter is not null)
        {
            services.RemoveAll<ISoftwareGrantStore>();
            services.AddScoped<ISoftwareGrantStore>(sp => new CountedStore(new SoftwareGrantStore(sp.GetRequiredService<IDbContextFactory<AppDbContext>>()), counter));
        }
    }));

    [FunctionalFact]
    public async Task GivenNativeOwnerAndTwoEnvelopes_WhenGranting_Then201ExactAggregateAndNoVaultMutation()
    {
        using var identity = new IdentityHandler(); using var host = Host(identity); using var client = host.CreateClient();
        using var owner = new ProtectionFixture(); using var author = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var recipient = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var state = await Setup(owner, author); var record = await Record(client, state); var collection = await Collection(client, state);
        var input = Input(state, author, recipient, record, collection); var before = await Vault(state);
        using var request = Request(state, Bytes(input)); using var response = await client.SendAsync(request);
        var output = await Success<GrantSoftwareAccessOutput>(response, 201);
        Assert.Equal(input.GrantId, output.GrantId); Assert.Equal(1, output.Revision);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        Assert.Equal(request.Headers.Authorization!.Parameter, identity.PersonBearer);
        Assert.Equal(1, identity.ApplicationCalls);
        await using var db = fixture.Context(); var grant = await db.SoftwareGrants.AsNoTracking().SingleAsync(x => x.PublicId == input.GrantId);
        Assert.Equal(state.InternalId, grant.AccountId); Assert.Equal(input.SoftwareIdentityId, grant.HeimdallSoftwarePublicId);
        Assert.Equal(SoftwareGrantState.Active, grant.State); Assert.Equal(1, grant.Revision); Assert.Equal(Bytes(input.RecipientKey), grant.RecipientKey);
        var r = Assert.Single(await db.SoftwareGrantRecords.Where(x => x.GrantId == grant.Id).ToArrayAsync());
        var c = Assert.Single(await db.SoftwareGrantCollections.Where(x => x.GrantId == grant.Id).ToArrayAsync());
        Assert.Equal(await db.Records.Where(x => x.PublicId == record).Select(x => x.Id).SingleAsync(), r.RecordId);
        Assert.Equal(await db.Collections.Where(x => x.PublicId == collection).Select(x => x.Id).SingleAsync(), c.CollectionId);
        Assert.Equal(Bytes(input.Targets[0].KeyEnvelope), r.RecipientKeyEnvelope); Assert.Equal(Bytes(input.Targets[1].KeyEnvelope), c.RecipientKeyEnvelope);
        Assert.Equal(before, await Vault(state));
    }

    [FunctionalTheory]
    [InlineData("query", 400)]
    [InlineData("duplicateHeader", 400)]
    [InlineData("badHeader", 400)]
    [InlineData("missingHeader", 401)]
    [InlineData("empty", 400)]
    [InlineData("oversized", 413)]
    [InlineData("noMedia", 400)]
    [InlineData("text", 400)]
    [InlineData("suffix", 400)]
    [InlineData("prefix", 400)]
    [InlineData("parameter", 400)]
    [InlineData("charset", 400)]
    [InlineData("duplicateCharset", 400)]
    [InlineData("brokenMedia", 400)]
    [InlineData("compression", 400)]
    [InlineData("bom", 400)]
    [InlineData("utf8", 400)]
    [InlineData("noToken", 401)]
    [InlineData("invalidToken", 401)]
    [InlineData("scope", 401)]
    [InlineData("zeroActor", 401)]
    public async Task GivenRejectedTransportOrAuthentication_WhenPosting_ThenNoApplicationOrStoreCall(string kind, int status)
    {
        using var identity = new IdentityHandler(); var counter = new StoreCounter(); using var host = Host(identity, counter); using var client = host.CreateClient();
        using var owner = new ProtectionFixture(); using var author = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var recipient = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = await Setup(owner, author); var input = Input(s, author, recipient, Guid.NewGuid(), Guid.NewGuid()); var before = await Vault(s);
        var raw = kind == "empty" ? [] : kind == "oversized" ? Encoding.UTF8.GetBytes(new string(' ', 65537)) : kind == "utf8" ? new byte[] { 0xc3, 0x28 } : Bytes(input);
        if (kind == "bom") raw = [239, 187, 191, .. raw];
        using var request = Request(s, raw, "/api/software-grants" + (kind == "query" ? "?actor=forged" : ""));
        if (kind is "missingHeader" or "badHeader") request.Headers.Remove("X-Cerberus-Vault-Access");
        if (kind == "badHeader") request.Headers.Add("X-Cerberus-Vault-Access", "bad");
        if (kind == "duplicateHeader") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", s.Access);
        if (kind == "noToken") request.Headers.Authorization = null;
        if (kind == "invalidToken") request.Headers.Authorization = new("Bearer", "invalid");
        if (kind is "scope" or "zeroActor") request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(kind == "zeroActor" ? Guid.Empty : s.Actor, kind == "scope" ? Guid.NewGuid() : null));
        if (kind == "compression") request.Content!.Headers.ContentEncoding.Add("gzip");
        if (kind is "noMedia" or "text" or "suffix" or "prefix" or "parameter" or "charset" or "brokenMedia" or "duplicateCharset")
        {
            request.Content!.Headers.Remove("Content-Type");
            if (kind != "noMedia") request.Content.Headers.TryAddWithoutValidation("Content-Type", kind switch
            { "text" => "text/json", "suffix" => "application/problem+json", "prefix" => "application/json-forged", "parameter" => "application/json; boundary=forged", "charset" => "application/json; charset=utf-16", "duplicateCharset" => "application/json; charset=utf-8; charset=utf-8", _ => "application/json; charset=\"utf-8" });
        }
        using var response = await client.SendAsync(request); await Failure(response, status, kind == "missingHeader" ? "vault_access_required" : status == 401 ? "authentication_required" : status == 400 ? "validation_failed" : null);
        Assert.Equal(0, identity.ApplicationCalls); Assert.Equal(0, counter.Calls); Assert.Equal(before, await Vault(s));
        if (kind is "noToken" or "invalidToken" or "scope" or "zeroActor") Assert.Equal(0, identity.PersonCalls);
        await Absent(input.GrantId);
    }

    [FunctionalTheory]
    [InlineData("application/json")]
    [InlineData("Application/Json")]
    [InlineData("application/json; charset=utf-8")]
    [InlineData("application/json; charset=\"UTF-8\"")]
    public async Task GivenAllowedMediaType_WhenPosting_ThenCreateGrant(string media)
    {
        using var identity = new IdentityHandler(); using var host = Host(identity); using var client = host.CreateClient();
        using var owner = new ProtectionFixture(); using var author = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var recipient = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = await Setup(owner, author); var input = Input(s, author, recipient, await Record(client, s), await Collection(client, s));
        using var request = Request(s, Bytes(input)); request.Content!.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(media);
        using var response = await client.SendAsync(request); await Success<GrantSoftwareAccessOutput>(response, 201);
    }

    [FunctionalTheory]
    [InlineData("unknownRoot")]
    [InlineData("context")]
    [InlineData("caseRoot")]
    [InlineData("missingRevision")]
    [InlineData("duplicateRoot")]
    [InlineData("unknownKey")]
    [InlineData("privateKey")]
    [InlineData("caseKey")]
    [InlineData("duplicateKey")]
    [InlineData("nullKey")]
    [InlineData("unknownTarget")]
    [InlineData("caseTarget")]
    [InlineData("duplicateTargetField")]
    [InlineData("emptyTargets")]
    [InlineData("duplicateTargets")]
    [InlineData("nullTarget")]
    [InlineData("badKind")]
    [InlineData("unknownEnvelope")]
    [InlineData("caseEnvelope")]
    [InlineData("duplicateEnvelope")]
    [InlineData("nullEnvelope")]
    [InlineData("upperUuid")]
    [InlineData("compactUuid")]
    [InlineData("bracedUuid")]
    [InlineData("zeroUuid")]
    [InlineData("targetUuid")]
    [InlineData("envelopeUuid")]
    [InlineData("stringRevision")]
    [InlineData("decimalRevision")]
    [InlineData("exponentRevision")]
    [InlineData("zeroRevision")]
    [InlineData("negativeRevision")]
    [InlineData("maxRevision")]
    [InlineData("unsafeRevision")]
    [InlineData("decimalEpoch")]
    [InlineData("exponentEpoch")]
    [InlineData("stringEpoch")]
    [InlineData("zeroEpoch")]
    [InlineData("unsafeEpoch")]
    [InlineData("grantMismatch")]
    [InlineData("identityMismatch")]
    [InlineData("revisionMismatch")]
    [InlineData("recipientMismatch")]
    public async Task GivenMalformedStrictInput_WhenPosting_Then400BeforeApplicationOrStore(string kind)
    {
        using var identity = new IdentityHandler(); var counter = new StoreCounter(); using var host = Host(identity, counter); using var client = host.CreateClient();
        using var owner = new ProtectionFixture(); using var author = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var recipient = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = await Setup(owner, author); var input = Input(s, author, recipient, Guid.NewGuid(), Guid.NewGuid()); var before = await Vault(s);
        var body = JsonSerializer.SerializeToNode(input, ProtectionFixture.Json)!.AsObject(); var key = body["recipientKey"]!.AsObject(); var target = body["targets"]![0]!.AsObject(); var envelope = target["keyEnvelope"]!.AsObject();
        switch (kind)
        {
            case "unknownRoot": body["plaintext"] = "secret"; break;
            case "context": body["actor"] = s.Actor.ToString(); break;
            case "caseRoot": body.Remove("grantId"); body["GrantId"] = input.GrantId.ToString(); break;
            case "missingRevision": body.Remove("expectedRevision"); break;
            case "unknownKey": key["owner"] = "forged"; break;
            case "privateKey": key["d"] = "secret"; break;
            case "caseKey": key.Remove("crv"); key["Crv"] = "P-256"; break;
            case "nullKey": body["recipientKey"] = null; break;
            case "unknownTarget": target["ownerId"] = s.AccountId.ToString(); break;
            case "caseTarget": target.Remove("kind"); target["Kind"] = "record"; break;
            case "emptyTargets": body["targets"] = new JsonArray(); break;
            case "duplicateTargets": body["targets"]![1] = target.DeepClone(); break;
            case "nullTarget": body["targets"]![0] = null; break;
            case "badKind": target["kind"] = "folder"; break;
            case "unknownEnvelope": envelope["key"] = "secret"; break;
            case "caseEnvelope": envelope.Remove("keyEpoch"); envelope["KeyEpoch"] = 1; break;
            case "nullEnvelope": target["keyEnvelope"] = null; break;
            case "upperUuid": body["grantId"] = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA"; break;
            case "compactUuid": body["softwareIdentityId"] = input.SoftwareIdentityId.ToString("N"); break;
            case "bracedUuid": body["grantId"] = input.GrantId.ToString("B"); break;
            case "zeroUuid": body["grantId"] = Guid.Empty.ToString(); break;
            case "targetUuid": target["id"] = input.Targets[0].Id.ToString("N"); break;
            case "envelopeUuid": envelope["grantId"] = input.GrantId.ToString("N"); break;
            case "stringRevision": body["expectedRevision"] = "1"; break;
            case "decimalRevision": body["expectedRevision"] = JsonNode.Parse("1.0"); break;
            case "exponentRevision": body["expectedRevision"] = JsonNode.Parse("1e0"); break;
            case "zeroRevision": body["expectedRevision"] = 0; break;
            case "negativeRevision": body["expectedRevision"] = -1; break;
            case "maxRevision": body["expectedRevision"] = 9007199254740991L; break;
            case "unsafeRevision": body["expectedRevision"] = 9007199254740992L; break;
            case "decimalEpoch": envelope["keyEpoch"] = JsonNode.Parse("1.0"); break;
            case "exponentEpoch": envelope["keyEpoch"] = JsonNode.Parse("1e0"); break;
            case "stringEpoch": envelope["keyEpoch"] = "1"; break;
            case "zeroEpoch": envelope["keyEpoch"] = 0; break;
            case "unsafeEpoch": envelope["keyEpoch"] = 9007199254740992L; break;
            case "grantMismatch": envelope["grantId"] = Guid.NewGuid().ToString(); break;
            case "identityMismatch": envelope["recipientIdentityId"] = Guid.NewGuid().ToString(); break;
            case "revisionMismatch": envelope["grantRevision"] = 2; break;
            case "recipientMismatch": envelope["recipientKeyFingerprint"] = ProtectionFixture.Public(author).Fingerprint(); break;
        }
        var raw = body.ToJsonString();
        raw = kind switch
        {
            "duplicateRoot" => raw.Replace("\"expectedRevision\":null", "\"expectedRevision\":null,\"expectedRevision\":null"),
            "duplicateKey" => raw.Replace("\"crv\":\"P-256\"", "\"crv\":\"P-256\",\"crv\":\"P-256\""),
            "duplicateTargetField" => raw.Replace("\"kind\":\"record\"", "\"kind\":\"record\",\"kind\":\"record\""),
            "duplicateEnvelope" => raw.Replace("\"keyEpoch\":1", "\"keyEpoch\":1,\"keyEpoch\":1"),
            _ => raw
        };
        using var request = Request(s, Encoding.UTF8.GetBytes(raw)); using var response = await client.SendAsync(request); await Failure(response, 400, "validation_failed");
        Assert.Equal(0, identity.ApplicationCalls); Assert.Equal(0, counter.Calls); Assert.Equal(before, await Vault(s)); await Absent(input.GrantId);
    }

    [FunctionalTheory]
    [InlineData("missing", 404)]
    [InlineData("deleted", 404)]
    [InlineData("malformed", 503)]
    [InlineData("unavailable", 503)]
    public async Task GivenNonActiveApplication_WhenPosting_ThenNoStoreAndSafeFailure(string kind, int status)
    {
        using var identity = new IdentityHandler { ApplicationState = kind }; var counter = new StoreCounter(); using var host = Host(identity, counter); using var client = host.CreateClient();
        using var owner = new ProtectionFixture(); using var author = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var recipient = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = await Setup(owner, author); var input = Input(s, author, recipient, Guid.NewGuid(), Guid.NewGuid()); var before = await Vault(s);
        using var request = Request(s, Bytes(input)); using var response = await client.SendAsync(request); await Failure(response, status, status == 404 ? "not_found" : "identity_unavailable");
        Assert.Equal(1, identity.ApplicationCalls); Assert.Equal(0, counter.Calls); Assert.Equal(before, await Vault(s)); await Absent(input.GrantId);
    }

    [FunctionalFact]
    public async Task GivenActiveThenRevokedGrant_WhenPosting_ThenConflictOrReplaceExactAggregateOnce()
    {
        using var identity = new IdentityHandler(); using var host = Host(identity); using var client = host.CreateClient();
        using var owner = new ProtectionFixture(); using var author = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var recipient = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var nextRecipient = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = await Setup(owner, author); var input = Input(s, author, recipient, await Record(client, s), await Collection(client, s));
        using (var request = Request(s, Bytes(input))) using (var response = await client.SendAsync(request)) await Success<GrantSoftwareAccessOutput>(response, 201);
        var winner = await Aggregate(input.GrantId); var before = await Vault(s);
        using (var request = Request(s, Bytes(input))) using (var response = await client.SendAsync(request)) await Failure(response, 409, "revision_conflict");
        Assert.Equal(winner, await Aggregate(input.GrantId)); Assert.Equal(before, await Vault(s));
        await using var db = fixture.Context(); var original = await db.SoftwareGrants.AsNoTracking().SingleAsync(x => x.PublicId == input.GrantId);
        await db.SoftwareGrants.Where(x => x.Id == original.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, SoftwareGrantState.Revoked));
        var next = Input(s, author, nextRecipient, await Record(client, s), await Collection(client, s), input.GrantId, input.SoftwareIdentityId, 1); before = await Vault(s);
        using (var request = Request(s, Bytes(next))) using (var response = await client.SendAsync(request))
        { var output = await Success<GrantSoftwareAccessOutput>(response, 201); Assert.Equal(input.GrantId, output.GrantId); Assert.Equal(2, output.Revision); }
        var saved = await db.SoftwareGrants.AsNoTracking().SingleAsync(x => x.PublicId == input.GrantId);
        Assert.Equal(original.Id, saved.Id); Assert.Equal(original.AccountId, saved.AccountId); Assert.Equal(original.HeimdallSoftwarePublicId, saved.HeimdallSoftwarePublicId);
        Assert.Equal(2, saved.Revision); Assert.NotEqual(original.ConcurrencyStamp, saved.ConcurrencyStamp); Assert.Equal(SoftwareGrantState.Active, saved.State); Assert.Equal(Bytes(next.RecipientKey), saved.RecipientKey);
        var r = Assert.Single(await db.SoftwareGrantRecords.Where(x => x.GrantId == saved.Id).ToArrayAsync()); var c = Assert.Single(await db.SoftwareGrantCollections.Where(x => x.GrantId == saved.Id).ToArrayAsync());
        Assert.Equal(await db.Records.Where(x => x.PublicId == next.Targets[0].Id).Select(x => x.Id).SingleAsync(), r.RecordId);
        Assert.Equal(await db.Collections.Where(x => x.PublicId == next.Targets[1].Id).Select(x => x.Id).SingleAsync(), c.CollectionId);
        Assert.Equal(Bytes(next.Targets[0].KeyEnvelope), r.RecipientKeyEnvelope); Assert.Equal(Bytes(next.Targets[1].KeyEnvelope), c.RecipientKeyEnvelope);
        Assert.Equal(before, await Vault(s)); winner = await Aggregate(input.GrantId);
        using (var request = Request(s, Bytes(next))) using (var response = await client.SendAsync(request)) await Failure(response, 409, "revision_conflict");
        Assert.Equal(winner, await Aggregate(input.GrantId));
    }

    [FunctionalTheory]
    [InlineData("signature", 400)]
    [InlineData("epoch", 400)]
    [InlineData("author", 400)]
    [InlineData("trashed", 404)]
    [InlineData("terminal", 404)]
    [InlineData("missing", 404)]
    [InlineData("session", 403)]
    [InlineData("storedEnvelope", 503)]
    [InlineData("database", 503)]
    [InlineData("childWrite", 503)]
    public async Task GivenCurrentTargetOrPersistenceFailure_WhenPosting_ThenNoPartialGrantAndSafeStatus(string kind, int status)
    {
        using var identity = new IdentityHandler(); using var host = Host(identity); using var client = host.CreateClient();
        using var owner = new ProtectionFixture(); using var author = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var recipient = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = await Setup(owner, author); var record = await Record(client, s); var collection = await Collection(client, s); var input = Input(s, author, recipient, record, collection);
        var target = input.Targets[0];
        if (kind is "signature" or "epoch" or "author") input.Targets[0] = target with
        {
            KeyEnvelope = kind switch
            { "signature" => target.KeyEnvelope with { Signature = ProtectionFixture.Encode(new byte[64]) }, "epoch" => target.KeyEnvelope with { KeyEpoch = 2 }, _ => target.KeyEnvelope with { AuthorKeyFingerprint = ProtectionFixture.Public(recipient).Fingerprint() } }
        };
        if (kind == "missing") input.Targets[0] = target with { Id = Guid.NewGuid() };
        await using var db = fixture.Context();
        if (kind == "trashed") await db.Records.Where(x => x.PublicId == record).ExecuteUpdateAsync(x => x.SetProperty(r => r.DeletedAt, DateTimeOffset.UtcNow));
        if (kind == "terminal") { db.TerminalErasures.Add(new() { ResourceId = record, ResourceKind = "record", DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        if (kind == "session") await db.VaultAccessSessions.Where(x => x.AccountId == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true));
        if (kind == "storedEnvelope") await db.Records.Where(x => x.PublicId == record).ExecuteUpdateAsync(x => x.SetProperty(r => r.Envelope, new byte[] { 1 }));
        var before = await Vault(s);
        if (kind == "database") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.software_grant RENAME TO fixture_unavailable_software_grant");
        if (kind == "childWrite") await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION cerberus.software_http_fault() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private fixture fault'; END $$; CREATE TRIGGER software_http_fault AFTER INSERT ON cerberus.software_grant_record FOR EACH ROW EXECUTE FUNCTION cerberus.software_http_fault()");
        try
        { using var request = Request(s, Bytes(input)); using var response = await client.SendAsync(request); await Failure(response, status, status switch { 400 => "validation_failed", 403 => "vault_access_denied", 404 => "not_found", _ => "persistence_unavailable" }); }
        finally
        {
            if (kind == "database") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_software_grant RENAME TO software_grant");
            if (kind == "childWrite") await db.Database.ExecuteSqlRawAsync("DROP TRIGGER software_http_fault ON cerberus.software_grant_record; DROP FUNCTION cerberus.software_http_fault()");
        }
        Assert.Equal(before, await Vault(s)); await Absent(input.GrantId);
        if (kind is "database" or "childWrite")
        { using var request = Request(s, Bytes(input)); using var response = await client.SendAsync(request); await Success<GrantSoftwareAccessOutput>(response, 201); }
    }

    [FunctionalTheory]
    [InlineData("deleted", 401)]
    [InlineData("unavailable", 503)]
    public async Task GivenRejectedCurrentHuman_WhenPosting_ThenNoApplicationOrStoreCall(string state, int status)
    {
        using var identity = new IdentityHandler { PersonState = state }; var counter = new StoreCounter(); using var host = Host(identity, counter); using var client = host.CreateClient();
        using var owner = new ProtectionFixture(); using var author = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var recipient = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = await Setup(owner, author); var input = Input(s, author, recipient, Guid.NewGuid(), Guid.NewGuid()); var before = await Vault(s);
        using var request = Request(s, Bytes(input)); using var response = await client.SendAsync(request); await Failure(response, status, status == 401 ? "authentication_required" : "identity_unavailable");
        Assert.Equal(1, identity.PersonCalls); Assert.Equal(0, identity.ApplicationCalls); Assert.Equal(0, counter.Calls); Assert.Equal(before, await Vault(s)); await Absent(input.GrantId);
    }

    [FunctionalTheory]
    [InlineData("Master", true)]
    [InlineData("PerProfile", true)]
    [InlineData("Master", false)]
    [InlineData("PerProfile", false)]
    public async Task GivenSelectedOwner_WhenPosting_ThenGrantOnlyExactSelectedTargets(string mode, bool linked)
    {
        using var identity = new IdentityHandler(); using var host = Host(identity); using var client = host.CreateClient();
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); using var author = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var recipient = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = await Setup(owner, author); var record = await Record(client, s); var collection = await Collection(client, s);
        var profileId = Guid.NewGuid();
        var profile = new ProfileCreateInput(profileId, Envelope(), new(mode, scoped.Material.UnlockVerifier,
            Signed(s, author, owner.Material.RecipientKey, "profile", profileId, profileId, s.Actor, 1), mode == "PerProfile" ? scoped.Material.PasswordWrapper : null),
            DateTimeOffset.UnixEpoch, linked ? [record] : [], [], linked ? [collection] : []);
        using (var request = Request(s, Bytes(profile), "/api/profiles")) using (var response = await client.SendAsync(request)) await Success<CreateProfileOutput>(response, 201);
        await using var db = fixture.Context(); var revision = await db.Profiles.Where(x => x.PublicId == profileId).Select(x => x.Revision).SingleAsync();
        var raw = Bytes(new { expectedRevision = revision });
        using var issue = Request(s, Bytes(new { expectedRevision = revision, requestHash = ProtocolBinary.Encode(SHA256.HashData(raw)) }), $"/api/profiles/{profileId}/access/challenges"); issue.Headers.Remove("X-Cerberus-Vault-Access");
        using var issued = await client.SendAsync(issue); var challenge = (await Success<IssueProfileAccessChallengeOutput>(issued, 201)).Challenge;
        using var open = Request(s, raw, $"/api/profiles/{profileId}/access"); open.Headers.Remove("X-Cerberus-Vault-Access"); open.Headers.Add("X-Cerberus-Challenge-Id", challenge.ChallengeId.ToString()); open.Headers.Add("X-Cerberus-Proof", scoped.Sign(challenge));
        using var opened = await client.SendAsync(open); var selected = s with { Access = (await Success<OpenProfileAccessOutput>(opened, 200)).VaultAccess };
        var input = Input(s, author, recipient, record, collection); var before = await Vault(s);
        using var post = Request(selected, Bytes(input)); using var result = await client.SendAsync(post);
        if (linked) await Success<GrantSoftwareAccessOutput>(result, 201); else { await Failure(result, 404, "not_found"); await Absent(input.GrantId); }
        Assert.Equal(before, await Vault(s));
        var session = await db.VaultAccessSessions.AsNoTracking().SingleAsync(x => x.HandleVerifier == Hash(selected.Access));
        Assert.Equal(await db.Profiles.Where(x => x.PublicId == profileId).Select(x => x.Id).SingleAsync(), session.ProfileId);
    }

    [FunctionalTheory]
    [InlineData("read-only", false)]
    [InlineData("read-write", false)]
    [InlineData("read-only", true)]
    [InlineData("read-write", true)]
    public async Task GivenVisibleForeignCollection_WhenRedelegating_ThenValidateNativeEvidenceBeforeDenial(string access, bool corrupt)
    {
        using var identity = new IdentityHandler(); using var host = Host(identity); using var client = host.CreateClient();
        using var owner = new ProtectionFixture(); using var foreign = new ProtectionFixture(); using var author = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var foreignAuthor = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var software = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = await Setup(owner, author); var r = await Setup(foreign, foreignAuthor); var collection = await Collection(client, s); var grantId = Guid.NewGuid();
        var native = new CollectionGrantInput(grantId, r.AccountId, null, access, Signed(s, author, foreign.Material.RecipientKey, "collection", collection, grantId, r.Actor, 1));
        using (var request = Request(s, Bytes(native), $"/api/collections/{collection}/grants")) using (var response = await client.SendAsync(request)) await Success<GrantCollectionAccessOutput>(response, 201);
        await using var db = fixture.Context();
        if (corrupt) await db.CollectionGrants.Where(x => x.PublicId == grantId).ExecuteUpdateAsync(x => x.SetProperty(g => g.RecipientKeyEnvelope, new byte[] { 1 }));
        var input = Input(r, foreignAuthor, software, Guid.NewGuid(), collection); input = input with { Targets = [input.Targets[1]] };
        var before = await Vault(r); var ownerBefore = await Vault(s); using var post = Request(r, Bytes(input)); using var result = await client.SendAsync(post);
        await Failure(result, corrupt ? 503 : 403, corrupt ? "persistence_unavailable" : "vault_access_denied"); await Absent(input.GrantId);
        Assert.Equal(before, await Vault(r)); Assert.Equal(ownerBefore, await Vault(s));
    }

    [FunctionalFact]
    public async Task GivenRawEmptyVaultHeader_WhenPosting_Then400BeforeApplicationOrStore()
    {
        using var identity = new IdentityHandler(); var counter = new StoreCounter(); using var host = Host(identity, counter);
        using var owner = new ProtectionFixture(); using var author = ECDsa.Create(ECCurve.NamedCurves.nistP256); using var recipient = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var s = await Setup(owner, author); var input = Input(s, author, recipient, Guid.NewGuid(), Guid.NewGuid()); var raw = Bytes(input);
        var context = await host.Server.SendAsync(c =>
        {
            c.Request.Method = "POST"; c.Request.Path = "/api/software-grants"; c.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(s.Actor);
            c.Request.Headers["X-Cerberus-Vault-Access"] = ""; c.Request.ContentType = "application/json"; c.Request.ContentLength = raw.Length; c.Request.Body = new MemoryStream(raw);
        });
        Assert.Equal(400, context.Response.StatusCode); Assert.Equal("no-store", context.Response.Headers.CacheControl.ToString());
        using var doc = await JsonDocument.ParseAsync(context.Response.Body); Assert.Equal("validation_failed", doc.RootElement.GetProperty("errors")[0].GetString());
        Assert.Equal(0, identity.ApplicationCalls); Assert.Equal(0, counter.Calls); await Absent(input.GrantId);
    }

    private static string Hash(string access) { Assert.True(OpaqueAccessHandle.TryHash(access, out var hash)); return hash; }
    private static RecipientEnvelope Signed(State s, ECDsa author, PublicJwk recipient, string kind, Guid target, Guid grant, Guid identity, long revision)
    {
        using var ephemeral = ECDsa.Create(ECCurve.NamedCurves.nistP256); var point = ephemeral.ExportParameters(false).Q;
        var envelope = new RecipientEnvelope("cerberus-recipient-wrap-v1", 1, grant, revision, identity, recipient.Fingerprint(), ProtectionFixture.Public(author).Fingerprint(),
            ProtectionFixture.Encode([4, .. point.X!, .. point.Y!]), ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(48)), "");
        return envelope with { Signature = ProtectionFixture.Encode(author.SignData(envelope.SigningBytes(s.AccountId, kind, target), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) };
    }
    private static async Task Failure(HttpResponseMessage response, int status, string? error)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); var raw = await response.Content.ReadAsStringAsync();
        if (status == 413) { Assert.Equal("", raw); return; }
        using var doc = JsonDocument.Parse(raw); Assert.False(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.Equal(new[] { error }, doc.RootElement.GetProperty("errors").EnumerateArray().Select(x => x.GetString()));
        if (doc.RootElement.TryGetProperty("data", out var data)) Assert.Equal(JsonValueKind.Null, data.ValueKind);
        Assert.DoesNotContain("private", raw); Assert.DoesNotContain("fixture", raw);
    }
    private async Task Absent(Guid id)
    { await using var db = fixture.Context(); Assert.False(await db.SoftwareGrants.AnyAsync(x => x.PublicId == id)); }
    private async Task<string> Aggregate(Guid id)
    {
        await using var db = fixture.Context();
        return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object('grant',(SELECT to_jsonb(g) FROM cerberus.software_grant g WHERE public_id={id}),
              'records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY record_id) FROM cerberus.software_grant_record r JOIN cerberus.software_grant g ON g.id=r.grant_id WHERE g.public_id={id}),
              'collections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY collection_id) FROM cerberus.software_grant_collection c JOIN cerberus.software_grant g ON g.id=c.grant_id WHERE g.public_id={id}))::text AS "Value"
            """).SingleAsync();
    }
    private sealed record State(Guid Actor, Guid AccountId, long InternalId, string Access);
    private async Task<State> Setup(ProtectionFixture owner, ECDsa author)
    {
        var actor = Guid.NewGuid(); await using var db = fixture.Context();
        var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = actor, DetailsEnvelope = Bytes(Envelope()) }; db.Accounts.Add(account); await db.SaveChangesAsync();
        var access = ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)); OpaqueAccessHandle.TryHash(access, out var verifier);
        db.VaultProtections.Add(new() { AccountId = account.Id, Material = Bytes(owner.Material with { AuthorKey = ProtectionFixture.Public(author) }) });
        db.VaultAccessSessions.Add(new() { AccountId = account.Id, HandleVerifier = verifier, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 });
        await db.SaveChangesAsync(); return new(actor, account.PublicId, account.Id, access);
    }
    private static SoftwareGrantInput Input(State s, ECDsa author, ECDsa recipient, Guid record, Guid collection, Guid? grant = null, Guid? identity = null, long? expected = null)
    {
        var id = grant ?? Guid.NewGuid(); var app = identity ?? Guid.NewGuid(); var key = ProtectionFixture.Public(recipient);
        return new(id, app, expected, new(key.Crv, key.Kty, key.X, key.Y), [Target("record", record), Target("collection", collection)]);
        SoftwareGrantTargetInput Target(string kind, Guid target)
        {
            using var ephemeral = ECDsa.Create(ECCurve.NamedCurves.nistP256); var p = ephemeral.ExportParameters(false).Q;
            var envelope = new RecipientEnvelope("cerberus-recipient-wrap-v1", 1, id, (expected ?? 0) + 1, app, key.Fingerprint(), ProtectionFixture.Public(author).Fingerprint(),
                ProtectionFixture.Encode([4, .. p.X!, .. p.Y!]), ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(48)), "");
            return new(kind, target, envelope with { Signature = ProtectionFixture.Encode(author.SignData(envelope.SigningBytes(s.AccountId, kind, target), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation)) });
        }
    }
    private static EncryptedEnvelope Envelope() => new("cerberus-content-v1", 1, ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(12)), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(73)), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(16)));
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ProtectionFixture.Json);
    private static HttpRequestMessage Request(State state, byte[] raw, string path = "/api/software-grants")
    {
        var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(raw) };
        request.Content.Headers.ContentType = new("application/json"); request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(state.Actor));
        request.Headers.Add("X-Cerberus-Vault-Access", state.Access); return request;
    }
    private static async Task<Guid> Record(HttpClient client, State state)
    {
        var input = new RecordCreateInput(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch, [], null);
        using var request = Request(state, Bytes(input), "/api/records"); using var response = await client.SendAsync(request); await Success<CreateRecordOutput>(response, 201); return input.RecordId;
    }
    private static async Task<Guid> Collection(HttpClient client, State state)
    {
        var input = new CollectionCreateInput(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch, [], [], []);
        using var request = Request(state, Bytes(input), "/api/collections"); using var response = await client.SendAsync(request); await Success<CreateCollectionOutput>(response, 201); return input.CollectionId;
    }
    private static async Task<T> Success<T>(HttpResponseMessage response, int status) where T : class
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var raw = await response.Content.ReadAsStringAsync(); using var doc = JsonDocument.Parse(raw); Assert.True(doc.RootElement.GetProperty("success").GetBoolean());
        if (typeof(T) == typeof(GrantSoftwareAccessOutput))
        {
            Assert.Equal(new[] { "data", "errors", "messages", "success", "timestamp" }, doc.RootElement.EnumerateObject().Select(x => x.Name).Order());
            Assert.Equal(new[] { "grantId", "revision" }, doc.RootElement.GetProperty("data").EnumerateObject().Select(x => x.Name).Order());
            Assert.Equal(new[] { "software_access_granted" }, doc.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetString()));
            Assert.Empty(doc.RootElement.GetProperty("errors").EnumerateArray());
        }
        return Assert.IsType<T>(JsonSerializer.Deserialize<DataOutput<T>>(raw, ProtectionFixture.Json)!.Data);
    }
    private async Task<string> Vault(State state)
    {
        await using var db = fixture.Context();
        return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object('account',(SELECT to_jsonb(a) FROM cerberus.account a WHERE id={state.InternalId}),
              'protection',(SELECT to_jsonb(p) FROM cerberus.vault_protection p WHERE account_id={state.InternalId}),
              'sessions',(SELECT jsonb_agg(to_jsonb(s) ORDER BY id) FROM cerberus.vault_access_session s WHERE account_id={state.InternalId}),
              'records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.record r WHERE account_id={state.InternalId}),
              'collections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY id) FROM cerberus.collection c WHERE account_id={state.InternalId}),
              'profiles',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM cerberus.profile p WHERE account_id={state.InternalId}),
              'folders',(SELECT jsonb_agg(to_jsonb(f) ORDER BY id) FROM cerberus.folder f WHERE account_id={state.InternalId}),
              'pr',(SELECT jsonb_agg(to_jsonb(p) ORDER BY profile_id,record_id) FROM cerberus.profile_record p WHERE account_id={state.InternalId}),
              'pf',(SELECT jsonb_agg(to_jsonb(p) ORDER BY profile_id,folder_id) FROM cerberus.profile_folder p WHERE account_id={state.InternalId}),
              'pc',(SELECT jsonb_agg(to_jsonb(p) ORDER BY profile_id,collection_id) FROM cerberus.profile_collection p JOIN cerberus.profile pr ON pr.id=p.profile_id WHERE pr.account_id={state.InternalId}),
              'cr',(SELECT jsonb_agg(to_jsonb(c) ORDER BY collection_id,record_id) FROM cerberus.collection_record c WHERE account_id={state.InternalId}),
              'cf',(SELECT jsonb_agg(to_jsonb(c) ORDER BY collection_id,folder_id) FROM cerberus.collection_folder c WHERE account_id={state.InternalId}),
              'sequence',(SELECT last_value FROM cerberus.server_sequence))::text AS "Value"
            """).SingleAsync();
    }
}
