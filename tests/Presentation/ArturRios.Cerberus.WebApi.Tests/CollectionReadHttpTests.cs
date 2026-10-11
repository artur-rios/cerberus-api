using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Command.Records;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Query.Records;
using ArturRios.Cerberus.Query.Collections;
using ArturRios.Cerberus.Query.Folders;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class CollectionReadHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory]
    [InlineData("Master", false)][InlineData("Master", true)]
    [InlineData("PerProfile", false)][InlineData("PerProfile", true)]
    public async Task GivenHttpCreatedCollection_WhenGetting_ThenReturnExactCiphertextAndSortedDirectLinksWithoutMutation(string mode, bool selected)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); using var otherKey = new ProtectionFixture();
        var s = await Setup(owner); var p = await Profile(s, owner, key, mode: mode); var q = await Profile(s, owner, otherKey);
        var root = await Folder(s); var child = await Folder(s, root.Id); var descendant = await Record(s, child.Id);
        var direct = await Record(s, root.Id); var outside = await Record(s);
        var target = await Create(s, [q.PublicId, p.PublicId], [outside.PublicId, direct.PublicId], [root.PublicId]);
        var request = selected ? s with { Access = await Open(s, p, key) } : s;
        var before = await Snapshot(s); using var response = await Read(request, "/api/collections/" + target.CollectionId);
        var data = await Success<CollectionDetailsOutput>(response, 200);
        Assert.Equal(target.CollectionId, data.CollectionId); Assert.Equal(target.Envelope, data.Envelope); Assert.Equal(Normalize(target.EditedAt), data.EditedAt);
        Assert.Equal((selected ? new[] { p.PublicId } : new[] { p.PublicId, q.PublicId }).Order(), data.ProfileIds);
        Assert.Equal(new[] { outside.PublicId, direct.PublicId }.Order(), data.RecordIds); Assert.Equal(new[] { root.PublicId }, data.FolderIds);
        Assert.DoesNotContain(descendant.PublicId, data.RecordIds); Assert.DoesNotContain(child.PublicId, data.FolderIds);
        await using var db = fixture.Context(); var persisted = await db.Collections.AsNoTracking().SingleAsync(x => x.PublicId == target.CollectionId);
        Assert.Equal(persisted.Revision, data.Revision); Assert.Equal(persisted.ServerSequence, data.ServerSequence); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData(CollectionGrantAccess.ReadOnly, false, "Master")][InlineData(CollectionGrantAccess.ReadOnly, true, "Master")]
    [InlineData(CollectionGrantAccess.ReadOnly, true, "PerProfile")][InlineData(CollectionGrantAccess.ReadWrite, false, "Master")]
    [InlineData(CollectionGrantAccess.ReadWrite, true, "Master")][InlineData(CollectionGrantAccess.ReadWrite, true, "PerProfile")]
    public async Task GivenNativeGrant_WhenGetting_ThenExposeOnlyActorProfilesAndCurrentDirectOwnerMembers(CollectionGrantAccess access, bool selected, string mode)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture();
        using var ownerKey = new ProtectionFixture(); using var key = new ProtectionFixture(); using var otherKey = new ProtectionFixture();
        var foreign = await Setup(owner); var s = await Setup(recipient); var ownerProfile = await Profile(foreign, owner, ownerKey);
        var root = await Folder(foreign); var child = await Folder(foreign, root.Id); var direct = await Record(foreign, root.Id); await Record(foreign, child.Id);
        var target = await Create(foreign, [ownerProfile.PublicId], [direct.PublicId], [root.PublicId]);
        await Grant(foreign, owner, s, recipient, target.CollectionId, access);
        var p = await Profile(s, recipient, key, mode: mode, collections: [target.CollectionId]);
        var q = await Profile(s, recipient, otherKey, collections: [target.CollectionId]);
        var request = selected ? s with { Access = await Open(s, p, key) } : s;
        var before = await Snapshot(s); var ownerBefore = await Snapshot(foreign);
        using var response = await Read(request, "/api/collections/" + target.CollectionId); var data = await Success<CollectionDetailsOutput>(response, 200);
        Assert.Equal(target.Envelope, data.Envelope);
        Assert.Equal((selected ? new[] { p.PublicId } : new[] { p.PublicId, q.PublicId }).Order(), data.ProfileIds);
        Assert.Equal(new[] { direct.PublicId }, data.RecordIds); Assert.Equal(new[] { root.PublicId }, data.FolderIds);
        Assert.Equal(before, await Snapshot(s)); Assert.Equal(ownerBefore, await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData("Master", "record")][InlineData("Master", "folder")]
    [InlineData("PerProfile", "record")][InlineData("PerProfile", "folder")]
    public async Task GivenSelectedMemberOnlyAccess_WhenGetting_ThenMemberSucceedsButCollectionRemainsHidden(string mode, string kind)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); var s = await Setup(owner);
        var folder = await Folder(s); var record = await Record(s, folder.Id);
        var target = await Create(s, records: [record.PublicId], folders: [folder.PublicId]);
        var p = await Profile(s, owner, key, kind == "folder" ? folder : null, mode, records: kind == "record" ? [record.PublicId] : []);
        var selected = s with { Access = await Open(s, p, key) }; var before = await Snapshot(s);
        using var member = await Read(selected, "/api/" + kind + "s/" + (kind == "record" ? record.PublicId : folder.PublicId));
        if (kind == "record") await Success<RecordDetailsOutput>(member, 200); else await Success<FolderDetailsOutput>(member, 200);
        using var response = await Read(selected, "/api/collections/" + target.CollectionId); await Failure(response, 404, "not_found");
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("grant")][InlineData("profileCollection")][InlineData("session")][InlineData("members")]
    public async Task GivenCurrentPermissionOrMembershipRemoval_WhenGettingAgain_ThenObserveNewStateWithoutMutation(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var foreign = await Setup(owner); var s = await Setup(recipient); var folder = await Folder(foreign); var record = await Record(foreign, folder.Id);
        var target = await Create(foreign, records: [record.PublicId], folders: [folder.PublicId]); var grant = await Grant(foreign, owner, s, recipient, target.CollectionId);
        var p = await Profile(s, recipient, key, collections: [target.CollectionId]); var selected = s with { Access = await Open(s, p, key) };
        using (var initial = await Read(selected, "/api/collections/" + target.CollectionId)) await Success<CollectionDetailsOutput>(initial, 200);
        await using (var db = fixture.Context())
        {
            if (kind == "grant") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.State, CollectionGrantState.Revoked));
            if (kind == "profileCollection") await db.ProfileCollections.Where(x => x.ProfileId == p.Id && x.CollectionId == grant.CollectionId).ExecuteDeleteAsync();
            if (kind == "session") { OpaqueAccessHandle.TryHash(selected.Access, out var verifier); await db.VaultAccessSessions.Where(x => x.HandleVerifier == verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true)); }
            if (kind == "members") { await db.CollectionRecords.Where(x => x.CollectionId == grant.CollectionId).ExecuteDeleteAsync(); await db.CollectionFolders.Where(x => x.CollectionId == grant.CollectionId).ExecuteDeleteAsync(); }
        }
        var before = await Snapshot(s); var ownerBefore = await Snapshot(foreign);
        using var response = await Read(selected, "/api/collections/" + target.CollectionId);
        if (kind == "members") { var data = await Success<CollectionDetailsOutput>(response, 200); Assert.Empty(data.RecordIds); Assert.Empty(data.FolderIds); }
        else await Failure(response, kind == "session" ? 403 : 404, kind == "session" ? "vault_access_denied" : "not_found");
        Assert.Equal(before, await Snapshot(s)); Assert.Equal(ownerBefore, await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData("bad")][InlineData("zero")][InlineData("uppercase")][InlineData("compact")][InlineData("braced")][InlineData("urn")]
    public async Task GivenNoncanonicalPublicId_WhenGetting_Then400WithoutMutation(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var id = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
        var text = kind switch { "bad" => "bad", "zero" => Guid.Empty.ToString(), "uppercase" => id.ToString().ToUpperInvariant(), "compact" => id.ToString("N"), "braced" => id.ToString("B"), _ => "urn:uuid:" + id };
        var before = await Snapshot(s); using var response = await Read(s, "/api/collections/" + text); await Failure(response, 400, "validation_failed"); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("?owner=other")][InlineData("?profileId=other")][InlineData("?pageSize=1")]
    [InlineData("?cursor=opaque")][InlineData("?search=secret")][InlineData("?id=other")][InlineData("?x=1&x=2")]
    public async Task GivenAnyQueryInput_WhenGetting_Then400WithoutMutation(string query)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s);
        using var response = await Read(s, "/api/collections/11111111-1111-1111-1111-111111111111" + query);
        await Failure(response, 400, "validation_failed"); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("missing", 401)][InlineData("bad", 400)][InlineData("duplicate", 400)]
    [InlineData("body", 400)][InlineData("oversized", 413)][InlineData("transfer", 400)]
    public async Task GivenMalformedAccessOrBody_WhenGetting_ThenRejectWithoutMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/collections/11111111-1111-1111-1111-111111111111");
        request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor));
        if (kind != "missing") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", kind == "bad" ? "bad" : s.Access);
        if (kind == "duplicate") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", s.Access);
        if (kind is "body" or "oversized") request.Content = new StringContent(kind == "body" ? "{}" : new string(' ', 65537));
        if (kind == "transfer") { request.Content = new StringContent("{}"); request.Headers.TransferEncodingChunked = true; }
        using var response = await Gateway.Client.SendAsync(request);
        await Failure(response, status, kind == "missing" ? "vault_access_required" : status == 400 ? "validation_failed" : null);
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("empty")][InlineData("http2")]
    public async Task GivenRawEmptyHeaderOrUnknownLengthHttp2Body_WhenGetting_Then400(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s); using var host = new WebApplicationFactory<Program>();
        var context = await host.Server.SendAsync(c =>
        {
            c.Request.Method = "GET"; c.Request.Path = "/api/collections/11111111-1111-1111-1111-111111111111";
            c.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(s.Actor);
            c.Request.Headers["X-Cerberus-Vault-Access"] = kind == "empty" ? "" : s.Access;
            if (kind == "http2") { c.Request.Protocol = "HTTP/2"; c.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature()); }
        });
        Assert.Equal(400, context.Response.StatusCode); Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString()); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("missing", 401)][InlineData("wrongScope", 401)][InlineData("deleted", 401)][InlineData("unavailable", 503)]
    public async Task GivenInvalidCurrentIdentity_WhenGetting_ThenRejectWithoutMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s);
        if (kind is "deleted" or "unavailable") fixture.Users[s.Actor + "@example.test"] = new(s.Actor, "fixture-password", Deleted: kind == "deleted", IdentityUnavailable: kind == "unavailable");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/collections/11111111-1111-1111-1111-111111111111");
        request.Headers.Add("X-Cerberus-Vault-Access", s.Access);
        if (kind != "missing") request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor, kind == "wrongScope" ? Guid.NewGuid() : null));
        using var response = await Gateway.Client.SendAsync(request); await Failure(response, status, status == 503 ? "identity_unavailable" : "authentication_required");
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("missing", 404)][InlineData("closing", 404)][InlineData("terminal", 404)]
    [InlineData("revoked", 403)][InlineData("expired", 403)][InlineData("future", 403)][InlineData("policy", 403)]
    [InlineData("generation", 403)][InlineData("dangling", 403)]
    public async Task GivenInvalidCurrentAccountOrSession_WhenGetting_ThenFailClosed(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s);
        await using (var db = fixture.Context())
        {
            var session = await db.VaultAccessSessions.SingleAsync(x => x.AccountId == s.InternalId);
            if (kind == "revoked") session.Revoked = true; if (kind == "expired") session.ExpiresAt = DateTimeOffset.UtcNow.AddSeconds(-1);
            if (kind == "future") session.IssuedAt = DateTimeOffset.UtcNow.AddHours(1); if (kind == "policy") session.PolicyRevision++;
            if (kind == "generation") session.RevocationGeneration++; if (kind == "dangling") session.ProfileId = long.MaxValue;
            if (kind == "closing") await db.Accounts.Where(x => x.Id == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
            if (kind == "terminal") db.TerminalErasures.Add(new() { ResourceId = s.AccountId, ResourceKind = "account", DeletedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var before = await Snapshot(s); var request = s;
        if (kind == "missing") { var actor = Guid.NewGuid(); fixture.Users[actor + "@example.test"] = new(actor, "fixture-password"); request = s with { Actor = actor }; }
        using var response = await Read(request, "/api/collections/" + target.CollectionId); await Failure(response, status, status == 404 ? "not_found" : "vault_access_denied");
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("trash")][InlineData("terminal")]
    public async Task GivenSelectedProfileBecomesHidden_WhenGetting_Then403WithoutWidening(string kind)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); var s = await Setup(owner);
        var p = await Profile(s, owner, key); var target = await Create(s, [p.PublicId]); var selected = s with { Access = await Open(s, p, key) };
        await using (var db = fixture.Context())
        {
            if (kind == "trash") await db.Profiles.Where(x => x.Id == p.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow));
            else { db.TerminalErasures.Add(new() { ResourceId = p.PublicId, ResourceKind = "profile", DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        }
        var before = await Snapshot(s); using var response = await Read(selected, "/api/collections/" + target.CollectionId);
        await Failure(response, 403, "vault_access_denied"); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("missing")][InlineData("foreign")][InlineData("trash")][InlineData("terminal")][InlineData("owner")]
    public async Task GivenMissingOrHiddenTarget_WhenGetting_ThenNonrevealing404(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var foreign = await Setup(owner); var target = await Create(kind == "foreign" ? foreign : s);
        await using (var db = fixture.Context())
        {
            if (kind == "trash") await db.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow).SetProperty(v => v.Envelope, new byte[] { 1 }));
            if (kind == "terminal") { db.TerminalErasures.Add(new() { ResourceId = target.CollectionId, ResourceKind = "collection", DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
            if (kind == "owner") await db.Accounts.Where(x => x.Id == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(v => v.State, AccountState.ClosurePending));
        }
        var before = await Snapshot(s); using var response = await Read(s, "/api/collections/" + (kind == "missing" ? Guid.NewGuid() : target.CollectionId));
        await Failure(response, 404, "not_found"); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("envelope")][InlineData("revision")][InlineData("sequence")][InlineData("unsafeSequence")]
    [InlineData("time")][InlineData("epoch")][InlineData("unavailable")]
    public async Task GivenRequiredProjectionOrPersistenceCorruption_WhenGetting_Then503WithoutPayload(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s); await using var db = fixture.Context();
        if (kind == "envelope") await db.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Envelope, new byte[] { 1 }));
        if (kind == "revision") await db.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revision, 0));
        if (kind is "sequence" or "unsafeSequence") await db.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.ServerSequence, kind == "sequence" ? 0 : 9007199254740992));
        if (kind == "time") await db.Database.ExecuteSqlInterpolatedAsync($"UPDATE cerberus.collection SET edited_at = TIMESTAMPTZ '0001-01-01 00:00:00+00' WHERE public_id = {target.CollectionId}");
        if (kind == "epoch") await db.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.KeyEpoch, 2));
        if (kind == "unavailable") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection_grant RENAME TO http_fixture_unavailable_collection_grant");
        var before = kind == "unavailable" ? null : await Snapshot(s);
        try { using var response = await Read(s, "/api/collections/" + target.CollectionId); await Failure(response, 503, "persistence_unavailable"); }
        finally { if (kind == "unavailable") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.http_fixture_unavailable_collection_grant RENAME TO collection_grant"); }
        if (before is not null) Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("wrapper")][InlineData("ownerPins")][InlineData("recipientPins")][InlineData("epoch")][InlineData("revision")]
    public async Task GivenRelevantNativeCorruption_WhenGetting_Then503WithoutPayload(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var foreign = await Setup(owner); var s = await Setup(recipient);
        var target = await Create(foreign); var grant = await Grant(foreign, owner, s, recipient, target.CollectionId);
        await using (var db = fixture.Context())
        {
            if (kind == "wrapper") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.RecipientKeyEnvelope, new byte[] { 1 }));
            if (kind is "ownerPins" or "recipientPins") await db.VaultProtections.Where(x => x.AccountId == (kind == "ownerPins" ? foreign.InternalId : s.InternalId)).ExecuteUpdateAsync(x => x.SetProperty(v => v.Material, new byte[] { 1 }));
            if (kind == "epoch") await db.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.KeyEpoch, 2));
            if (kind == "revision") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revision, 2));
        }
        var before = await Snapshot(s); var ownerBefore = await Snapshot(foreign);
        using var response = await Read(s, "/api/collections/" + target.CollectionId); await Failure(response, 503, "persistence_unavailable");
        Assert.Equal(before, await Snapshot(s)); Assert.Equal(ownerBefore, await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenRelevantCycle_WhenGetting_Then503UnlessHiddenMemberIsOmitted(bool hidden)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var root = await Folder(s); var child = await Folder(s, root.Id); var record = await Record(s, child.Id);
        var target = await Create(s, records: [record.PublicId], folders: [child.PublicId]);
        await using (var db = fixture.Context()) await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ParentFolderId, child.Id).SetProperty(v => v.DeletedAt, hidden ? (DateTimeOffset?)DateTimeOffset.UtcNow : null));
        var before = await Snapshot(s); using var response = await Read(s, "/api/collections/" + target.CollectionId);
        if (hidden) { var data = await Success<CollectionDetailsOutput>(response, 200); Assert.Empty(data.RecordIds); Assert.Empty(data.FolderIds); }
        else await Failure(response, 503, "persistence_unavailable");
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenUnrelatedCorruptionAndDamagedDirectMemberCiphertext_WhenGetting_ThenReturnOnlyTargetAndDirectIds()
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var foreign = await Setup(recipient);
        var root = await Folder(s); var member = await Record(s, root.Id); var target = await Create(s, records: [member.PublicId], folders: [root.PublicId]);
        var unrelated = await Create(s); var other = await Create(foreign); var grant = await Grant(foreign, recipient, s, owner, other.CollectionId);
        var cycle = await Folder(s);
        await using (var db = fixture.Context())
        {
            await db.Collections.Where(x => x.PublicId == unrelated.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Envelope, new byte[] { 1 }).SetProperty(v => v.ServerSequence, 0));
            await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.RecipientKeyEnvelope, new byte[] { 1 }));
            await db.Folders.Where(x => x.Id == cycle.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.ParentFolderId, cycle.Id));
            await db.Records.Where(x => x.Id == member.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Envelope, new byte[] { 1 }).SetProperty(v => v.Revision, 0));
            await db.Folders.Where(x => x.Id == root.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.Envelope, new byte[] { 1 }).SetProperty(v => v.Revision, 0));
        }
        var before = await Snapshot(s); using var response = await Read(s, "/api/collections/" + target.CollectionId);
        var data = await Success<CollectionDetailsOutput>(response, 200); Assert.Equal(target.Envelope, data.Envelope);
        Assert.Equal(new[] { member.PublicId }, data.RecordIds); Assert.Equal(new[] { root.PublicId }, data.FolderIds); Assert.Equal(before, await Snapshot(s));
    }

    private async Task<CollectionGrant> Grant(State owner, ProtectionFixture author, State recipient, ProtectionFixture recipientKeys, Guid collectionId, CollectionGrantAccess access = CollectionGrantAccess.ReadOnly)
    {
        await using var db = fixture.Context(); var collection = await db.Collections.SingleAsync(x => x.PublicId == collectionId);
        var grant = new CollectionGrant { PublicId = Guid.NewGuid(), CollectionId = collection.Id, RecipientAccountId = recipient.InternalId, Access = access };
        grant.RecipientKeyEnvelope = Bytes(author.WrapGrant(owner.AccountId, collectionId, grant.PublicId, recipient.Actor, recipientKeys.Material.RecipientKey));
        db.CollectionGrants.Add(grant); await db.SaveChangesAsync(); return grant;
    }

    private async Task<CollectionCreateInput> Create(State s, Guid[]? profiles = null, Guid[]? records = null, Guid[]? folders = null)
    {
        var input = new CollectionCreateInput(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch.AddTicks(19), profiles ?? [], records ?? [], folders ?? []);
        using var response = await Send(s, input); await Success<CreateCollectionOutput>(response, 201); return input;
    }
    private async Task<VaultRecord> Record(State s, long? folder = null)
    {
        var record = new VaultRecord { PublicId = Guid.NewGuid(), AccountId = s.InternalId, FolderId = folder, Envelope = Bytes(Envelope()), EditedAt = DateTimeOffset.UnixEpoch };
        await using var db = fixture.Context(); db.Records.Add(record); await db.SaveChangesAsync(); return record;
    }
    private sealed class BodyFeature:IHttpRequestBodyDetectionFeature{public bool CanHaveBody=>true;}
    private sealed record State(Guid Actor,Guid AccountId,long InternalId,string Access);
    private async Task<State> Setup(ProtectionFixture client)
    {var actor=Guid.NewGuid();fixture.Users[actor+"@example.test"]=new(actor,"fixture-password");await using var db=fixture.Context();var account=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=Bytes(Envelope())};db.Accounts.Add(account);await db.SaveChangesAsync();var access=ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(access,out var verifier);db.VaultProtections.Add(new(){AccountId=account.Id,Material=Bytes(client.Material)});db.VaultAccessSessions.Add(new(){AccountId=account.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return new(actor,account.PublicId,account.Id,access);}
    private async Task<VaultFolder> Folder(State s,long? parent=null){var f=new VaultFolder{PublicId=Guid.NewGuid(),AccountId=s.InternalId,ParentFolderId=parent,Envelope=Bytes(Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.Context();db.Folders.Add(f);await db.SaveChangesAsync();return f;}
    private async Task<Profile> Profile(State s,ProtectionFixture owner,ProtectionFixture scoped,VaultFolder? folder=null,string mode="Master",Guid[]? collections=null,Guid[]? records=null)
    {var id=Guid.NewGuid();var input=new ProfileCreateInput(id,Envelope(),new(mode,scoped.Material.UnlockVerifier,owner.Wrap(s.AccountId,"profile",id,s.Actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null),DateTimeOffset.UnixEpoch,records??[],folder is null?[]:[folder.PublicId],collections??[]);using var request=Request(s,Bytes(input),"/api/profiles");using var r=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Created,r.StatusCode);await using var db=fixture.Context();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==id);}
    private async Task<string> Open(State s,Profile p,ProtectionFixture scoped)
    {await using var db=fixture.Context();var revision=(await db.Profiles.AsNoTracking().SingleAsync(x=>x.Id==p.Id)).Revision;var raw=Bytes(new{expectedRevision=revision});using var challengeRequest=Request(s,Bytes(new{expectedRevision=revision,requestHash=ProtocolBinary.Encode(SHA256.HashData(raw))}),"/api/profiles/"+p.PublicId+"/access/challenges");challengeRequest.Headers.Remove("X-Cerberus-Vault-Access");using var issued=await Gateway.Client.SendAsync(challengeRequest);var challenge=(await Success<IssueProfileAccessChallengeOutput>(issued,201)).Challenge;using var open=Request(s,raw,"/api/profiles/"+p.PublicId+"/access");open.Headers.Remove("X-Cerberus-Vault-Access");open.Headers.Add("X-Cerberus-Challenge-Id",challenge.ChallengeId.ToString());open.Headers.Add("X-Cerberus-Proof",scoped.Sign(challenge));using var response=await Gateway.Client.SendAsync(open);return (await Success<OpenProfileAccessOutput>(response,200)).VaultAccess;}
    private static CollectionCreateInput Input(Guid[]? profiles=null)=>new(Guid.NewGuid(),Envelope(),DateTimeOffset.UnixEpoch.AddTicks(19),profiles??[],[],[]);
    private static EncryptedEnvelope Envelope(long epoch=1)=>new("cerberus-content-v1",epoch,ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(12)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(73)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(16)));
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private static DateTimeOffset Normalize(DateTimeOffset value)=>value.AddTicks(-(value.Ticks%10));
    private static HttpRequestMessage Request(State s,byte[] body,string path="/api/collections"){var r=new HttpRequestMessage(HttpMethod.Post,path){Content=new ByteArrayContent(body)};r.Content.Headers.ContentType=new("application/json");r.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));r.Headers.Add("X-Cerberus-Vault-Access",s.Access);return r;}
    private async Task<HttpResponseMessage> Send(State s,CollectionCreateInput input){using var request=Request(s,Bytes(input));return await Gateway.Client.SendAsync(request);}
    private async Task<HttpResponseMessage> Read(State s,string path){using var request=new HttpRequestMessage(HttpMethod.Get,path);request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));request.Headers.Add("X-Cerberus-Vault-Access",s.Access);return await Gateway.Client.SendAsync(request);}
    private async Task<string> Protected(State s){await using var db=fixture.Context();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleAsync(x=>x.AccountId==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync()});}
    private static async Task<T> Success<T>(HttpResponseMessage response, int status) where T : class
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var text = await response.Content.ReadAsStringAsync(); using var document = JsonDocument.Parse(text);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        if (typeof(T) == typeof(CollectionDetailsOutput)) {
            var data = document.RootElement.GetProperty("data");
            Assert.Equal(new[] { "collectionId", "editedAt", "envelope", "folderIds", "profileIds", "recordIds", "revision", "serverSequence" }, data.EnumerateObject().Select(x => x.Name).Order());
            Assert.Contains("collection_found", document.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetString()));
        }
        return Assert.IsType<T>(JsonSerializer.Deserialize<DataOutput<T>>(text, ProtectionFixture.Json)!.Data);
    }
    private static void Fields(object value,params string[] names){using var d=JsonDocument.Parse(JsonSerializer.Serialize(value,ProtectionFixture.Json));Assert.Equal(names.Order(),d.RootElement.EnumerateObject().Select(x=>x.Name).Order());}
    private static async Task Failure(HttpResponseMessage r,int status,string? error=null){Assert.Equal((HttpStatusCode)status,r.StatusCode);Assert.True(r.Headers.CacheControl?.NoStore);if(status==413){Assert.Equal("",await r.Content.ReadAsStringAsync());return;}using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.False(d.RootElement.GetProperty("success").GetBoolean());if(d.RootElement.TryGetProperty("data",out var data))Assert.Equal(JsonValueKind.Null,data.ValueKind);if(error is not null)Assert.Contains(error,d.RootElement.GetProperty("errors").EnumerateArray().Select(x=>x.GetString()));}
    private async Task<string> Snapshot(State s,bool skipMembers=false){await using var db=fixture.Context();return JsonSerializer.Serialize(new{Protected=await Protected(s),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>x.RecipientAccountId==s.InternalId || db.Collections.Any(c=>c.Id==x.CollectionId && c.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync(),Members=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),CollectionFolders=skipMembers?[]:await db.CollectionFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileCollections=await db.ProfileCollections.AsNoTracking().Where(x=>db.Profiles.Any(p=>p.Id==x.ProfileId && p.AccountId==s.InternalId)).OrderBy(x=>x.ProfileId).ThenBy(x=>x.CollectionId).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),ProfileFolders=await db.ProfileFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileRecords=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync()});}
}
