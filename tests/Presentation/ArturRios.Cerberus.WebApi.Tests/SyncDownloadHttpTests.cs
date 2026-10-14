using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Util.Test.Functional;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class SyncDownloadHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenCurrentSelectedProfile_WhenStartingSynchronization_ThenReturnScopedSnapshotAndOpaqueContinuation()
    {
        var s = await Setup(); Authorize(RegistrationApiFixture.Token(s.Actor));
        using var response = await Send("pageSize=10", s.Access);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var data = json.RootElement.GetProperty("data");
        Assert.Equal(s.ProfileId, data.GetProperty("profileId").GetGuid()); Assert.True(data.GetProperty("clearScope").GetBoolean());
        Assert.False(data.GetProperty("resynchronizationRequired").GetBoolean()); Assert.False(data.GetProperty("hasMore").GetBoolean());
        var item = Assert.Single(data.GetProperty("items").EnumerateArray());
        Assert.Equal("profile", item.GetProperty("resourceKind").GetString()); Assert.Equal(s.ProfileId, item.GetProperty("resourceId").GetGuid());
        Assert.Equal("profile", item.GetProperty("data").GetProperty("resourceType").GetString());
        Assert.Equal("upsert", item.GetProperty("eventType").GetString()); Assert.True(item.GetProperty("sequence").GetInt64() > 0);
        var cursor = data.GetProperty("nextCursor").GetString()!; Assert.NotEmpty(cursor);
        using var next = await Send("pageSize=10&cursor=" + Uri.EscapeDataString(cursor), s.Access);
        Assert.Equal(HttpStatusCode.OK, next.StatusCode);
        using var delta = JsonDocument.Parse(await next.Content.ReadAsStringAsync()); Assert.Empty(delta.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
    }

    [FunctionalTheory]
    [InlineData("extra=1")][InlineData("Cursor=anything")][InlineData("pageSize=1&pageSize=2")]
    [InlineData("cursor=a&cursor=b")][InlineData("pageSize=01")][InlineData("pageSize=+1")]
    [InlineData("pageSize=-1")][InlineData("pageSize=0")][InlineData("pageSize=101")][InlineData("pageSize=garbage")]
    public async Task GivenUnsupportedQuery_WhenDownloading_ThenReturnStrictNoStoreValidationError(string query)
    {
        var s = await Setup(); Authorize(RegistrationApiFixture.Token(s.Actor));
        using var response = await Send(query, s.Access); await Failure(response, HttpStatusCode.BadRequest, "validation_failed");
    }
    [FunctionalTheory]
    [InlineData("body")][InlineData("duplicateHeader")][InlineData("invalidHandle")]
    public async Task GivenInvalidRequestShape_WhenDownloading_ThenRejectWithoutContent(string kind)
    {
        var s = await Setup(); Authorize(RegistrationApiFixture.Token(s.Actor));
        using var response = await Send("pageSize=10", kind == "invalidHandle" ? "bad" : s.Access, kind);
        await Failure(response, HttpStatusCode.BadRequest, "validation_failed");
    }
    [FunctionalTheory]
    [InlineData("identity", HttpStatusCode.Unauthorized)][InlineData("missing", HttpStatusCode.Unauthorized)]
    [InlineData("revoked", HttpStatusCode.Forbidden)][InlineData("accountwide", HttpStatusCode.Forbidden)]
    [InlineData("closing", HttpStatusCode.NotFound)]
    public async Task GivenMissingCurrentAuthority_WhenDownloading_ThenDenyWithoutCursor(string kind, HttpStatusCode status)
    {
        var s = await Setup(); if (kind != "identity") Authorize(RegistrationApiFixture.Token(s.Actor));
        await using var db = fixture.Context();
        if (kind == "revoked") await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true));
        if (kind == "accountwide") await db.VaultAccessSessions.Where(x => x.HandleVerifier == s.Verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.ProfileId, (long?)null));
        if (kind == "closing") await db.Accounts.Where(x => x.Id == s.Owner).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
        using var response = await Send("pageSize=10", kind == "missing" ? null : s.Access);
        await Failure(response, status);
    }
    [FunctionalTheory]
    [InlineData("")][InlineData("invalid")]
    public async Task GivenUnusableCursor_WhenDownloading_ThenReturnTypedResynchronizationInstructions(string cursor)
    {
        var s = await Setup(); Authorize(RegistrationApiFixture.Token(s.Actor));
        using var response = await Send("pageSize=10&cursor=" + cursor, s.Access);
        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); var data = json.RootElement.GetProperty("data");
        Assert.Equal(s.ProfileId, data.GetProperty("profileId").GetGuid()); Assert.True(data.GetProperty("resynchronizationRequired").GetBoolean());
        Assert.True(data.GetProperty("clearScope").GetBoolean()); Assert.Empty(data.GetProperty("items").EnumerateArray());
        Assert.Equal(JsonValueKind.Null, data.GetProperty("nextCursor").ValueKind);
        Assert.Contains("resynchronization_required", await response.Content.ReadAsStringAsync());
    }

    [FunctionalFact]
    public async Task GivenKnownRecord_WhenRemovedThenPermanentlyDeleted_ThenSerializeMinimalNullPayloadsForContractValidation()
    {
        var s = await Setup(withRecord: true); Authorize(RegistrationApiFixture.Token(s.Actor));
        using var snapshot = await Send("pageSize=10", s.Access); Assert.Equal(HttpStatusCode.OK, snapshot.StatusCode);
        using var initial = JsonDocument.Parse(await snapshot.Content.ReadAsStringAsync());
        var cursor = initial.RootElement.GetProperty("data").GetProperty("nextCursor").GetString()!;
        await using var db = fixture.Context();
        await db.Records.Where(x => x.PublicId == s.RecordId).ExecuteUpdateAsync(x => x.SetProperty(r => r.DeletedAt, DateTimeOffset.UtcNow));
        using var removal = await Send("pageSize=10&cursor=" + Uri.EscapeDataString(cursor), s.Access);
        Assert.Equal(HttpStatusCode.OK, removal.StatusCode);
        var removalJson = await removal.Content.ReadAsStringAsync(); using var removed = JsonDocument.Parse(removalJson);
        var item = Assert.Single(removed.RootElement.GetProperty("data").GetProperty("items").EnumerateArray(), x => x.GetProperty("resourceKind").GetString() == "record");
        Assert.Equal("removed", item.GetProperty("eventType").GetString()); Assert.Equal(JsonValueKind.Null, item.GetProperty("data").ValueKind);
        cursor = removed.RootElement.GetProperty("data").GetProperty("nextCursor").GetString()!;
        db.TerminalErasures.Add(new() { ResourceKind = "record", ResourceId = s.RecordId, DeletedAt = DateTimeOffset.UtcNow });
        await db.SaveChangesAsync(); await db.Records.Where(x => x.PublicId == s.RecordId).ExecuteDeleteAsync();
        using var terminal = await Send("pageSize=10&cursor=" + Uri.EscapeDataString(cursor), s.Access);
        Assert.Equal(HttpStatusCode.OK, terminal.StatusCode); var terminalJson = await terminal.Content.ReadAsStringAsync();
        using var deleted = JsonDocument.Parse(terminalJson);
        var tombstone = Assert.Single(deleted.RootElement.GetProperty("data").GetProperty("items").EnumerateArray());
        Assert.Equal("permanently-deleted", tombstone.GetProperty("eventType").GetString()); Assert.Equal(JsonValueKind.Null, tombstone.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.String, tombstone.GetProperty("deletedAt").ValueKind);
        var directory = Path.Combine(AppContext.BaseDirectory, "sync-contract-fixtures"); Directory.CreateDirectory(directory);
        await File.WriteAllTextAsync(Path.Combine(directory, "snapshot.json"), initial.RootElement.GetRawText());
        await File.WriteAllTextAsync(Path.Combine(directory, "removed.json"), removalJson);
        await File.WriteAllTextAsync(Path.Combine(directory, "terminal.json"), terminalJson);
    }

    private async Task<State> Setup(bool withRecord = false)
    {
        using var client = new ProtectionFixture(); using var scoped = new ProtectionFixture();
        var actor = Guid.NewGuid(); fixture.Users[Guid.NewGuid() + "@example.test"] = new(actor, "fixture-password");
        var access = ProtectionFixture.Encode(RandomNumberGenerator.GetBytes(32)); Assert.True(OpaqueAccessHandle.TryHash(access, out var verifier));
        await using var db = fixture.Context();
        var a = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = actor }; db.Accounts.Add(a); await db.SaveChangesAsync();
        var id = Guid.NewGuid();
        var envelope = new EncryptedEnvelope("cerberus-content-v1", 1, ProtectionFixture.Encode(new byte[32]), ProtectionFixture.Encode(new byte[12]), "AQID", ProtectionFixture.Encode(new byte[16]));
        var p = new Profile { AccountId = a.Id, PublicId = id, EditedAt = DateTimeOffset.UtcNow,
            Envelope = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtectionFixture.Json),
            KeyWrappers = JsonSerializer.SerializeToUtf8Bytes(new ProfileKeyWrappers("Master", scoped.Material.UnlockVerifier, client.Wrap(a.PublicId, "profile", id, actor), null), ProtectionFixture.Json) };
        db.Profiles.Add(p); await db.SaveChangesAsync();
        var recordId = Guid.Empty;
        if (withRecord)
        {
            var record = new VaultRecord { PublicId = Guid.NewGuid(), AccountId = a.Id, Envelope = JsonSerializer.SerializeToUtf8Bytes(envelope, ProtectionFixture.Json), EditedAt = DateTimeOffset.UtcNow };
            db.Records.Add(record); await db.SaveChangesAsync(); db.ProfileRecords.Add(new() { AccountId = a.Id, ProfileId = p.Id, RecordId = record.Id }); recordId = record.PublicId;
        }
        db.VaultAccessSessions.Add(new VaultAccessSession { AccountId = a.Id, ProfileId = p.Id, HandleVerifier = verifier,
            IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 });
        await db.SaveChangesAsync(); return new(actor, access, verifier, id, a.Id, recordId);
    }
    private async Task<HttpResponseMessage> Send(string query, string? access, string kind = "")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/sync/changes?" + query);
        if (access is not null) request.Headers.Add("X-Cerberus-Vault-Access", kind == "duplicateHeader" ? [access, access] : [access]);
        if (kind == "body") request.Content = new StringContent("{}", System.Text.Encoding.UTF8, "application/json");
        return await Gateway.Client.SendAsync(request);
    }
    private static async Task Failure(HttpResponseMessage response, HttpStatusCode status, string? error = null)
    {
        Assert.Equal(status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var body = await response.Content.ReadAsStringAsync(); if (error is not null) Assert.Contains(error, body);
        Assert.DoesNotContain("nextCursor", body); Assert.DoesNotContain("keyWrappers", body);
    }
    private sealed record State(Guid Actor, string Access, string Verifier, Guid ProfileId, long Owner, Guid RecordId);
}
