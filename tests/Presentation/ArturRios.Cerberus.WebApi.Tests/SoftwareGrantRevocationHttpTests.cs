using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Jwt;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi;
using Swashbuckle.AspNetCore.Swagger;

namespace ArturRios.Cerberus.WebApi.Tests;

public sealed class SoftwareGrantRevocationContractTests
{
    // Break: documenting a widened body/response, incorrect UUID input, or omitted safe status.
    [UnitFact]
    public void GivenSoftwareRevocationRoute_WhenGeneratingOpenApi_ThenExactDeleteContract()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        { ApplicationName = typeof(Startup).Assembly.GetName().Name,Args = [] });
        builder.Services.AddCerberusContracts(); using var host = builder.Build();
        var document = host.Services.GetRequiredService<ISwaggerProvider>().GetSwagger("v1");
        using var writer = new StringWriter(); document.SerializeAsV3(new OpenApiJsonWriter(writer));
        using var json = JsonDocument.Parse(writer.ToString()); var root = json.RootElement;
        var operation = root.GetProperty("paths").GetProperty("/api/software-grants/{id}").GetProperty("delete");
        var path = Assert.Single(operation.GetProperty("parameters").EnumerateArray(),x => x.GetProperty("in").GetString() == "path");
        Assert.Equal("id",path.GetProperty("name").GetString()); Assert.True(path.GetProperty("required").GetBoolean());
        Assert.Equal("uuid",path.GetProperty("schema").GetProperty("format").GetString());
        var body = operation.GetProperty("requestBody"); Assert.True(body.GetProperty("required").GetBoolean());
        Assert.Equal("application/json",Assert.Single(body.GetProperty("content").EnumerateObject()).Name);
        var schemas = root.GetProperty("components").GetProperty("schemas");
        var command = schemas.GetProperty("RevokeSoftwareAccessCommand");
        Assert.Equal(new[] { "expectedRevision" },command.GetProperty("properties").EnumerateObject().Select(x => x.Name));
        Assert.Equal(new[] { "expectedRevision" },command.GetProperty("required").EnumerateArray().Select(x => x.GetString()));
        Assert.False(command.GetProperty("additionalProperties").GetBoolean());
        Assert.Equal("integer",command.GetProperty("properties").GetProperty("expectedRevision").GetProperty("type").GetString());
        Assert.Equal(new[] { "grantId","revision" },schemas.GetProperty("RevokeSoftwareAccessOutput").GetProperty("properties").EnumerateObject().Select(x => x.Name).Order());
        var responses = operation.GetProperty("responses");
        Assert.Equal(new[] { "200","400","401","403","404","409","413","503" },responses.EnumerateObject().Select(x => x.Name).Order());
        Assert.EndsWith("/RevokeSoftwareAccessOutputDataOutput",responses.GetProperty("200").GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        foreach (var status in new[] { "400","401","403","404","409","503" })
            Assert.EndsWith("/ProcessOutput",responses.GetProperty(status).GetProperty("content").GetProperty("application/json").GetProperty("schema").GetProperty("$ref").GetString());
        Assert.False(responses.GetProperty("413").TryGetProperty("content",out _));
    }
}

[Collection("Registration host")]
public sealed class SoftwareGrantRevocationHttpTests : IDisposable
{
    private readonly RegistrationApiFixture fixture;
    private readonly WebApplicationFactory<Program> host;
    private (HttpClient Client, TestServer Server) Gateway { get; }

    public SoftwareGrantRevocationHttpTests(RegistrationApiFixture fixture)
    {
        this.fixture = fixture;
        host = new WebApplicationFactory<Program>().WithWebHostBuilder(builder => builder.UseEnvironment("local"));
        Gateway = (host.CreateClient(),host.Server);
    }

    public void Dispose() { Gateway.Client.Dispose(); host.Dispose(); }

    // Break: missing route, retained recipient material, widened output or unrelated vault mutation.
    [FunctionalFact]
    public async Task GivenOwnedActiveGrant_WhenDeleting_Then200ExactOutputAndFullyReducedAggregate()
    {
        var s = await Setup(); var before = await Snapshot(s, excludeRevokedGrant: true);
        await using var db = fixture.Context();
        var old = await db.SoftwareGrants.AsNoTracking().SingleAsync(x => x.PublicId == s.Grant);
        var identityCalls = fixture.IdentityCalls;
        using var request = Request(s); using var response = await Gateway.Client.SendAsync(request);
        await Success(response, s);
        Assert.Equal(identityCalls + 1,fixture.IdentityCalls);
        var saved = await db.SoftwareGrants.AsNoTracking().SingleAsync(x => x.PublicId == s.Grant);
        Assert.Equal(old.Id, saved.Id); Assert.Equal(old.AccountId, saved.AccountId);
        Assert.Equal(old.HeimdallSoftwarePublicId, saved.HeimdallSoftwarePublicId);
        Assert.Equal(SoftwareGrantState.Revoked, saved.State); Assert.Equal(2, saved.Revision);
        Assert.Empty(saved.RecipientKey); Assert.NotEqual(old.ConcurrencyStamp, saved.ConcurrencyStamp);
        Assert.NotEqual(Guid.Empty, saved.ConcurrencyStamp);
        Assert.Empty(await db.SoftwareGrantRecords.Where(x => x.GrantId == saved.Id).ToArrayAsync());
        Assert.Empty(await db.SoftwareGrantCollections.Where(x => x.GrantId == saved.Id).ToArrayAsync());
        Assert.Equal(before, await Snapshot(s, excludeRevokedGrant: true));
    }

    // Break: accepting forged transport/identity or mutating a grant before visible input is valid.
    [FunctionalTheory]
    [InlineData("query",400)][InlineData("duplicateHeader",400)][InlineData("malformedHeader",400)]
    [InlineData("missingHeader",401)][InlineData("empty",400)][InlineData("oversized",413)]
    [InlineData("noMedia",400)][InlineData("text",400)][InlineData("suffix",400)][InlineData("prefix",400)]
    [InlineData("parameter",400)][InlineData("charset",400)][InlineData("duplicateCharset",400)][InlineData("brokenMedia",400)]
    [InlineData("compression",400)][InlineData("bom",400)][InlineData("utf8",400)]
    [InlineData("noToken",401)][InlineData("invalidToken",401)][InlineData("zeroActor",401)][InlineData("scope",401)]
    [InlineData("missingActor",401)][InlineData("malformedActor",401)]
    [InlineData("upperRoute",400)][InlineData("bracedRoute",400)][InlineData("compactRoute",400)]
    [InlineData("zeroRoute",400)][InlineData("malformedRoute",400)]
    public async Task GivenRejectedTransportOrIdentity_WhenDeleting_ThenSafeFailureAndNoMutation(string kind, int status)
    {
        var s = await Setup(); var before = await Snapshot(s);
        byte[] body = kind switch
        {
            "empty" => [], "oversized" => Encoding.UTF8.GetBytes(new string(' ',65537)),
            "utf8" => [0xc3,0x28], _ => Encoding.UTF8.GetBytes("{\"expectedRevision\":1}")
        };
        if (kind == "bom") body = [239,187,191,..body];
        var id = kind switch
        {
            "upperRoute" => s.Grant.ToString("D").ToUpperInvariant(), "bracedRoute" => s.Grant.ToString("B"),
            "compactRoute" => s.Grant.ToString("N"), "zeroRoute" => Guid.Empty.ToString("D"),
            "malformedRoute" => "malformed", _ => s.Grant.ToString("D")
        };
        using var request = Request(s,body,id);
        if (kind == "query") request.RequestUri = new("/api/software-grants/" + id + "?expectedRevision=1",UriKind.Relative);
        if (kind is "missingHeader" or "malformedHeader") request.Headers.Remove("X-Cerberus-Vault-Access");
        if (kind == "malformedHeader") request.Headers.Add("X-Cerberus-Vault-Access","bad");
        if (kind == "duplicateHeader") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",s.Access);
        if (kind == "noToken") request.Headers.Authorization = null;
        if (kind == "invalidToken") request.Headers.Authorization = new("Bearer","invalid");
        if (kind is "zeroActor" or "scope") request.Headers.Authorization = new("Bearer",RegistrationApiFixture.Token(
            kind == "zeroActor" ? Guid.Empty : s.Actor,kind == "scope" ? Guid.NewGuid() : null));
        if (kind is "missingActor" or "malformedActor")
        {
            var claims = new Dictionary<string,string> { ["roleId"] = "3",["scopeId"] = RegistrationApiFixture.Scope.ToString() };
            if (kind == "malformedActor") claims["id"] = "malformed";
            request.Headers.Authorization = new("Bearer",new JwtHandler().CreateToken(new JwtConfiguration(60,
                "fixture-issuer","fixture-audience","fixture-only-signing-key-32-characters",claims)));
        }
        if (kind == "compression") request.Content!.Headers.ContentEncoding.Add("gzip");
        if (kind is "noMedia" or "text" or "suffix" or "prefix" or "parameter" or "charset" or "duplicateCharset" or "brokenMedia")
        {
            request.Content!.Headers.Remove("Content-Type");
            if (kind != "noMedia") request.Content.Headers.TryAddWithoutValidation("Content-Type",kind switch
            {
                "text" => "text/json", "suffix" => "application/problem+json", "prefix" => "application/json-forged",
                "parameter" => "application/json; boundary=forged", "charset" => "application/json; charset=utf-16",
                "duplicateCharset" => "application/json; charset=utf-8; charset=utf-8", _ => "application/json; charset=\"utf-8"
            });
        }
        using var response = await Gateway.Client.SendAsync(request);
        await Failure(response,s,status,kind == "missingHeader" ? "vault_access_required" : status == 401 ? "authentication_required" : status == 400 ? "validation_failed" : null);
        Assert.Equal(before,await Snapshot(s));
    }

    // Break: widened JSON names/numbers or optional revision permits an unintended state transition.
    [FunctionalTheory]
    [InlineData("{}")][InlineData("null")][InlineData("[]")][InlineData("{")]
    [InlineData("{\"expectedRevision\":1,\"expectedRevision\":1}")]
    [InlineData("{\"expectedRevision\":1,\"actor\":\"forged\"}")]
    [InlineData("{\"ExpectedRevision\":1}")][InlineData("{\"expectedrevision\":1}")]
    [InlineData("{\"expectedRevision\":\"1\"}")][InlineData("{\"expectedRevision\":1.0}")]
    [InlineData("{\"expectedRevision\":1e0}")][InlineData("{\"expectedRevision\":null}")]
    [InlineData("{\"expectedRevision\":true}")][InlineData("{\"expectedRevision\":0}")]
    [InlineData("{\"expectedRevision\":-1}")][InlineData("{\"expectedRevision\":9007199254740991}")]
    [InlineData("{\"expectedRevision\":9007199254740992}")][InlineData("{\"expectedRevision\":9223372036854775808}")]
    public async Task GivenMalformedStrictBody_WhenDeleting_Then400WithoutMutation(string body)
    {
        var s = await Setup(); var before = await Snapshot(s);
        using var request = Request(s,Encoding.UTF8.GetBytes(body)); using var response = await Gateway.Client.SendAsync(request);
        await Failure(response,s,400,"validation_failed"); Assert.Equal(before,await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("application/json")][InlineData("Application/Json")]
    [InlineData("application/json; charset=utf-8")][InlineData("application/json; charset=\"UTF-8\"")]
    public async Task GivenAllowedMedia_WhenDeleting_Then200(string media)
    {
        var s = await Setup(); using var request = Request(s);
        request.Content!.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(media);
        using var response = await Gateway.Client.SendAsync(request); await Success(response,s);
    }

    // Break: unsafe status precedence, disclosing hidden ownership, or changing state on a denied request.
    [FunctionalTheory]
    [InlineData("missing",404)][InlineData("foreign",404)][InlineData("revoked",404)]
    [InlineData("terminal",404)][InlineData("inactive",404)][InlineData("accountTerminal",404)]
    [InlineData("expired",403)][InlineData("sessionRevoked",403)][InlineData("policy",403)]
    [InlineData("generation",403)][InlineData("sessionMissing",403)][InlineData("stale",409)]
    [InlineData("deletedIdentity",401)][InlineData("missingIdentity",401)][InlineData("identityUnavailable",503)]
    [InlineData("storedRevision",503)]
    public async Task GivenUnavailableAuthorityOrState_WhenDeleting_ThenExactSafeStatusAndNoMutation(string kind,int status)
    {
        var s = await Setup(); var actor = s; var id = s.Grant;
        await using var db = fixture.Context();
        if (kind == "missing") id = Guid.NewGuid();
        if (kind == "foreign") actor = await Setup();
        if (kind == "revoked") await db.SoftwareGrants.Where(x => x.PublicId == s.Grant).ExecuteUpdateAsync(x => x.SetProperty(g => g.State,SoftwareGrantState.Revoked));
        if (kind == "inactive") await db.Accounts.Where(x => x.Id == s.AccountId).ExecuteUpdateAsync(x => x.SetProperty(a => a.State,AccountState.ClosurePending));
        if (kind is "terminal" or "accountTerminal")
            db.TerminalErasures.Add(new() { ResourceKind = kind == "terminal" ? "grant" : "account",ResourceId = kind == "terminal" ? s.Grant : s.Account,DeletedAt = DateTimeOffset.UtcNow });
        if (kind is "expired" or "sessionRevoked" or "policy" or "generation")
        {
            var session = await db.VaultAccessSessions.SingleAsync(x => x.AccountId == s.AccountId);
            if (kind == "expired") session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            if (kind == "sessionRevoked") session.Revoked = true;
            if (kind == "policy") session.PolicyRevision++;
            if (kind == "generation") session.RevocationGeneration++;
        }
        if (kind == "sessionMissing") actor = s with { Access = ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)) };
        if (kind == "storedRevision") await db.SoftwareGrants.Where(x => x.PublicId == s.Grant).ExecuteUpdateAsync(x => x.SetProperty(g => g.Revision,0));
        if (kind is "deletedIdentity" or "identityUnavailable")
            fixture.Users[s.Actor + "@example.test"] = new(s.Actor,"fixture-password",Deleted:kind == "deletedIdentity",IdentityUnavailable:kind == "identityUnavailable");
        if (kind == "missingIdentity") fixture.Users.TryRemove(s.Actor + "@example.test",out _);
        await db.SaveChangesAsync(); var before = await Snapshot(s); var actorBefore = await Snapshot(actor);
        using var request = Request(actor,Encoding.UTF8.GetBytes(kind == "stale" ? "{\"expectedRevision\":2}" : "{\"expectedRevision\":1}"),id.ToString("D"));
        using var response = await Gateway.Client.SendAsync(request);
        await Failure(response,s,status,status switch
        { 401 => "authentication_required",403 => "vault_access_denied",404 => "not_found",409 => "revision_conflict",_ => kind == "identityUnavailable" ? "identity_unavailable" : "persistence_unavailable" });
        NoDisclosure(await response.Content.ReadAsStringAsync(),actor);
        Assert.Equal(before,await Snapshot(s)); Assert.Equal(actorBefore,await Snapshot(actor));
    }

    // Break: storage exceptions leaking details or committing any partial erase.
    [FunctionalTheory][InlineData("update")][InlineData("recordDelete")][InlineData("collectionDelete")]
    public async Task GivenPersistenceFault_WhenDeleting_Then503RollbackAndCleanRetry(string kind)
    {
        var s = await Setup(); var before = await Snapshot(s); await using var db = fixture.Context();
        var table = kind == "update" ? "software_grant" : kind == "recordDelete" ? "software_grant_record" : "software_grant_collection";
        var operation = kind == "update" ? "UPDATE" : "DELETE";
        // Identifiers are selected only from the literal table/operation allowlist above.
        var install = $"CREATE FUNCTION cerberus.software_revoke_http_fault() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private fixture fault'; END $$; CREATE TRIGGER software_revoke_http_fault AFTER {operation} ON cerberus.{table} FOR EACH ROW EXECUTE FUNCTION cerberus.software_revoke_http_fault()";
        await db.Database.ExecuteSqlRawAsync(install);
        try
        {
            using var request = Request(s); using var response = await Gateway.Client.SendAsync(request);
            await Failure(response,s,503,"persistence_unavailable");
            Assert.DoesNotContain("private fixture fault",await response.Content.ReadAsStringAsync());
        }
        finally
        {
            var remove = $"DROP TRIGGER IF EXISTS software_revoke_http_fault ON cerberus.{table}; DROP FUNCTION IF EXISTS cerberus.software_revoke_http_fault()";
            await db.Database.ExecuteSqlRawAsync(remove);
        }
        Assert.Equal(before,await Snapshot(s));
        using var retry = Request(s); using var retried = await Gateway.Client.SendAsync(retry); await Success(retried,s);
    }

    // Break: confusing a present empty raw header with an absent header.
    [FunctionalFact]
    public async Task GivenRawEmptyVaultHeader_WhenDeleting_Then400WithoutMutation()
    {
        var s = await Setup(); var before = await Snapshot(s); var body = Encoding.UTF8.GetBytes("{\"expectedRevision\":1}");
        var context = await Gateway.Server.SendAsync(c =>
        {
            c.Request.Method = "DELETE"; c.Request.Path = "/api/software-grants/" + s.Grant.ToString("D");
            c.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(s.Actor);
            c.Request.Headers["X-Cerberus-Vault-Access"] = "";
            c.Request.ContentType = "application/json"; c.Request.ContentLength = body.Length; c.Request.Body = new MemoryStream(body);
        });
        Assert.Equal(400,context.Response.StatusCode); Assert.Equal("no-store",context.Response.Headers.CacheControl.ToString());
        using var reader = new StreamReader(context.Response.Body); var raw = await reader.ReadToEndAsync(); NoDisclosure(raw,s);
        using var doc = JsonDocument.Parse(raw); Assert.Equal(new[] { "validation_failed" },doc.RootElement.GetProperty("errors").EnumerateArray().Select(x => x.GetString()));
        Assert.Equal(before,await Snapshot(s));
    }

    private sealed record State(Guid Actor, Guid Account, long AccountId, string Access, Guid Grant,
        Guid Software, Guid Record, Guid Collection, string Key, string Envelope, string Ciphertext);

    private async Task<State> Setup()
    {
        await using var db = fixture.Context();
        var actor = Guid.NewGuid(); var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = actor };
        fixture.Users[actor + "@example.test"] = new(actor, "fixture-password");
        db.Accounts.Add(account); await db.SaveChangesAsync();
        var access = ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32));
        Assert.True(OpaqueAccessHandle.TryHash(access, out var verifier));
        var key = "private-recipient-key-" + Guid.NewGuid(); var envelope = "private-key-envelope-" + Guid.NewGuid();
        var ciphertext = "private-ciphertext-" + Guid.NewGuid();
        var record = new VaultRecord { PublicId = Guid.NewGuid(), AccountId = account.Id, Envelope = Encoding.UTF8.GetBytes(ciphertext) };
        var collection = new VaultCollection { PublicId = Guid.NewGuid(), AccountId = account.Id, Envelope = Encoding.UTF8.GetBytes(ciphertext) };
        var grant = new SoftwareGrant { PublicId = Guid.NewGuid(), AccountId = account.Id,
            HeimdallSoftwarePublicId = Guid.NewGuid(), RecipientKey = Encoding.UTF8.GetBytes(key) };
        db.VaultAccessSessions.Add(new() { AccountId = account.Id, HandleVerifier = verifier,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1),
            PolicyRevision = 1, RevocationGeneration = 1 });
        db.Records.Add(record); db.Collections.Add(collection); db.SoftwareGrants.Add(grant);
        db.SoftwareGrants.Add(new() { PublicId = Guid.NewGuid(), AccountId = account.Id,
            HeimdallSoftwarePublicId = Guid.NewGuid(), RecipientKey = Encoding.UTF8.GetBytes(key) });
        await db.SaveChangesAsync();
        db.SoftwareGrantRecords.Add(new() { AccountId = account.Id, GrantId = grant.Id, RecordId = record.Id,
            RecipientKeyEnvelope = Encoding.UTF8.GetBytes(envelope) });
        db.SoftwareGrantCollections.Add(new() { AccountId = account.Id, GrantId = grant.Id, CollectionId = collection.Id,
            RecipientKeyEnvelope = Encoding.UTF8.GetBytes(envelope) });
        await db.SaveChangesAsync();
        return new(actor, account.PublicId, account.Id, access, grant.PublicId, grant.HeimdallSoftwarePublicId,
            record.PublicId, collection.PublicId, key, envelope, ciphertext);
    }

    private static HttpRequestMessage Request(State s, byte[]? body = null, string? id = null)
    {
        var request = new HttpRequestMessage(HttpMethod.Delete, "/api/software-grants/" + (id ?? s.Grant.ToString("D")))
        { Content = new ByteArrayContent(body ?? Encoding.UTF8.GetBytes("{\"expectedRevision\":1}")) };
        request.Content.Headers.ContentType = new("application/json");
        request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor));
        request.Headers.Add("X-Cerberus-Vault-Access", s.Access); return request;
    }

    private static void NoDisclosure(string raw, State s)
    {
        foreach (var field in new[] { "softwareIdentity", "recipientKey", "keyEnvelope", "targets", "record", "collection", "ciphertext" })
            Assert.DoesNotContain(field, raw, StringComparison.OrdinalIgnoreCase);
        foreach (var secret in new[] { s.Actor.ToString(), s.Account.ToString(), s.Software.ToString(), s.Record.ToString(),
            s.Collection.ToString(), s.Access, s.Key, s.Envelope, s.Ciphertext,
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s.Key)), Convert.ToBase64String(Encoding.UTF8.GetBytes(s.Envelope)),
            Convert.ToBase64String(Encoding.UTF8.GetBytes(s.Ciphertext)) }) Assert.DoesNotContain(secret, raw);
    }

    private static async Task Success(HttpResponseMessage response, State s)
    {
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var raw = await response.Content.ReadAsStringAsync(); NoDisclosure(raw, s);
        using var doc = JsonDocument.Parse(raw); var root = doc.RootElement;
        Assert.Equal(new[] { "data", "errors", "messages", "success", "timestamp" }, root.EnumerateObject().Select(x => x.Name).Order());
        Assert.True(root.GetProperty("success").GetBoolean()); Assert.Empty(root.GetProperty("errors").EnumerateArray());
        Assert.Equal(new[] { "software_access_revoked" }, root.GetProperty("messages").EnumerateArray().Select(x => x.GetString()));
        var data = root.GetProperty("data");
        Assert.Equal(new[] { "grantId", "revision" }, data.EnumerateObject().Select(x => x.Name).Order());
        Assert.Equal(s.Grant, data.GetProperty("grantId").GetGuid()); Assert.Equal(2, data.GetProperty("revision").GetInt64());
    }

    private static async Task Failure(HttpResponseMessage response, State s, int status, string? error)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        var raw = await response.Content.ReadAsStringAsync(); NoDisclosure(raw, s);
        if (status == 413) { Assert.Empty(raw); return; }
        using var doc = JsonDocument.Parse(raw); var root = doc.RootElement;
        Assert.False(root.GetProperty("success").GetBoolean());
        Assert.Equal(new[] { error }, root.GetProperty("errors").EnumerateArray().Select(x => x.GetString()));
        Assert.Empty(root.GetProperty("messages").EnumerateArray());
        if (root.TryGetProperty("data", out var data)) Assert.Equal(JsonValueKind.Null, data.ValueKind);
    }

    private async Task<string> Snapshot(State s, bool excludeRevokedGrant = false)
    {
        await using var db = fixture.Context();
        return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
              'account',(SELECT to_jsonb(a) FROM cerberus.account a WHERE id={s.AccountId}),
              'sessions',(SELECT jsonb_agg(to_jsonb(v) ORDER BY id) FROM cerberus.vault_access_session v WHERE account_id={s.AccountId}),
              'records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.record r WHERE account_id={s.AccountId}),
              'collections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY id) FROM cerberus.collection c WHERE account_id={s.AccountId}),
              'grants',(SELECT jsonb_agg(to_jsonb(g) ORDER BY id) FROM cerberus.software_grant g WHERE account_id={s.AccountId} AND (NOT {excludeRevokedGrant} OR public_id<>{s.Grant})),
              'grantRecords',(SELECT jsonb_agg(to_jsonb(r) ORDER BY r.grant_id,r.record_id) FROM cerberus.software_grant_record r JOIN cerberus.software_grant g ON g.id=r.grant_id WHERE g.account_id={s.AccountId} AND (NOT {excludeRevokedGrant} OR g.public_id<>{s.Grant})),
              'grantCollections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY c.grant_id,c.collection_id) FROM cerberus.software_grant_collection c JOIN cerberus.software_grant g ON g.id=c.grant_id WHERE g.account_id={s.AccountId} AND (NOT {excludeRevokedGrant} OR g.public_id<>{s.Grant})))::text AS "Value"
            """).SingleAsync();
    }
}
