using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Command.Folders;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Command.Protection;
using ArturRios.Cerberus.Command.Records;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Collections;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Query.Collections;
using ArturRios.Cerberus.Query.Folders;
using ArturRios.Cerberus.Query.Records;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class CollectionGrantRevocationHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{


    // Catch missing route, widened authority, retained online access, leaked material, or partial commits.
    [FunctionalTheory]
    [InlineData("wide", "wide", "read-only")][InlineData("wide", "wide", "read-write")]
    [InlineData("Master", "Master", "read-only")][InlineData("Master", "Master", "read-write")]
    [InlineData("PerProfile", "PerProfile", "read-only")][InlineData("PerProfile", "PerProfile", "read-write")]
    [InlineData("wide", "Master", "read-only")][InlineData("PerProfile", "wide", "read-write")]
    public async Task GivenNativeActiveGrant_WhenRevokingThenRegranting_ThenOnlineAuthorityAndExactProfileScopeFollowCurrentState(
        string ownerMode, string recipientMode, string access)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture();
        using var ownerKey = new ProtectionFixture(); using var recipientKey = new ProtectionFixture(); using var otherKey = new ProtectionFixture();
        var s = await Setup(owner); var r = await Setup(recipient);
        var folder = await Folder(s); var child = await Folder(s, folder); var direct = await Record(s); var inherited = await Record(s, child);
        var target = await Create(s, [direct], [folder]); var other = await Create(s);
        var ownRecord = await Record(r); var ownFolder = await Folder(r); var ownCollection = await Create(r, [ownRecord], [ownFolder]);
        var initial = Input(s, owner, r, recipient, target, access);
        using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        using (var response = await Grant(s, other, Input(s, owner, r, recipient, other))) await Success<GrantCollectionAccessOutput>(response, 201);
        var ownerProfile = await Profile(s, owner, ownerKey, ownerMode == "wide" ? "Master" : ownerMode, [target]);
        var recipientProfile = await Profile(r, recipient, recipientKey, recipientMode == "wide" ? "Master" : recipientMode, [target, ownCollection]);
        var unrelatedProfile = await Profile(r, recipient, otherKey, "Master", [ownCollection]);
        var actor = ownerMode == "wide" ? s : s with { Access = await Open(s, ownerProfile, ownerKey) };
        var recipientActor = recipientMode == "wide" ? r : r with { Access = await Open(r, recipientProfile, recipientKey) };
        await Visibility(recipientActor, recipientMode == "wide" ? [target, other, ownCollection] : [target, ownCollection],
            [direct, inherited, ownRecord], [folder, child, ownFolder]);
        using (var read = await Read(recipientActor, "/api/collections/" + target)) await Success<CollectionDetailsOutput>(read);
        foreach (var record in new[] { direct, inherited })
        {
            using var write = await UpdateRecord(recipientActor, record);
            if (access == "read-write") await Success<UpdateRecordOutput>(write);
            else await Failure(write, 403, "vault_access_denied");
        }
        var before = await Stable(s, r, true); var generation = await Generation(r);
        await using var db = fixture.Context();
        var old = await db.CollectionGrants.AsNoTracking().SingleAsync(x => x.PublicId == initial.GrantId);
        using (var response = await Revoke(actor, target, initial.GrantId))
        {
            var data = await Success<RevokeCollectionAccessOutput>(response);
            Assert.Equal(target, data.CollectionId); Assert.Equal(initial.GrantId, data.GrantId); Assert.Equal(2, data.Revision);
            var raw = await response.Content.ReadAsStringAsync();
            foreach (var secret in new[] { r.Actor.ToString(), r.AccountId.ToString(), s.AccountId.ToString(), recipientProfile.PublicId.ToString(), initial.KeyEnvelope.Signature, actor.Access, recipientActor.Access })
                Assert.DoesNotContain(secret, raw);
        }
        Assert.Equal(before, await Stable(s, r, true)); Assert.Equal(generation + 1, await Generation(r));
        var revoked = await db.CollectionGrants.AsNoTracking().SingleAsync(x => x.PublicId == initial.GrantId);
        Assert.Equal(old.Id, revoked.Id); Assert.Equal(old.CollectionId, revoked.CollectionId); Assert.Equal(old.RecipientAccountId, revoked.RecipientAccountId);
        Assert.Equal(old.Access, revoked.Access); Assert.Equal(CollectionGrantState.Revoked, revoked.State);
        Assert.Equal(2, revoked.Revision); Assert.Empty(revoked.RecipientKeyEnvelope); Assert.NotEqual(old.ConcurrencyStamp, revoked.ConcurrencyStamp);
        foreach (var path in new[] { "/api/collections", "/api/collections/" + target, "/api/records/" + direct, "/api/folders/" + child, "/api/records/" + ownRecord })
        { using var stale = await Read(recipientActor, path); await Failure(stale, 403, "vault_access_denied"); }
        using (var staleWrite = await UpdateRecord(recipientActor, direct)) await Failure(staleWrite, 403, "vault_access_denied");
        var freshWide = r with { Access = await Unlock(r, recipient) };
        var fresh = recipientMode == "wide" ? freshWide : r with { Access = await Open(r, recipientProfile, recipientKey) };
        await Visibility(fresh, recipientMode == "wide" ? [other, ownCollection] : [ownCollection], [ownRecord], [ownFolder]);
        await Visibility(freshWide, [other, ownCollection], [ownRecord], [ownFolder]);
        foreach (var path in new[] { "/api/collections/" + target, "/api/records/" + direct, "/api/records/" + inherited, "/api/folders/" + folder, "/api/folders/" + child })
        { using var hidden = await Read(fresh, path); await Failure(hidden, 404, "not_found"); }
        var deniedBefore = await Stable(s, r); var deniedGrants = await Grants(target);
        foreach (var record in new[] { direct, inherited })
        {
            using (var update = await UpdateRecord(fresh, record)) await Failure(update, 404, "not_found");
            using var move = await Send(fresh, "/api/records/" + record + "/folder", new { expectedRevision = access == "read-write" ? 2 : 1, folderId = (Guid?)null });
            await Failure(move, 404, "not_found");
        }
        foreach (var parent in new[] { folder, child })
        {
            using (var create = await Send(fresh, "/api/records", new RecordCreateInput(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch, recipientMode == "wide" ? [] : [recipientProfile.PublicId], parent), HttpMethod.Post))
                await Failure(create, 404, "not_found");
            using (var create = await Send(fresh, "/api/folders", new FolderCreateInput(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch, recipientMode == "wide" ? [] : [recipientProfile.PublicId], parent), HttpMethod.Post))
                await Failure(create, 404, "not_found");
            using (var update = await Send(fresh, "/api/folders/" + parent, new FolderUpdateInput(1, Envelope(), DateTimeOffset.UnixEpoch)))
                await Failure(update, 404, "not_found");
            using (var move = await Send(fresh, "/api/folders/" + parent + "/parent", new { expectedRevision = 1, parentFolderId = (Guid?)null }))
                await Failure(move, 404, "not_found");
            using (var move = await Send(fresh, "/api/records/" + ownRecord + "/folder", new { expectedRevision = 1, folderId = parent }))
                await Failure(move, 404, "not_found");
            using (var move = await Send(fresh, "/api/folders/" + ownFolder + "/parent", new { expectedRevision = 1, parentFolderId = parent }))
                await Failure(move, 404, "not_found");
        }
        Assert.Equal(deniedBefore, await Stable(s, r)); Assert.Equal(deniedGrants, await Grants(target));
        Assert.True(await db.ProfileCollections.AnyAsync(x => x.ProfileId == recipientProfile.Id && x.CollectionId == old.CollectionId));
        using (var ownRead = await Read(fresh, "/api/records/" + ownRecord)) await Success<RecordDetailsOutput>(ownRead);
        await UnchangedFailure(s, r, actor, target, initial.GrantId, 2, 404);
        var regrant = Input(s, owner, r, recipient, target, access, initial.GrantId, 2);
        Assert.NotEqual(Bytes(initial.KeyEnvelope), Bytes(regrant.KeyEnvelope));
        using (var response = await Grant(actor, target, regrant)) Assert.Equal(3, (await Success<GrantCollectionAccessOutput>(response, 201)).Revision);
        Assert.Equal(generation + 1, await Generation(r));
        var restored = r with { Access = await Open(r, recipientProfile, recipientKey) };
        await Visibility(restored, [target, ownCollection], [direct, inherited, ownRecord], [folder, child, ownFolder]);
        using (var details = await Read(restored, "/api/collections/" + target))
            Assert.Equal(new[] { recipientProfile.PublicId }, (await Success<CollectionDetailsOutput>(details)).ProfileIds);
        var isolated = r with { Access = await Open(r, unrelatedProfile, otherKey) };
        await Visibility(isolated, [ownCollection], [ownRecord], [ownFolder]);
        using (var hidden = await Read(isolated, "/api/collections/" + target)) await Failure(hidden, 404, "not_found");
        using (var ownerRead = await Read(actor, "/api/collections/" + target)) await Success<CollectionDetailsOutput>(ownerRead);
    }

    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenUnlinkedSelectedOwner_WhenRevoking_Then404WithoutWidening(string mode)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target); using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        var p = await Profile(s, owner, key, mode);
        await UnchangedFailure(s, r, s with { Access = await Open(s, p, key) }, target, initial.GrantId, 1, 404);
    }

    [FunctionalTheory][InlineData("read-only", false)][InlineData("read-write", false)][InlineData("read-only", true)][InlineData("read-write", true)]
    public async Task GivenVisibleForeignTarget_WhenRevoking_ThenNativeEvidencePrecedesDenial(string access, bool corrupt)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target, access); using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        if (corrupt) { await using var db = fixture.Context(); await db.CollectionGrants.Where(x => x.PublicId == initial.GrantId).ExecuteUpdateAsync(x => x.SetProperty(g => g.RecipientKeyEnvelope, new byte[] { 1 })); }
        await UnchangedFailure(s, r, r, target, Guid.NewGuid(), 1, corrupt ? 503 : 403);
    }

    [FunctionalTheory][InlineData("malformed")][InlineData("empty")][InlineData("unprotected")]
    public async Task GivenOwnerReductionWithUnusableOldMaterial_WhenRevoking_Then200ErasesWrapper(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target); using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        await using var db = fixture.Context();
        if (kind == "unprotected") await db.VaultProtections.Where(x => x.AccountId == r.InternalId).ExecuteDeleteAsync();
        else await db.CollectionGrants.Where(x => x.PublicId == initial.GrantId).ExecuteUpdateAsync(x => x.SetProperty(g => g.RecipientKeyEnvelope, kind == "empty" ? Array.Empty<byte>() : new byte[] { 1 }));
        var before = await Stable(s, r, true);
        using var response2 = await Revoke(s, target, initial.GrantId); await Success<RevokeCollectionAccessOutput>(response2);
        Assert.Equal(before, await Stable(s, r, true)); Assert.Equal(2, await Generation(r));
        Assert.Empty((await db.CollectionGrants.AsNoTracking().SingleAsync(x => x.PublicId == initial.GrantId)).RecipientKeyEnvelope);
    }

    [FunctionalTheory][InlineData("stale",409)][InlineData("grantMaximum",409)][InlineData("generationMaximum",409)]
    [InlineData("grantMissing",404)][InlineData("grantRevoked",404)][InlineData("grantTerminal",404)][InlineData("crossCollection",404)]
    [InlineData("recipientClosing",404)][InlineData("recipientTerminal",404)]
    [InlineData("grantAccess",503)][InlineData("recipientGeneration",503)]
    [InlineData("ownerClosing",404)][InlineData("ownerTerminal",404)][InlineData("deleted",404)][InlineData("terminal",404)][InlineData("missing",404)]
    [InlineData("expired",403)][InlineData("revoked",403)][InlineData("policy",403)][InlineData("generation",403)]
    public async Task GivenCurrentState_WhenRevoking_ThenExactSafeStatusAndNoMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target); using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        var pathTarget = kind == "missing" ? Guid.NewGuid() : kind == "crossCollection" ? await Create(s) : target;
        var grantId = kind == "grantMissing" ? Guid.NewGuid() : initial.GrantId;
        await using (var db = fixture.Context())
        {
            if (kind is "ownerClosing" or "recipientClosing") await db.Accounts.Where(x => x.Id == (kind == "ownerClosing" ? s.InternalId : r.InternalId)).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
            if (kind == "deleted") await db.Collections.Where(x => x.PublicId == target).ExecuteUpdateAsync(x => x.SetProperty(c => c.DeletedAt, DateTimeOffset.UtcNow));
            if (kind == "grantRevoked") await db.CollectionGrants.Where(x => x.PublicId == initial.GrantId).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked));
            if (kind == "grantAccess") await db.CollectionGrants.Where(x => x.PublicId == initial.GrantId).ExecuteUpdateAsync(x => x.SetProperty(g => g.Access, (CollectionGrantAccess)99));
            if (kind == "grantMaximum") await db.CollectionGrants.Where(x => x.PublicId == initial.GrantId).ExecuteUpdateAsync(x => x.SetProperty(g => g.Revision, ProtocolBinary.MaxInteger));
            if (kind is "recipientGeneration" or "generationMaximum") await db.Accounts.Where(x => x.Id == r.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.RevocationGeneration, kind == "generationMaximum" ? ProtocolBinary.MaxInteger : ProtocolBinary.MaxInteger + 1));
            if (kind is "terminal" or "ownerTerminal" or "recipientTerminal" or "grantTerminal") db.TerminalErasures.Add(new() { ResourceKind = kind == "terminal" ? "collection" : kind == "grantTerminal" ? "grant" : "account", ResourceId = kind == "terminal" ? target : kind == "grantTerminal" ? initial.GrantId : kind == "ownerTerminal" ? s.AccountId : r.AccountId, DeletedAt = DateTimeOffset.UtcNow });
            if (kind is "expired" or "revoked" or "policy" or "generation")
            { var session = await db.VaultAccessSessions.SingleAsync(x => x.AccountId == s.InternalId); if (kind == "expired") session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); if (kind == "revoked") session.Revoked = true; if (kind == "policy") session.PolicyRevision++; if (kind == "generation") session.RevocationGeneration++; }
            await db.SaveChangesAsync();
        }
        await UnchangedFailure(s, r, kind == "grantAccess" ? r : s, pathTarget, grantId, kind == "stale" ? 2 : 1, status, target);
    }

    [FunctionalTheory][InlineData("outage")][InlineData("grant")][InlineData("generation")]
    public async Task GivenDatabaseFault_WhenRevoking_Then503RollsBackBothMutationsAndCleanRetrySucceeds(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target); using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        var before = await Stable(s, r); var grants = await Grants(target); await using var db = fixture.Context();
        if (kind == "outage") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection_grant RENAME TO fixture_unavailable_grant");
        else if (kind == "grant") await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION cerberus.revoke_http_fault() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private fixture fault'; END $$; CREATE TRIGGER revoke_http_fault AFTER UPDATE ON cerberus.collection_grant FOR EACH ROW EXECUTE FUNCTION cerberus.revoke_http_fault()");
        else await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION cerberus.revoke_http_fault() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private fixture fault'; END $$; CREATE TRIGGER revoke_http_fault AFTER UPDATE OF revocation_generation ON cerberus.account FOR EACH ROW EXECUTE FUNCTION cerberus.revoke_http_fault()");
        try { using var response = await Revoke(s, target, initial.GrantId); await Failure(response, 503, "persistence_unavailable"); }
        finally
        {
            if (kind == "outage") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_grant RENAME TO collection_grant");
            else if (kind == "grant") await db.Database.ExecuteSqlRawAsync("DROP TRIGGER revoke_http_fault ON cerberus.collection_grant; DROP FUNCTION cerberus.revoke_http_fault()");
            else await db.Database.ExecuteSqlRawAsync("DROP TRIGGER revoke_http_fault ON cerberus.account; DROP FUNCTION cerberus.revoke_http_fault()");
        }
        Assert.Equal(before, await Stable(s, r)); Assert.Equal(grants, await Grants(target));
        using var retry = await Revoke(s, target, initial.GrantId); await Success<RevokeCollectionAccessOutput>(retry); Assert.Equal(2, await Generation(r));
    }

    [FunctionalTheory]
    [InlineData("{}")][InlineData("[]")][InlineData("null")][InlineData("{")]
    [InlineData("{\"ExpectedRevision\":1}")][InlineData("{\"expectedRevision\":\"1\"}")][InlineData("{\"expectedRevision\":1.5}")]
    [InlineData("{\"expectedRevision\":null}")][InlineData("{\"expectedRevision\":0}")][InlineData("{\"expectedRevision\":-1}")]
    [InlineData("{\"expectedRevision\":9007199254740991}")][InlineData("{\"expectedRevision\":9007199254740992}")][InlineData("{\"expectedRevision\":1,\"expectedRevision\":1}")]
    [InlineData("{\"expectedRevision\":1,\"access\":\"read-only\"}")][InlineData("{\"expectedRevision\":1,\"keyEnvelope\":{}}")]
    [InlineData("{\"expectedRevision\":1,\"grantId\":\"secret\"}")][InlineData("{\"expectedRevision\":1,\"actor\":\"secret\"}")]
    [InlineData("{\"expectedRevision\":1,\"profileId\":\"secret\"}")][InlineData("{\"expectedRevision\":1,\"plaintext\":\"secret\"}")]
    public async Task GivenInvalidExactBody_WhenRevoking_Then400AndNoMutation(string raw)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target); using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        var before = await Stable(s, r); var grants = await Grants(target);
        using var request = Request(s, RevokePath(target, initial.GrantId), Encoding.UTF8.GetBytes(raw), HttpMethod.Delete);
        using var failure = await Gateway.Client.SendAsync(request); await Failure(failure, 400, "validation_failed");
        Assert.Equal(before, await Stable(s, r)); Assert.Equal(grants, await Grants(target));
    }

    [FunctionalFact]
    public async Task GivenRequestBlockedOnOwnerLock_WhenCallerCancels_ThenDatabaseWaitEndsWithoutMutation()
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture();
        var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target);
        using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        var before = await Stable(s, r); var grants = await Grants(target);
        await using var holder = fixture.Context(); await using var transaction = await holder.Database.BeginTransactionAsync();
        var pid = await holder.Database.SqlQuery<int>($"SELECT pg_backend_pid() AS \"Value\"").SingleAsync();
        await holder.Database.ExecuteSqlInterpolatedAsync($"SELECT FROM cerberus.account WHERE id={s.InternalId} FOR UPDATE");
        using var request = Request(s, RevokePath(target, initial.GrantId), Bytes(new { expectedRevision = 1 }), HttpMethod.Delete);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var pending = Gateway.Client.SendAsync(request, cancellation.Token);
        await using var observer = fixture.Context();
        async Task<bool> Blocked() => await observer.Database.SqlQuery<bool>($"SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE {pid}=ANY(pg_blocking_pids(pid))) AS \"Value\"").SingleAsync();
        var deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (!await Blocked() && !pending.IsCompleted && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        Assert.True(await Blocked());
        await cancellation.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => await pending);
        deadline = DateTimeOffset.UtcNow.AddSeconds(10);
        while (await Blocked() && DateTimeOffset.UtcNow < deadline) await Task.Delay(20);
        Assert.False(await Blocked());
        await transaction.RollbackAsync();
        Assert.Equal(before, await Stable(s, r)); Assert.Equal(grants, await Grants(target));
    }

    private async Task UnchangedFailure(State owner, State recipient, State actor, Guid target, Guid grant, long revision, int status, Guid? snapshotTarget = null)
    {
        var before = await Stable(owner, recipient); var grants = await Grants(snapshotTarget ?? target);
        using var response = await Revoke(actor, target, grant, revision);
        await Failure(response, status, status switch { 400 => "validation_failed", 403 => "vault_access_denied", 404 => "not_found", 409 => "revision_conflict", _ => "persistence_unavailable" });
        Assert.Equal(before, await Stable(owner, recipient)); Assert.Equal(grants, await Grants(snapshotTarget ?? target));
    }
    private static string RevokePath(Guid target, Guid grant) => Path(target) + "/" + grant;
    private async Task<HttpResponseMessage> Revoke(State s, Guid target, Guid grant, long revision = 1)
    { using var request = Request(s, RevokePath(target, grant), Bytes(new { expectedRevision = revision }), HttpMethod.Delete); return await Gateway.Client.SendAsync(request); }
    private async Task<HttpResponseMessage> Send(State s, string path, object body, HttpMethod? method = null)
    { using var request = Request(s, path, Bytes(body), method ?? HttpMethod.Put); return await Gateway.Client.SendAsync(request); }
    [FunctionalTheory][InlineData("bad")][InlineData("zero")][InlineData("uppercase")][InlineData("compact")][InlineData("braced")][InlineData("urn")]
    public async Task GivenNoncanonicalCollectionOrGrantPath_WhenRevoking_Then400(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient);
        var id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var text = kind switch { "bad" => "bad", "zero" => Guid.Empty.ToString(), "uppercase" => id.ToString().ToUpperInvariant(), "compact" => id.ToString("N"), "braced" => id.ToString("B"), _ => "urn:uuid:" + id };
        foreach (var path in new[] { "/api/collections/" + text + "/grants/" + id, "/api/collections/" + id + "/grants/" + text })
        {
            using var request = Request(s, path, Bytes(new { expectedRevision = 1 }), HttpMethod.Delete);
            using var response = await Gateway.Client.SendAsync(request); await Failure(response, 400, "validation_failed");
        }
    }

    [FunctionalTheory][InlineData("query",400)][InlineData("duplicate",400)][InlineData("bad",400)][InlineData("missing",401)]
    [InlineData("utf8",400)][InlineData("bom",400)][InlineData("compression",400)][InlineData("charset",400)][InlineData("oversized",413)][InlineData("empty",400)]
    [InlineData("noMedia",400)][InlineData("text",400)][InlineData("suffix",400)][InlineData("prefix",400)][InlineData("brokenMedia",400)]
    public async Task GivenInvalidTransportOrAccess_WhenRevoking_ThenStrictFailureAndNoMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target); using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        var before = await Stable(s, r); var grants = await Grants(target);
        var raw = kind == "oversized" ? Encoding.UTF8.GetBytes(new string(' ', 65537)) : kind == "utf8" ? [0xc3, 0x28] : kind == "empty" ? [] : Bytes(new { expectedRevision = 1 });
        if (kind == "bom") raw = new byte[] { 239, 187, 191 }.Concat(raw).ToArray();
        using var request = Request(s, RevokePath(target, initial.GrantId) + (kind == "query" ? "?profileId=secret" : ""), raw, HttpMethod.Delete);
        if (kind is "missing" or "bad") request.Headers.Remove("X-Cerberus-Vault-Access");
        if (kind == "bad") request.Headers.Add("X-Cerberus-Vault-Access", "bad");
        if (kind == "duplicate") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", s.Access);
        if (kind == "compression") request.Content!.Headers.ContentEncoding.Add("gzip");
        if (kind == "charset") request.Content!.Headers.ContentType!.CharSet = "utf-16";
        if (kind is "noMedia" or "text" or "suffix" or "prefix" or "brokenMedia")
        { request.Content!.Headers.Remove("Content-Type"); if (kind != "noMedia") request.Content.Headers.TryAddWithoutValidation("Content-Type", kind switch { "text" => "text/json", "suffix" => "application/problem+json", "prefix" => "application/json-forged", _ => "application/json; charset=\"utf-8" }); }
        using var failure = await Gateway.Client.SendAsync(request); await Failure(failure, status, status == 401 ? "vault_access_required" : status == 400 ? "validation_failed" : null);
        Assert.Equal(before, await Stable(s, r)); Assert.Equal(grants, await Grants(target));
    }

    [FunctionalTheory][InlineData("application/json")][InlineData("Application/Json")][InlineData("application/json; charset=utf-8")][InlineData("application/json; charset=\"UTF-8\"")]
    public async Task GivenValidJsonMediaType_WhenRevoking_Then200(string media)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target); using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        using var request = Request(s, RevokePath(target, initial.GrantId), Bytes(new { expectedRevision = 1 }), HttpMethod.Delete);
        request.Content!.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(media);
        using var changed = await Gateway.Client.SendAsync(request); await Success<RevokeCollectionAccessOutput>(changed);
    }

    [FunctionalFact]
    public async Task GivenRawEmptyAccessHeader_WhenRevoking_Then400AndNoMutation()
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target); using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        var before = await Stable(s, r); var grants = await Grants(target);
        using var host = new WebApplicationFactory<Program>(); var raw = Bytes(new { expectedRevision = 1 });
        var context = await host.Server.SendAsync(c => { c.Request.Method = "DELETE"; c.Request.Path = RevokePath(target, initial.GrantId); c.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(s.Actor); c.Request.Headers["X-Cerberus-Vault-Access"] = ""; c.Request.ContentType = "application/json"; c.Request.ContentLength = raw.Length; c.Request.Body = new MemoryStream(raw); });
        Assert.Equal(400, context.Response.StatusCode); Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString());
        Assert.Equal(before, await Stable(s, r)); Assert.Equal(grants, await Grants(target));
    }

    [FunctionalTheory][InlineData("missing",401)][InlineData("scope",401)][InlineData("deleted",401)][InlineData("unavailable",503)]
    public async Task GivenRejectedIdentity_WhenRevoking_ThenSafeStatusAndNoMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target); using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        if (kind is "deleted" or "unavailable") fixture.Users[s.Actor + "@example.test"] = new(s.Actor, "fixture-password", Deleted: kind == "deleted", IdentityUnavailable: kind == "unavailable");
        using var request = Request(s, RevokePath(target, initial.GrantId), Bytes(new { expectedRevision = 1 }), HttpMethod.Delete);
        if (kind == "missing") request.Headers.Authorization = null; if (kind == "scope") request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor, Guid.NewGuid()));
        var before = await Stable(s, r); var grants = await Grants(target);
        using var failure = await Gateway.Client.SendAsync(request); await Failure(failure, status, status == 401 ? "authentication_required" : "identity_unavailable");
        Assert.Equal(before, await Stable(s, r)); Assert.Equal(grants, await Grants(target));
    }

    private async Task<HttpResponseMessage> UpdateRecord(State s, Guid record)
    {
        await using var db = fixture.Context(); var before = await db.Records.AsNoTracking().SingleAsync(x => x.PublicId == record);
        var input = new RecordUpdateInput(before.Revision, Envelope(), DateTimeOffset.UnixEpoch.AddDays(1));
        using var request = Request(s, "/api/records/" + record, Bytes(input), HttpMethod.Put);
        var response = await Gateway.Client.SendAsync(request); var after = await db.Records.AsNoTracking().SingleAsync(x => x.PublicId == record);
        if (response.IsSuccessStatusCode)
        { Assert.Equal(before.Revision + 1, after.Revision); Assert.Equal(Bytes(input.Envelope), after.Envelope); Assert.True(after.ServerSequence > before.ServerSequence); }
        else Assert.Equal(JsonSerializer.Serialize(before), JsonSerializer.Serialize(after));
        return response;
    }
    private async Task<long> Generation(State s)
    { await using var db = fixture.Context(); return await db.Accounts.Where(x => x.Id == s.InternalId).Select(x => x.RevocationGeneration).SingleAsync(); }
    private async Task<string> Unlock(State s, ProtectionFixture client)
    {
        var raw = Bytes(new { expectedProtectionRevision = 1 });
        using var issue = Request(s, "/api/vault/challenges", Bytes(new { operation = "unlock-account", requestHash = ProtocolBinary.Encode(SHA256.HashData(raw)) }));
        issue.Headers.Remove("X-Cerberus-Vault-Access");
        using var issued = await Gateway.Client.SendAsync(issue); var challenge = (await Success<VaultChallengeOutput>(issued, 201)).Challenge;
        using var unlock = Request(s, "/api/vault/unlock", raw); unlock.Headers.Remove("X-Cerberus-Vault-Access");
        unlock.Headers.Add("X-Cerberus-Challenge-Id", challenge.ChallengeId.ToString()); unlock.Headers.Add("X-Cerberus-Proof", client.Sign(challenge));
        using var response = await Gateway.Client.SendAsync(unlock); return (await Success<VaultUnlockOutput>(response)).VaultAccess;
    }

    private async Task Visibility(State s, Guid[] collections, Guid[] records, Guid[] folders)
    {
        using var collectionResponse = await Read(s, "/api/collections"); Assert.Equal(collections.Order(), (await Success<CollectionListOutput>(collectionResponse)).Items.Select(x => x.CollectionId).Order());
        using var recordResponse = await Read(s, "/api/records"); Assert.Equal(records.Order(), (await Success<RecordListOutput>(recordResponse)).Items.Select(x => x.RecordId).Order());
        using var folderResponse = await Read(s, "/api/folders"); Assert.Equal(folders.Order(), (await Success<FolderListOutput>(folderResponse)).Items.Select(x => x.FolderId).Order());
        foreach (var id in records) { using var response = await Read(s, "/api/records/" + id); await Success<RecordDetailsOutput>(response); }
        foreach (var id in folders) { using var response = await Read(s, "/api/folders/" + id); await Success<FolderDetailsOutput>(response); }
    }
    private async Task<Guid> Create(State s, Guid[]? records = null, Guid[]? folders = null)
    {
        var input = new CollectionCreateInput(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch, [], records ?? [], folders ?? []);
        using var request = Request(s, "/api/collections", Bytes(input)); using var response = await Gateway.Client.SendAsync(request); await Success<CreateCollectionOutput>(response, 201); return input.CollectionId;
    }
    private async Task<Guid> Record(State s, Guid? folder = null)
    {
        var input = new RecordCreateInput(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch, [], folder);
        using var request = Request(s, "/api/records", Bytes(input)); using var response = await Gateway.Client.SendAsync(request); await Success<CreateRecordOutput>(response, 201); return input.RecordId;
    }
    private async Task<Guid> Folder(State s, Guid? parent = null)
    {
        var input = new FolderCreateInput(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch, [], parent);
        using var request = Request(s, "/api/folders", Bytes(input)); using var response = await Gateway.Client.SendAsync(request); await Success<CreateFolderOutput>(response, 201); return input.FolderId;
    }
    private async Task<Profile> Profile(State s, ProtectionFixture owner, ProtectionFixture scoped, string mode, Guid[]? collections = null)
    {
        var id = Guid.NewGuid(); var input = new ProfileCreateInput(id, Envelope(), new(mode, scoped.Material.UnlockVerifier, owner.Wrap(s.AccountId, "profile", id, s.Actor), mode == "PerProfile" ? scoped.Material.PasswordWrapper : null), DateTimeOffset.UnixEpoch, [], [], collections ?? []);
        using var request = Request(s, "/api/profiles", Bytes(input)); using var response = await Gateway.Client.SendAsync(request); await Success<CreateProfileOutput>(response, 201);
        await using var db = fixture.Context(); return await db.Profiles.AsNoTracking().SingleAsync(x => x.PublicId == id);
    }
    private async Task<string> Open(State s, Profile p, ProtectionFixture scoped)
    {
        var raw = Bytes(new { expectedRevision = p.Revision });
        using var issue = Request(s, "/api/profiles/" + p.PublicId + "/access/challenges", Bytes(new { expectedRevision = p.Revision, requestHash = ProtocolBinary.Encode(SHA256.HashData(raw)) })); issue.Headers.Remove("X-Cerberus-Vault-Access");
        using var issued = await Gateway.Client.SendAsync(issue); var challenge = (await Success<IssueProfileAccessChallengeOutput>(issued, 201)).Challenge;
        using var open = Request(s, "/api/profiles/" + p.PublicId + "/access", raw); open.Headers.Remove("X-Cerberus-Vault-Access"); open.Headers.Add("X-Cerberus-Challenge-Id", challenge.ChallengeId.ToString()); open.Headers.Add("X-Cerberus-Proof", scoped.Sign(challenge));
        using var response = await Gateway.Client.SendAsync(open); return (await Success<OpenProfileAccessOutput>(response)).VaultAccess;
    }
    private sealed record State(Guid Actor, Guid AccountId, long InternalId, string Access);
    private async Task<State> Setup(ProtectionFixture client)
    {
        var actor = Guid.NewGuid(); fixture.Users[actor + "@example.test"] = new(actor, "fixture-password"); await using var db = fixture.Context();
        var account = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = actor, DetailsEnvelope = Bytes(Envelope()) }; db.Accounts.Add(account); await db.SaveChangesAsync();
        var access = ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)); OpaqueAccessHandle.TryHash(access, out var verifier);
        db.VaultProtections.Add(new() { AccountId = account.Id, Material = Bytes(client.Material) });
        db.VaultAccessSessions.Add(new() { AccountId = account.Id, HandleVerifier = verifier, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 }); await db.SaveChangesAsync(); return new(actor, account.PublicId, account.Id, access);
    }
    private static CollectionGrantInput Input(State s, ProtectionFixture owner, State r, ProtectionFixture recipient, Guid target, string access = "read-only", Guid? id = null, long? expected = null)
    { var grant = id ?? Guid.NewGuid(); return new(grant, r.AccountId, expected, access, owner.WrapGrant(s.AccountId, target, grant, r.Actor, recipient.Material.RecipientKey, revision: (expected ?? 0) + 1)); }
    private static EncryptedEnvelope Envelope() => new("cerberus-content-v1", 1, ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(12)), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(73)), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(16)));
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ProtectionFixture.Json);
    private static string Path(Guid id) => "/api/collections/" + id + "/grants";
    private static HttpRequestMessage Request(State s, string path, byte[]? body = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, path); if (body is not null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json"); }
        request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor)); request.Headers.Add("X-Cerberus-Vault-Access", s.Access); return request;
    }
    private async Task<HttpResponseMessage> Grant(State s, Guid id, CollectionGrantInput input) { using var request = Request(s, Path(id), Bytes(input)); return await Gateway.Client.SendAsync(request); }
    private async Task<HttpResponseMessage> Read(State s, string path) { using var request = Request(s, path, method: HttpMethod.Get); return await Gateway.Client.SendAsync(request); }
    private static async Task<T> Success<T>(HttpResponseMessage response, int status = 200) where T : class
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var raw = await response.Content.ReadAsStringAsync(); using var document = JsonDocument.Parse(raw); Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        if (typeof(T) == typeof(GrantCollectionAccessOutput) || typeof(T) == typeof(RevokeCollectionAccessOutput))
        {
            Assert.Equal(typeof(T) == typeof(RevokeCollectionAccessOutput) ? new[] { "collectionId", "grantId", "revision" } : new[] { "access", "collectionId", "folderIds", "grantId", "recordIds", "revision" }, document.RootElement.GetProperty("data").EnumerateObject().Select(x => x.Name).Order());
            Assert.Equal(new[] { typeof(T) == typeof(RevokeCollectionAccessOutput) ? "collection_access_revoked" : "collection_access_granted" }, document.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetString()));
        }
        return Assert.IsType<T>(JsonSerializer.Deserialize<DataOutput<T>>(raw, ProtectionFixture.Json)!.Data);
    }
    private static async Task Failure(HttpResponseMessage response, int status, string? error = null)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); var raw = await response.Content.ReadAsStringAsync();
        if (status == 413) { Assert.Equal("", raw); return; }
        using var document = JsonDocument.Parse(raw); Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        if (document.RootElement.TryGetProperty("data", out var data)) Assert.Equal(JsonValueKind.Null, data.ValueKind);
        if (error is not null) Assert.Equal(new[] { error }, document.RootElement.GetProperty("errors").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("private fixture fault", raw);
    }
    private async Task<string> Grants(Guid id)
    { await using var db = fixture.Context(); return JsonSerializer.Serialize(await db.CollectionGrants.AsNoTracking().Where(x => db.Collections.Any(c => c.Id == x.CollectionId && c.PublicId == id)).OrderBy(x => x.Id).ToArrayAsync()); }
    private async Task<string> Stable(State s, State r, bool excludeGeneration = false)
    {
        await using var db = fixture.Context();
        return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
              'accounts',(SELECT jsonb_agg(CASE WHEN {excludeGeneration} AND id={r.InternalId} THEN to_jsonb(a)-'revocation_generation' ELSE to_jsonb(a) END ORDER BY id) FROM cerberus.account a WHERE id IN ({s.InternalId},{r.InternalId})),
              'protection',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM cerberus.vault_protection p WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'sessions',(SELECT jsonb_agg(to_jsonb(v) ORDER BY id) FROM cerberus.vault_access_session v WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'profiles',(SELECT jsonb_agg(to_jsonb(p) ORDER BY id) FROM cerberus.profile p WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'collections',(SELECT jsonb_agg(to_jsonb(c) ORDER BY id) FROM cerberus.collection c WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'records',(SELECT jsonb_agg(to_jsonb(r) ORDER BY id) FROM cerberus.record r WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'folders',(SELECT jsonb_agg(to_jsonb(f) ORDER BY id) FROM cerberus.folder f WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'pc',(SELECT jsonb_agg(to_jsonb(pc) ORDER BY profile_id,collection_id) FROM cerberus.profile_collection pc JOIN cerberus.profile p ON p.id=pc.profile_id WHERE p.account_id IN ({s.InternalId},{r.InternalId})),
              'cf',(SELECT jsonb_agg(to_jsonb(cf) ORDER BY collection_id,folder_id) FROM cerberus.collection_folder cf WHERE account_id IN ({s.InternalId},{r.InternalId})),
              'cr',(SELECT jsonb_agg(to_jsonb(cr) ORDER BY collection_id,record_id) FROM cerberus.collection_record cr WHERE account_id IN ({s.InternalId},{r.InternalId})))::text AS "Value"
            """).SingleAsync();
    }
}
