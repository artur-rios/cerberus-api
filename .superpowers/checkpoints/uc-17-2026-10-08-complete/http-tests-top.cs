using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Command.Records;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Query.Records;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class RecordListHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenHttpCreatedOwnedRecords_WhenPaging_ThenReturnExactOpaqueShapeAndStableHighwaterWithoutMutation()
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var foreign = await Setup(owner);
        await Create(foreign); var hidden = await Create(s); var terminal = await Create(s); var first = await Create(s); var second = await Create(s);
        await using (var db = fixture.Context()) {
            await db.Records.Where(x => x.PublicId == hidden.RecordId).ExecuteUpdateAsync(x => x.SetProperty(r => r.DeletedAt, DateTimeOffset.UtcNow));
            db.TerminalErasures.Add(new() { ResourceId = terminal.RecordId, ResourceKind = "record", DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
        }
        var before = await Snapshot(s); using var response = await Read(s, "/api/records?pageSize=1"); var page = await Success<RecordListOutput>(response, 200);
        Fields(page, "items", "nextCursor"); var item = Assert.Single(page.Items); Fields(item, "recordId", "revision", "serverSequence", "editedAt", "envelope");
        Assert.Equal(first.RecordId, item.RecordId); Assert.Equal(first.Envelope, item.Envelope); Assert.Equal(Normalize(first.EditedAt), item.EditedAt);
        Assert.NotNull(page.NextCursor); Assert.Equal(before, await Snapshot(s)); var later = await Create(s);
        using var next = await Read(s, "/api/records?pageSize=1&cursor=" + page.NextCursor); var end = await Success<RecordListOutput>(next, 200);
        Assert.Equal(second.RecordId, Assert.Single(end.Items).RecordId); Assert.Null(end.NextCursor); Assert.DoesNotContain(end.Items, x => x.RecordId == later.RecordId);
    }

    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenFreshNativeSelectedHandle_WhenListing_ThenResolveDirectAndDynamicFolderScopeWithoutOtherProfileLeakage(string mode)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); using var otherKey = new ProtectionFixture();
        var s = await Setup(owner); var root = await Folder(s); var child = await Folder(s, root.Id);
        var p = await Profile(s, owner, scoped, root, mode); var other = await Profile(s, owner, otherKey);
        var direct = await Create(s, [p.PublicId]); var descendant = await Create(s, [], child.PublicId);
        await Create(s, [other.PublicId]); await Create(s);
        var selected = s with { Access = await Open(s, p, scoped) }; var before = await Snapshot(s);
        using var r = await Read(selected, "/api/records"); var list = await Success<RecordListOutput>(r, 200);
        Assert.Equal(new[] { direct.RecordId, descendant.RecordId }.Order(), list.Items.Select(x => x.RecordId).Order());
        Assert.Equal(before, await Snapshot(s)); foreach (var item in list.Items) Fields(item, "recordId", "revision", "serverSequence", "editedAt", "envelope");
    }

    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly, false)][InlineData(CollectionGrantAccess.ReadOnly, true)]
    [InlineData(CollectionGrantAccess.ReadWrite, false)][InlineData(CollectionGrantAccess.ReadWrite, true)]
    public async Task GivenNativeRecipientCollectionGrant_WhenListing_ThenIncludeDirectAndDescendantMembershipOnceWithinCurrentScope(CollectionGrantAccess access, bool selected)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var scoped = new ProtectionFixture();
        var foreign = await Setup(owner); var s = await Setup(recipient); var root = await Folder(foreign); var child = await Folder(foreign, root.Id);
        var direct = await Create(foreign); var descendant = await Create(foreign, [], child.PublicId); await Create(foreign); var own = await Create(s);
        var (collection, _) = await Share(foreign, owner, s, recipient, access, [direct.RecordId, descendant.RecordId], [root.Id]);
        var p = await Profile(s, recipient, scoped, collections: [collection.PublicId]); var request = selected ? s with { Access = await Open(s, p, scoped) } : s;
        var before = await Snapshot(s); var ownerBefore = await Snapshot(foreign); using var r = await Read(request, "/api/records"); var list = await Success<RecordListOutput>(r, 200);
        Assert.Equal((selected ? new[] { direct.RecordId, descendant.RecordId } : new[] { direct.RecordId, descendant.RecordId, own.RecordId }).Order(), list.Items.Select(x => x.RecordId).Order());
        Assert.Equal(before, await Snapshot(s)); Assert.Equal(ownerBefore, await Snapshot(foreign));
        Assert.All(list.Items, x => Fields(x, "recordId", "revision", "serverSequence", "editedAt", "envelope"));
    }

    [FunctionalTheory][InlineData("grant")][InlineData("member")][InlineData("profileCollection")][InlineData("session")]
    public async Task GivenPermissionRevocationBetweenPages_WhenContinuing_ThenApplyCurrentVisibility(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var scoped = new ProtectionFixture();
        var foreign = await Setup(owner); var s = await Setup(recipient); var first = await Create(foreign); var second = await Create(foreign);
        var (collection, grant) = await Share(foreign, owner, s, recipient, CollectionGrantAccess.ReadOnly, [first.RecordId, second.RecordId], []);
        var p = await Profile(s, recipient, scoped, collections: [collection.PublicId]); var selected = s with { Access = await Open(s, p, scoped) };
        using var initial = await Read(selected, "/api/records?pageSize=1"); var page = await Success<RecordListOutput>(initial, 200); Assert.NotNull(page.NextCursor);
        await using (var db = fixture.Context()) {
            if (kind == "grant") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked));
            if (kind == "member") await db.CollectionRecords.Where(x => x.CollectionId == collection.Id).ExecuteDeleteAsync();
            if (kind == "profileCollection") await db.ProfileCollections.Where(x => x.ProfileId == p.Id).ExecuteDeleteAsync();
            if (kind == "session") { OpaqueAccessHandle.TryHash(selected.Access, out var verifier); await db.VaultAccessSessions.Where(x => x.HandleVerifier == verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true)); }
        }
        var before = await Snapshot(s); using var next = await Read(selected, "/api/records?pageSize=1&cursor=" + page.NextCursor);
        if (kind == "session") await Failure(next, 403, "vault_access_denied"); else { var end = await Success<RecordListOutput>(next, 200); Assert.Empty(end.Items); Assert.Null(end.NextCursor); }
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("?pageSize=0")][InlineData("?pageSize=-1")][InlineData("?pageSize=101")][InlineData("?pageSize=2147483648")]
    [InlineData("?pageSize=1.0")][InlineData("?pageSize=+1")][InlineData("?pageSize=%201")][InlineData("?pageSize=01")][InlineData("?pageSize=")]
    [InlineData("?pageSize=1&pageSize=2")][InlineData("?PageSize=1")][InlineData("?profileId=00000000-0000-0000-0000-000000000001")]
    [InlineData("?cursor=")][InlineData("?cursor=bad")][InlineData("?cursor=a&cursor=b")][InlineData("?search=secret")]
    public async Task GivenMalformedOrUnknownQuery_WhenListing_Then400WithoutMutation(string query)
    { using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s); using var r = await Read(s, "/api/records" + query); await Failure(r, 400, "validation_failed"); Assert.Equal(before, await Snapshot(s)); }

    [FunctionalTheory][InlineData("missing", 401)][InlineData("bad", 400)][InlineData("duplicate", 400)]
    [InlineData("body", 400)][InlineData("oversized", 413)][InlineData("transfer", 400)]
    public async Task GivenMalformedAccessOrGetBody_WhenListing_ThenRejectWithoutMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/records"); request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor));
        if (kind != "missing") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", kind == "bad" ? "bad" : s.Access);
        if (kind == "duplicate") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", s.Access);
        if (kind is "body" or "oversized") request.Content = new StringContent(kind == "body" ? "{}" : new string(' ', 65537));
        if (kind == "transfer") { request.Content = new StringContent("{}"); request.Headers.TransferEncodingChunked = true; }
        using var response = await Gateway.Client.SendAsync(request); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("empty")][InlineData("http2")]
    public async Task GivenRawEmptyHeaderOrUnknownLengthBody_WhenListing_Then400(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s); using var host = new WebApplicationFactory<Program>();
        var context = await host.Server.SendAsync(c => { c.Request.Method = "GET"; c.Request.Path = "/api/records";
            c.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(s.Actor); c.Request.Headers["X-Cerberus-Vault-Access"] = kind == "empty" ? "" : s.Access;
            if (kind == "http2") c.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature()); });
        Assert.Equal(400, context.Response.StatusCode); Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString()); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("missing", 401)][InlineData("wrongScope", 401)][InlineData("deleted", 401)][InlineData("unavailable", 503)]
    public async Task GivenInvalidCurrentIdentity_WhenListing_ThenRejectWithoutPersistence(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s);
        if (kind is "deleted" or "unavailable") fixture.Users[s.Actor + "@example.test"] = new(s.Actor, "fixture-password", Deleted: kind == "deleted", IdentityUnavailable: kind == "unavailable");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/records"); request.Headers.Add("X-Cerberus-Vault-Access", s.Access);
        if (kind != "missing") request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor, kind == "wrongScope" ? Guid.NewGuid() : null));
        using var response = await Gateway.Client.SendAsync(request); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("missing", 404)][InlineData("closing", 404)][InlineData("terminal", 404)]
    [InlineData("revoked", 403)][InlineData("expired", 403)][InlineData("future", 403)][InlineData("policy", 403)]
    [InlineData("generation", 403)][InlineData("dangling", 403)]
    public async Task GivenInvalidCurrentAccountOrSession_WhenListing_ThenFailClosed(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner);
        await using (var db = fixture.Context()) { var v = await db.VaultAccessSessions.SingleAsync(x => x.AccountId == s.InternalId);
            if (kind == "revoked") v.Revoked = true; if (kind == "expired") v.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            if (kind == "future") v.IssuedAt = DateTimeOffset.UtcNow.AddHours(1); if (kind == "policy") v.PolicyRevision++;
            if (kind == "generation") v.RevocationGeneration++; if (kind == "dangling") v.ProfileId = long.MaxValue;
            if (kind == "closing") await db.Accounts.Where(x => x.Id == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
            if (kind == "terminal") db.TerminalErasures.Add(new() { ResourceId = s.AccountId, ResourceKind = "account", DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        var before = await Snapshot(s); var request = s;
        if (kind == "missing") { var actor = Guid.NewGuid(); fixture.Users[actor + "@example.test"] = new(actor, "fixture-password"); request = s with { Actor = actor }; }
        using var response = await Read(request, "/api/records"); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("actor")][InlineData("access")][InlineData("size")][InlineData("tamper")]
    public async Task GivenCursorSubstitution_WhenContinuing_Then400(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); await Create(s); await Create(s);
        using var initial = await Read(s, "/api/records?pageSize=1"); var cursor = (await Success<RecordListOutput>(initial, 200)).NextCursor!;
        if (kind == "actor") s = await Setup(owner);
        if (kind == "access") { var access = ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)); OpaqueAccessHandle.TryHash(access, out var verifier);
            await using var db = fixture.Context(); db.VaultAccessSessions.Add(new() { AccountId = s.InternalId, HandleVerifier = verifier, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 }); await db.SaveChangesAsync(); s = s with { Access = access }; }
        if (kind == "tamper") { Assert.True(ProtocolBinary.TryDecode(cursor, null, out var bytes)); bytes[^1] ^= 1; cursor = ProtocolBinary.Encode(bytes); }
        var before = await Snapshot(s); using var response = await Read(s, "/api/records?pageSize=" + (kind == "size" ? 2 : 1) + "&cursor=" + cursor);
        await Failure(response, 400, "validation_failed"); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("envelope")][InlineData("revision")][InlineData("zero")][InlineData("duplicate")]
    [InlineData("time")][InlineData("unavailable")][InlineData("provider")]
    public async Task GivenCorruptVisibleContentOrDependency_WhenListing_Then503WithoutPartialPayload(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var first = await Create(s); var second = await Create(s); await using var db = fixture.Context();
        if (kind == "envelope") await db.Records.Where(x => x.PublicId == second.RecordId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Envelope, new byte[] { 1 }));
        if (kind == "revision") await db.Records.Where(x => x.PublicId == second.RecordId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Revision, 0));
        if (kind is "zero" or "duplicate") { var sequence = (await db.Records.SingleAsync(x => x.PublicId == first.RecordId)).ServerSequence;
            await db.Records.Where(x => x.PublicId == second.RecordId).ExecuteUpdateAsync(x => x.SetProperty(r => r.ServerSequence, kind == "zero" ? 0 : sequence)); }
        if (kind == "time") await db.Records.Where(x => x.PublicId == second.RecordId).ExecuteUpdateAsync(x => x.SetProperty(r => r.EditedAt, DateTimeOffset.MinValue));
        if (kind is "unavailable" or "provider") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection_record RENAME TO http_fixture_unavailable_collection_record");
        var before = await Snapshot(s, kind is "unavailable" or "provider");
        try { using var response = await Read(s, kind == "duplicate" ? "/api/records?pageSize=1" : "/api/records"); await Failure(response, 503, "persistence_unavailable"); }
        finally { if (kind is "unavailable" or "provider") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.http_fixture_unavailable_collection_record RENAME TO collection_record"); }
        Assert.Equal(before, await Snapshot(s, kind is "unavailable" or "provider"));
    }

    [FunctionalFact]
    public async Task GivenEmptyOwnedInventory_WhenListing_Then200EmptyPageWithoutCursor()
    { using var owner = new ProtectionFixture(); var s = await Setup(owner); using var r = await Read(s, "/api/records"); var page = await Success<RecordListOutput>(r, 200); Assert.Empty(page.Items); Assert.Null(page.NextCursor); }
