using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Sync;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class SyncEditsHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenDiscriminatorAfterOrdinaryFields_WhenUploading_ThenAcceptEquivalentJsonPropertyOrder()
    {
        var s = await Setup(); Authorize(RegistrationApiFixture.Token(s.Actor)); var node = System.Text.Json.Nodes.JsonNode.Parse(s.Body)!;
        var item = node["edits"]![0]!.AsObject(); var type = item["editType"]!.GetValue<string>(); item.Remove("editType"); item["editType"] = type;
        using var request = Request(s, node.ToJsonString()); using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); await using var db = fixture.Context(); Assert.True(await db.Records.AnyAsync(x => x.PublicId == s.Record));
    }
    [FunctionalTheory]
    [InlineData("missing")][InlineData("empty")][InlineData("null")][InlineData("null-item")]
    [InlineData("unknown")][InlineData("duplicate")][InlineData("case")][InlineData("type")]
    [InlineData("missing-operation")][InlineData("invalid-operation")][InlineData("missing-input")][InlineData("missing-type")]
    [InlineData("duplicate-operation")][InlineData("unknown-input")][InlineData("unknown-edit")]
    [InlineData("string-number")][InlineData("offset")][InlineData("noncanonical")][InlineData("too-many")]
    public async Task GivenInvalidStrictBatch_WhenPosting_ThenRejectWithoutResourceOrOperation(string invalid)
    {
        var s = await Setup(); Authorize(RegistrationApiFixture.Token(s.Actor));
        var node = System.Text.Json.Nodes.JsonNode.Parse(s.Body)!; var edits = node["edits"]!.AsArray(); var item = edits[0]!;
        switch (invalid)
        {
            case "missing": node.AsObject().Remove("edits"); break;
            case "empty": node["edits"] = new System.Text.Json.Nodes.JsonArray(); break;
            case "null": node["edits"] = null; break;
            case "null-item": edits[0] = null; break;
            case "unknown": node["actor"] = s.Actor.ToString("D"); break;
            case "case": node["Edits"] = edits.DeepClone(); node.AsObject().Remove("edits"); break;
            case "type": item["editType"] = "record-delete"; break;
            case "missing-type": item.AsObject().Remove("editType"); break;
            case "missing-operation": item.AsObject().Remove("operationId"); break;
            case "invalid-operation": item["operationId"] = Guid.Empty.ToString("D"); break;
            case "missing-input": item.AsObject().Remove("input"); break;
            case "duplicate-operation": edits.Add(item.DeepClone()); break;
            case "unknown-input": item["input"]!["accountId"] = s.Actor.ToString("D"); break;
            case "unknown-edit": item["OfflineLatestEdit"] = true; break;
            case "string-number": item["input"]!["envelope"]!["keyEpoch"] = "1"; break;
            case "offset": item["input"]!["editedAt"] = "2026-01-01T01:00:00+01:00"; break;
            case "noncanonical": item["operationId"] = Guid.NewGuid().ToString("B"); break;
            case "too-many": for (var n = 0; n < 100; n++) { var copy = item.DeepClone(); copy["operationId"] = Guid.NewGuid().ToString("D"); edits.Add(copy); } break;
        }
        var body = invalid == "duplicate" ? "{\"edits\":[],\"edits\":[]}" : node.ToJsonString();
        using var request = Request(s, body); using var response = await Gateway.Client.SendAsync(request);
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        await Unchanged(s);
    }
    [FunctionalTheory]
    [InlineData("noIdentity", HttpStatusCode.Unauthorized)][InlineData("noHandle", HttpStatusCode.Unauthorized)]
    [InlineData("handle", HttpStatusCode.BadRequest)][InlineData("duplicate", HttpStatusCode.BadRequest)]
    [InlineData("query", HttpStatusCode.BadRequest)][InlineData("compression", HttpStatusCode.BadRequest)]
    [InlineData("media", HttpStatusCode.BadRequest)][InlineData("charset", HttpStatusCode.BadRequest)]
    [InlineData("parameter", HttpStatusCode.BadRequest)][InlineData("bom", HttpStatusCode.BadRequest)]
    [InlineData("empty", HttpStatusCode.BadRequest)][InlineData("size", HttpStatusCode.RequestEntityTooLarge)]
    public async Task GivenInvalidTransport_WhenUploading_ThenReturnSafeErrorAndNoMutation(string invalid, HttpStatusCode status)
    {
        var s = await Setup(); if (invalid != "noIdentity") Authorize(RegistrationApiFixture.Token(s.Actor));
        using var request = Request(s, invalid == "empty" ? "" : invalid == "size" ? new string(' ', 65537) : (invalid == "bom" ? "\uFEFF" : "") + s.Body);
        if (invalid == "noHandle") request.Headers.Remove("X-Cerberus-Vault-Access");
        if (invalid == "handle") { request.Headers.Remove("X-Cerberus-Vault-Access"); request.Headers.Add("X-Cerberus-Vault-Access", "invalid"); }
        if (invalid == "duplicate") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", s.Access);
        if (invalid == "query") request.RequestUri = new("/api/sync/edits?pageSize=1", UriKind.Relative);
        if (invalid == "compression") request.Content!.Headers.ContentEncoding.Add("gzip");
        if (invalid == "media") request.Content!.Headers.ContentType = new("application/cerberus+json");
        if (invalid == "charset") request.Content!.Headers.ContentType!.CharSet = "utf-16";
        if (invalid == "parameter") request.Content!.Headers.ContentType!.Parameters.Add(new("extra", "value"));
        using var response = await Gateway.Client.SendAsync(request); Assert.Equal(status, response.StatusCode);
        Assert.True(response.Headers.CacheControl?.NoStore); await Unchanged(s);
    }
    private sealed record State(Guid Actor, long Account, Guid Record, string Access, string Body);
    private async Task<State> Setup()
    {
        var actor = Guid.NewGuid(); fixture.Users[Guid.NewGuid() + "@example.test"] = new(actor, "fixture-password");
        var access = ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32)); Assert.True(OpaqueAccessHandle.TryHash(access, out var verifier));
        await using var db = fixture.Context(); var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = actor };
        db.Accounts.Add(account); await db.SaveChangesAsync(); db.VaultAccessSessions.Add(new() { AccountId = account.Id, HandleVerifier = verifier,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 }); await db.SaveChangesAsync();
        var id = Guid.NewGuid(); var envelope = new EncryptedEnvelope("cerberus-content-v1", 1, ProtectionFixture.Encode(new byte[32]), ProtectionFixture.Encode(new byte[12]), "AQID", ProtectionFixture.Encode(new byte[16]));
        var body = JsonSerializer.Serialize(new SyncEditsInput([new SyncRecordCreate(Guid.NewGuid(), new(id, envelope, DateTimeOffset.FromUnixTimeSeconds(1000), []))]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        return new(actor, account.Id, id, access, body);
    }
    private static HttpRequestMessage Request(State s, string body)
    {
        var r = new HttpRequestMessage(HttpMethod.Post, "/api/sync/edits") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        r.Headers.Add("X-Cerberus-Vault-Access", s.Access); return r;
    }
    private async Task Unchanged(State s)
    {
        await using var db = fixture.Context(); Assert.False(await db.Records.AnyAsync(x => x.PublicId == s.Record));
        Assert.False(await db.SyncOperations.AnyAsync(x => x.AccountId == s.Account));
    }
    [FunctionalFact]
    public async Task GivenCurrentAccountAccessAndEncryptedCreate_WhenPostingThenReplaying_ThenReturnStableOutcomeWithoutPayload()
    {
        var actor = Guid.NewGuid(); fixture.Users[Guid.NewGuid() + "@example.test"] = new(actor, "fixture-password");
        var access = ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32)); Assert.True(OpaqueAccessHandle.TryHash(access, out var verifier));
        await using var db = fixture.Context(); var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = actor };
        db.Accounts.Add(account); await db.SaveChangesAsync();
        db.VaultAccessSessions.Add(new() { AccountId = account.Id, HandleVerifier = verifier, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 }); await db.SaveChangesAsync();
        var id = Guid.NewGuid(); var operation = Guid.NewGuid(); var envelope = new EncryptedEnvelope("cerberus-content-v1", 1,
            ProtectionFixture.Encode(new byte[32]), ProtectionFixture.Encode(new byte[12]), "AQID", ProtectionFixture.Encode(new byte[16]));
        var body = JsonSerializer.Serialize(new SyncEditsInput([new SyncRecordCreate(operation, new RecordCreateInput(id, envelope, DateTimeOffset.FromUnixTimeSeconds(1000), []))]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Authorize(RegistrationApiFixture.Token(actor)); string? first = null;
        var directory = Path.Combine(AppContext.BaseDirectory, "sync-edit-contract-fixtures"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "create-request.json"), body);
        for (var n = 0; n < 2; n++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, "/api/sync/edits") { Content = new StringContent(body, Encoding.UTF8, "application/json") };
            request.Headers.Add("X-Cerberus-Vault-Access", access); using var response = await Gateway.Client.SendAsync(request);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
            var raw = await response.Content.ReadAsStringAsync(); Assert.DoesNotContain("ciphertext", raw); Assert.DoesNotContain("envelope", raw);
            if (n == 0) await File.WriteAllTextAsync(Path.Combine(directory, "applied.json"), raw);
            using var json = JsonDocument.Parse(raw); var outcome = Assert.Single(json.RootElement.GetProperty("data").GetProperty("outcomes").EnumerateArray());
            Assert.Equal(operation, outcome.GetProperty("operationId").GetGuid()); Assert.Equal(id, outcome.GetProperty("resourceId").GetGuid());
            Assert.Equal("applied", outcome.GetProperty("status").GetString()); Assert.Equal(1, outcome.GetProperty("revision").GetInt64());
            if (first is null) first = outcome.GetRawText(); else Assert.Equal(first, outcome.GetRawText());
        }
        Assert.Equal(1, await db.Records.CountAsync(x => x.PublicId == id));
        var older = new SyncRecordUpdate(Guid.NewGuid(), new(id, envelope, DateTimeOffset.FromUnixTimeSeconds(999)));
        var hidden = new SyncRecordUpdate(Guid.NewGuid(), new(Guid.NewGuid(), envelope, DateTimeOffset.FromUnixTimeSeconds(1001)));
        var nextBody = JsonSerializer.Serialize(new SyncEditsInput([older, hidden]), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/sync/edits") { Content = new StringContent(nextBody, Encoding.UTF8, "application/json") })
        {
            request.Headers.Add("X-Cerberus-Vault-Access", access); using var response = await Gateway.Client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(raw); var outcomes = json.RootElement.GetProperty("data").GetProperty("outcomes");
            Assert.Equal("superseded", outcomes[0].GetProperty("status").GetString()); Assert.Equal("denied", outcomes[1].GetProperty("status").GetString());
            Assert.Equal(JsonValueKind.Null, outcomes[1].GetProperty("revision").ValueKind);
            await File.WriteAllTextAsync(Path.Combine(directory, "update-request.json"), nextBody); await File.WriteAllTextAsync(Path.Combine(directory, "superseded-denied.json"), raw);
        }
        db.TerminalErasures.Add(new() { ResourceKind = "record", ResourceId = id, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
        await db.Records.Where(x => x.PublicId == id).ExecuteDeleteAsync();
        using (var request = new HttpRequestMessage(HttpMethod.Post, "/api/sync/edits") { Content = new StringContent(body, Encoding.UTF8, "application/json") })
        {
            request.Headers.Add("X-Cerberus-Vault-Access", access); using var response = await Gateway.Client.SendAsync(request); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var raw = await response.Content.ReadAsStringAsync(); using var json = JsonDocument.Parse(raw);
            Assert.Equal("permanently-deleted", json.RootElement.GetProperty("data").GetProperty("outcomes")[0].GetProperty("status").GetString());
            await File.WriteAllTextAsync(Path.Combine(directory, "terminal.json"), raw);
        }
    }
}
