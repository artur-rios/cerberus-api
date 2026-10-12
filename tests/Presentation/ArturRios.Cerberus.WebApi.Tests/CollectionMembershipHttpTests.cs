using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ArturRios.Cerberus.Command.Collections;
using ArturRios.Cerberus.Command.Folders;
using ArturRios.Cerberus.Command.Profiles;
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
using Npgsql;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class CollectionMembershipHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory][InlineData("wide")][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenRealNativeOwnerFlows_WhenReplacingMembers_ThenExactSortedOutputAndCurrentRecipientVisibility(string mode)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var s = await Setup(owner); var other = await Setup(recipient);
        var oldFolder = await Folder(s); var oldRecord = await Record(s, oldFolder.PublicId);
        var addedFolder = await Folder(s); var descendant = await Record(s, addedFolder.PublicId);
        var secondFolder = await Folder(s); var direct = await Record(s); var retained = await Record(s);
        var target = await Create(s, [oldRecord.PublicId, retained.PublicId], [oldFolder.PublicId]);
        var grant = await Grant(s, owner, other, recipient, target.CollectionId);
        var profile = await Profile(s, owner, key, mode == "wide" ? "Master" : mode,
            [target.CollectionId], [direct.PublicId], [addedFolder.PublicId, secondFolder.PublicId]);
        var actor = mode == "wide" ? s : s with { Access = await Open(s, profile, key) };
        var grantBefore = await Grants(target.CollectionId); var recipientBefore = await Snapshot(other);
        await Visibility(other, [oldRecord.PublicId, retained.PublicId], [oldFolder.PublicId]);
        var input = Replacement(records: [direct.PublicId, retained.PublicId, descendant.PublicId], folders: [secondFolder.PublicId, addedFolder.PublicId]);
        await using var db = fixture.Context(); var before = await db.Collections.AsNoTracking().SingleAsync(x => x.PublicId == target.CollectionId);
        var members = await db.Records.AsNoTracking().Where(x => x.AccountId == s.InternalId).ToDictionaryAsync(x => x.PublicId);
        using (var response = await Replace(actor, target.CollectionId, input))
        {
            var result = await Success<SetCollectionMembershipOutput>(response);
            Assert.Equal(target.CollectionId, result.CollectionId); Assert.Equal(2, result.Revision);
            Assert.InRange(result.ServerSequence, before.ServerSequence + 1, ProtocolBinary.MaxInteger);
            Assert.Equal(Normalize(input.EditedAt), result.EditedAt);
            Assert.Equal(input.RecordIds.Order(), result.RecordIds); Assert.Equal(input.FolderIds.Order(), result.FolderIds);
        }
        using (var response = await Read(actor, "/api/collections/" + target.CollectionId))
        {
            var detail = await Success<CollectionDetailsOutput>(response); Assert.Equal(input.Envelope, detail.Envelope);
            Assert.Equal(input.RecordIds.Order(), detail.RecordIds); Assert.Equal(input.FolderIds.Order(), detail.FolderIds);
            Assert.Equal(new[] { profile.PublicId }, detail.ProfileIds);
        }
        using (var response = await Read(other, "/api/collections/" + target.CollectionId))
        {
            var detail = await Success<CollectionDetailsOutput>(response); Assert.Equal(input.Envelope, detail.Envelope);
            Assert.Empty(detail.ProfileIds); Assert.Equal(input.RecordIds.Order(), detail.RecordIds);
        }
        await Visibility(other, [direct.PublicId, retained.PublicId, descendant.PublicId], [addedFolder.PublicId, secondFolder.PublicId]);
        using (var response = await Read(other, "/api/records/" + oldRecord.PublicId)) await Failure(response, 404, "not_found");
        using (var response = await Read(other, "/api/folders/" + oldFolder.PublicId)) await Failure(response, 404, "not_found");
        Assert.Equal(grantBefore, await Grants(target.CollectionId)); Assert.Equal(recipientBefore, await Snapshot(other));
        var current = await db.Records.AsNoTracking().Where(x => x.AccountId == s.InternalId).ToArrayAsync();
        foreach (var row in current)
        {
            var previous = members[row.PublicId]; Assert.Equal(previous.Envelope, row.Envelope); Assert.Equal(previous.EditedAt, row.EditedAt);
            Assert.Equal(previous.FolderId, row.FolderId); Assert.Equal(previous.Revision + (row.PublicId == retained.PublicId ? 0 : 1), row.Revision);
        }
        Assert.Equal(1, (await db.CollectionGrants.AsNoTracking().SingleAsync(x => x.Id == grant.Id)).Revision);
        // Dynamic descendants are visible without a fabricated direct membership.
        var future = await Record(s, addedFolder.PublicId);
        using (var response = await Read(other, "/api/records/" + future.PublicId)) await Success<RecordDetailsOutput>(response);
        using (var response = await Read(other, "/api/collections/" + target.CollectionId))
            Assert.DoesNotContain(future.PublicId, (await Success<CollectionDetailsOutput>(response)).RecordIds);
    }

    [FunctionalTheory][InlineData("Master", "privateRecord")][InlineData("PerProfile", "privateRecord")]
    [InlineData("Master", "privateFolder")][InlineData("PerProfile", "privateFolder")]
    [InlineData("Master", "recordContainer")][InlineData("PerProfile", "recordContainer")]
    public async Task GivenSelectedScope_WhenIntroducingPrivateMemberOrRecordContainer_Then404WithoutMutation(string mode, string kind)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); var s = await Setup(owner);
        var folder = await Folder(s); var record = await Record(s, folder.PublicId); var target = await Create(s);
        var p = await Profile(s, owner, key, mode, [target.CollectionId], kind == "recordContainer" ? [record.PublicId] : []);
        var actor = s with { Access = await Open(s, p, key) }; var before = await Snapshot(s);
        using var response = await Replace(actor, target.CollectionId, Replacement(records: kind == "privateRecord" ? [record.PublicId] : [], folders: kind == "privateRecord" ? [] : [folder.PublicId]));
        await Failure(response, 404, "not_found"); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenSelectedInheritedMembers_WhenPromotingThenClearing_ThenNoScopeExpansion(string mode)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); var s = await Setup(owner);
        var folder = await Folder(s); var child = await Folder(s, folder.PublicId); var record = await Record(s, child.PublicId);
        var target = await Create(s, folders: [folder.PublicId]); var p = await Profile(s, owner, key, mode, [target.CollectionId]);
        var actor = s with { Access = await Open(s, p, key) };
        using (var response = await Replace(actor, target.CollectionId, Replacement(records: [record.PublicId], folders: [child.PublicId]))) await Success<SetCollectionMembershipOutput>(response);
        using (var response = await Replace(actor, target.CollectionId, Replacement(2))) await Success<SetCollectionMembershipOutput>(response);
        using (var response = await Read(actor, "/api/records/" + record.PublicId)) await Failure(response, 404, "not_found");
        using (var response = await Read(s, "/api/records/" + record.PublicId)) await Success<RecordDetailsOutput>(response);
    }

    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly, "wide")][InlineData(CollectionGrantAccess.ReadWrite, "wide")]
    [InlineData(CollectionGrantAccess.ReadOnly, "Master")][InlineData(CollectionGrantAccess.ReadWrite, "Master")]
    [InlineData(CollectionGrantAccess.ReadOnly, "PerProfile")][InlineData(CollectionGrantAccess.ReadWrite, "PerProfile")]
    public async Task GivenNativeRecipient_WhenAdministratingMembershipWithStaleRevision_Then403WithoutMutation(CollectionGrantAccess access, string mode)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var o = await Setup(owner); var s = await Setup(recipient); var target = await Create(o); await Grant(o, owner, s, recipient, target.CollectionId, access);
        if (mode != "wide") { var p = await Profile(s, recipient, key, mode, [target.CollectionId]); s = s with { Access = await Open(s, p, key) }; }
        using (var response = await Read(s, "/api/collections/" + target.CollectionId)) await Success<CollectionDetailsOutput>(response);
        var before = await Snapshot(o); var otherBefore = await Snapshot(s);
        using var result = await Replace(s, target.CollectionId, Replacement(77)); await Failure(result, 403, "vault_access_denied");
        Assert.Equal(before, await Snapshot(o)); Assert.Equal(otherBefore, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("missing")][InlineData("foreign")][InlineData("unselected")][InlineData("deleted")]
    [InlineData("terminal")][InlineData("ownerClosing")][InlineData("ownerTerminal")][InlineData("revoked")][InlineData("grantTerminal")]
    public async Task GivenHiddenCorruptTarget_WhenReplacing_Then404BeforeEvidenceOrStaleRevision(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var o = await Setup(owner); var s = await Setup(recipient); var target = await Create(o);
        var grant = kind == "foreign" ? null : await Grant(o, owner, s, recipient, target.CollectionId);
        if (kind == "unselected") { var p = await Profile(s, recipient, key); s = s with { Access = await Open(s, p, key) }; }
        await using (var db = fixture.Context())
        {
            await db.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(c => c.Envelope, new byte[] { 1 }).SetProperty(c => c.DeletedAt, kind == "deleted" ? (DateTimeOffset?)DateTimeOffset.UtcNow : null));
            if (kind == "ownerClosing") await db.Accounts.Where(x => x.Id == o.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
            if (kind == "revoked") await db.CollectionGrants.Where(x => x.Id == grant!.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked));
            if (kind is "terminal" or "ownerTerminal" or "grantTerminal") { db.TerminalErasures.Add(new() { ResourceId = kind == "terminal" ? target.CollectionId : kind == "ownerTerminal" ? o.AccountId : grant!.PublicId, ResourceKind = kind == "terminal" ? "collection" : kind == "ownerTerminal" ? "account" : "grant", DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        }
        var before = await Snapshot(o); using var response = await Replace(s, kind == "missing" ? Guid.NewGuid() : target.CollectionId, Replacement(77));
        await Failure(response, 404, "not_found"); Assert.Equal(before, await Snapshot(o));
    }

    [FunctionalTheory][InlineData("wrapper", true)][InlineData("wrapper", false)][InlineData("ownerPins", true)][InlineData("ownerPins", false)]
    [InlineData("recipientPins", true)][InlineData("recipientPins", false)][InlineData("revision", true)][InlineData("revision", false)]
    public async Task GivenMalformedRequiredNativeEvidence_WhenReplacingAsOwnerOrRecipient_Then503WithoutMutation(string kind, bool asOwner)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var o = await Setup(owner); var s = await Setup(recipient);
        var target = await Create(o); var grant = await Grant(o, owner, s, recipient, target.CollectionId);
        await using (var db = fixture.Context())
        {
            if (kind == "wrapper") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.RecipientKeyEnvelope, new byte[] { 1 }));
            if (kind is "ownerPins" or "recipientPins") await db.VaultProtections.Where(x => x.AccountId == (kind == "ownerPins" ? o.InternalId : s.InternalId)).ExecuteUpdateAsync(x => x.SetProperty(v => v.Material, new byte[] { 1 }));
            if (kind == "revision") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.Revision, 2));
        }
        var before = await Snapshot(o); var otherBefore = await Snapshot(s); using var response = await Replace(asOwner ? o : s, target.CollectionId, Replacement(77));
        await Failure(response, 503, "persistence_unavailable"); Assert.Equal(before, await Snapshot(o)); Assert.Equal(otherBefore, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("outage")][InlineData("fault")]
    public async Task GivenDatabaseFailureAfterOrBeforeWrite_WhenReplacing_ThenAtomicRollbackAndCleanRetry(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var previous = await Record(s); var added = await Record(s);
        var target = await Create(s, [previous.PublicId]); var before = await Snapshot(s); var input = Replacement(records: [added.PublicId]);
        await using var db = fixture.Context();
        if (kind == "outage") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection RENAME TO fixture_unavailable_collection");
        else await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION cerberus.membership_http_fault() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private fixture fault'; END $$; CREATE TRIGGER membership_http_fault AFTER INSERT ON cerberus.collection_record FOR EACH ROW EXECUTE FUNCTION cerberus.membership_http_fault()");
        try { using var response = await Replace(s, target.CollectionId, input); await Failure(response, 503, "persistence_unavailable"); }
        finally
        {
            if (kind == "outage") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_collection RENAME TO collection");
            else await db.Database.ExecuteSqlRawAsync("DROP TRIGGER membership_http_fault ON cerberus.collection_record; DROP FUNCTION cerberus.membership_http_fault()");
        }
        Assert.Equal(before, await Snapshot(s)); using var retry = await Replace(s, target.CollectionId, input); await Success<SetCollectionMembershipOutput>(retry);
    }

    [FunctionalFact]
    public async Task GivenConcurrentSameRevisionAndLostSuccess_WhenReplacing_ThenOneWinnerAndRetryPreservesIt()
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var a = await Record(s); var b = await Record(s); var target = await Create(s);
        var first = Replacement(records: [a.PublicId]); var second = Replacement(records: [b.PublicId]);
        var replies = await Task.WhenAll(Replace(s, target.CollectionId, first), Replace(s, target.CollectionId, second));
        try
        {
            var winner = Array.FindIndex(replies, r => r.StatusCode == HttpStatusCode.OK); Assert.InRange(winner, 0, 1);
            await Success<SetCollectionMembershipOutput>(replies[winner]); await Failure(replies[1 - winner], 409, "revision_conflict");
            var before = await Snapshot(s); using var retry = await Replace(s, target.CollectionId, winner == 0 ? first : second); await Failure(retry, 409, "revision_conflict");
            Assert.Equal(before, await Snapshot(s));
        }
        finally { foreach (var reply in replies) reply.Dispose(); }
    }

    [FunctionalTheory][InlineData("policy",403)][InlineData("revoked",403)][InlineData("profile",403)]
    [InlineData("link",404)][InlineData("deleted",404)][InlineData("terminal",404)]
    public async Task GivenCurrentAuthorityChangesWhileWaiting_WhenReplacingWithStaleRevision_ThenCurrentDenialWins(string kind, int status)
    {
        using var owner = new ProtectionFixture(); using var key = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s);
        var p = await Profile(s, owner, key, collections: [target.CollectionId]); var actor = s with { Access = await Open(s, p, key) };
        await using var held = fixture.Context(); await using var tx = await held.Database.BeginTransactionAsync();
        await held.Database.ExecuteSqlInterpolatedAsync($"SELECT 1 FROM cerberus.account WHERE id={s.InternalId} FOR NO KEY UPDATE");
        var pending = Replace(actor, target.CollectionId, Replacement(77)); await WaitForAccountLock(); Assert.False(pending.IsCompleted);
        if (kind == "policy") await held.Accounts.Where(x => x.Id == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.PolicyRevision, a => a.PolicyRevision + 1));
        if (kind == "revoked") await held.VaultAccessSessions.Where(x => x.AccountId == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true));
        if (kind == "profile") await held.Profiles.Where(x => x.Id == p.Id).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow));
        if (kind == "link") await held.ProfileCollections.Where(x => x.ProfileId == p.Id).ExecuteDeleteAsync();
        if (kind == "deleted") await held.Collections.Where(x => x.PublicId == target.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(v => v.DeletedAt, DateTimeOffset.UtcNow));
        if (kind == "terminal") { held.TerminalErasures.Add(new() { ResourceId = target.CollectionId, ResourceKind = "collection", DeletedAt = DateTimeOffset.UtcNow }); await held.SaveChangesAsync(); }
        await tx.CommitAsync(); var before = await Snapshot(s); using var response = await pending.WaitAsync(TimeSpan.FromSeconds(15));
        await Failure(response, status, status == 403 ? "vault_access_denied" : "not_found"); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("bad")][InlineData("zero")][InlineData("uppercase")][InlineData("compact")][InlineData("braced")][InlineData("urn")]
    public async Task GivenNoncanonicalRoute_WhenReplacing_Then400WithoutMutation(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var id = Guid.Parse("abcdefab-1234-4321-abcd-123456789abc");
        var path = kind switch { "bad" => "bad", "zero" => Guid.Empty.ToString(), "uppercase" => id.ToString().ToUpperInvariant(), "compact" => id.ToString("N"), "braced" => id.ToString("B"), _ => "urn:uuid:" + id };
        var before = await Snapshot(s); using var request = Request(s, "/api/collections/" + path + "/members", Bytes(Replacement()), HttpMethod.Put);
        using var response = await Gateway.Client.SendAsync(request); await Failure(response, 400, "validation_failed"); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("ownerId")][InlineData("profileIds")][InlineData("grants")][InlineData("recipientKeyEnvelope")][InlineData("keyEpoch")]
    [InlineData("actor")][InlineData("vaultAccess")][InlineData("collectionId")][InlineData("name")][InlineData("duplicate")][InlineData("case")]
    [InlineData("numericRevision")][InlineData("numericEpoch")][InlineData("missingRevision")][InlineData("missingEnvelope")][InlineData("missingTime")]
    [InlineData("missingRecords")][InlineData("missingFolders")][InlineData("nullRecords")][InlineData("nullFolders")][InlineData("duplicateRecord")]
    [InlineData("duplicateFolder")][InlineData("uppercaseRecord")][InlineData("uppercaseFolder")][InlineData("compactRecord")][InlineData("zeroRecord")]
    [InlineData("zeroFolder")][InlineData("null")][InlineData("empty")][InlineData("format")][InlineData("epoch")][InlineData("revision")]
    [InlineData("unsafeRevision")][InlineData("time")][InlineData("offset")][InlineData("saltReuse")][InlineData("nonceReuse")]
    public async Task GivenInvalidOrForgedBody_WhenReplacing_Then400WithoutMutation(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s); var input = Replacement();
        if (kind == "format") input = input with { Envelope = input.Envelope with { Format = "unknown" } };
        if (kind == "epoch") input = input with { Envelope = input.Envelope with { KeyEpoch = 2 } };
        if (kind == "revision") input = input with { ExpectedRevision = 0 };
        if (kind == "unsafeRevision") input = input with { ExpectedRevision = ProtocolBinary.MaxInteger + 1 };
        if (kind == "time") input = input with { EditedAt = new(9, TimeSpan.Zero) };
        if (kind == "offset") input = input with { EditedAt = input.EditedAt.ToOffset(TimeSpan.FromHours(1)) };
        if (kind == "saltReuse") input = input with { Envelope = input.Envelope with { KeySalt = target.Envelope.KeySalt } };
        if (kind == "nonceReuse") input = input with { Envelope = input.Envelope with { Nonce = target.Envelope.Nonce } };
        var body = JsonSerializer.SerializeToNode(input, ProtectionFixture.Json)!.AsObject();
        var missing = kind switch { "missingRevision" => "expectedRevision", "missingEnvelope" => "envelope", "missingTime" => "editedAt", "missingRecords" => "recordIds", "missingFolders" => "folderIds", _ => null };
        if (missing is not null) body.Remove(missing);
        if (kind is "ownerId" or "profileIds" or "grants" or "recipientKeyEnvelope" or "keyEpoch" or "actor" or "vaultAccess" or "collectionId" or "name") body[kind] = null;
        if (kind is "nullRecords" or "nullFolders") body[kind == "nullRecords" ? "recordIds" : "folderIds"] = null;
        var id = "abcdefab-1234-4321-abcd-123456789abc";
        if (kind is "duplicateRecord" or "duplicateFolder") body[kind == "duplicateRecord" ? "recordIds" : "folderIds"] = new JsonArray(id, id);
        if (kind is "uppercaseRecord" or "uppercaseFolder" or "compactRecord" or "zeroRecord" or "zeroFolder")
            body[kind.EndsWith("Folder", StringComparison.Ordinal) ? "folderIds" : "recordIds"] = new JsonArray(kind.StartsWith("zero", StringComparison.Ordinal) ? Guid.Empty.ToString() : kind == "compactRecord" ? id.Replace("-", "") : id.ToUpperInvariant());
        var raw = body.ToJsonString();
        raw = kind switch { "null" => "null", "empty" => "{}", "duplicate" => raw.Insert(1, "\"expectedRevision\":1,"), "case" => raw.Replace("recordIds", "RecordIds"), "numericRevision" => raw.Replace("\"expectedRevision\":1", "\"expectedRevision\":\"1\""), "numericEpoch" => raw.Replace("\"keyEpoch\":1", "\"keyEpoch\":\"1\""), _ => raw };
        var before = await Snapshot(s); using var request = Request(s, Path(target.CollectionId), Encoding.UTF8.GetBytes(raw), HttpMethod.Put);
        using var response = await Gateway.Client.SendAsync(request); await Failure(response, 400); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("query",400)][InlineData("duplicate",400)][InlineData("bad",400)][InlineData("missing",401)]
    [InlineData("utf8",400)][InlineData("bom",400)][InlineData("compression",400)][InlineData("charset",400)][InlineData("oversized",413)]
    [InlineData("noMedia",400)][InlineData("text",400)][InlineData("suffix",400)][InlineData("prefix",400)][InlineData("brokenMedia",400)]
    public async Task GivenInvalidTransportOrAccess_WhenReplacing_ThenRejectWithoutMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s); var before = await Snapshot(s);
        var raw = kind == "oversized" ? Encoding.UTF8.GetBytes(new string(' ', 65537)) : kind == "utf8" ? [0xc3, 0x28] : Bytes(Replacement());
        if (kind == "bom") raw = new byte[] { 239, 187, 191 }.Concat(raw).ToArray();
        using var request = Request(s, Path(target.CollectionId) + (kind == "query" ? "?ownerId=secret" : ""), raw, HttpMethod.Put);
        if (kind is "missing" or "bad") request.Headers.Remove("X-Cerberus-Vault-Access");
        if (kind == "bad") request.Headers.Add("X-Cerberus-Vault-Access", "bad");
        if (kind == "duplicate") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", s.Access);
        if (kind == "compression") request.Content!.Headers.ContentEncoding.Add("gzip");
        if (kind == "charset") request.Content!.Headers.ContentType!.CharSet = "utf-16";
        if (kind is "noMedia" or "text" or "suffix" or "prefix" or "brokenMedia")
        {
            request.Content!.Headers.Remove("Content-Type");
            if (kind != "noMedia") request.Content.Headers.TryAddWithoutValidation("Content-Type", kind switch { "text" => "text/json", "suffix" => "application/problem+json", "prefix" => "application/json-forged", _ => "application/json; charset=\"utf-8" });
        }
        using var response = await Gateway.Client.SendAsync(request); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("application/json")][InlineData("Application/Json")][InlineData("application/json; charset=utf-8")][InlineData("application/json; charset=\"UTF-8\"")]
    public async Task GivenExactJsonMediaTypeAndUnchangedEnvelope_WhenReplacingEmptyMembership_ThenMetadataOperationSucceeds(string media)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s); var input = Replacement() with { Envelope = target.Envelope };
        using var request = Request(s, Path(target.CollectionId), Bytes(input), HttpMethod.Put); request.Content!.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(media);
        using var response = await Gateway.Client.SendAsync(request); Assert.Equal(2, (await Success<SetCollectionMembershipOutput>(response)).Revision);
    }

    [FunctionalFact]
    public async Task GivenRawEmptyAccessHeader_WhenReplacing_Then400AndNoStore()
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); using var host = new WebApplicationFactory<Program>(); var raw = Bytes(Replacement());
        var context = await host.Server.SendAsync(c => { c.Request.Method = "PUT"; c.Request.Path = Path(Guid.NewGuid()); c.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(s.Actor); c.Request.Headers["X-Cerberus-Vault-Access"] = ""; c.Request.ContentType = "application/json"; c.Request.ContentLength = raw.Length; c.Request.Body = new MemoryStream(raw); });
        Assert.Equal(400, context.Response.StatusCode); Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString());
    }

    [FunctionalTheory][InlineData("missing",401)][InlineData("scope",401)][InlineData("deleted",401)][InlineData("unavailable",503)]
    public async Task GivenRejectedCurrentIdentity_WhenReplacing_ThenNoMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s);
        if (kind is "deleted" or "unavailable") fixture.Users[s.Actor + "@example.test"] = new(s.Actor, "fixture-password", Deleted: kind == "deleted", IdentityUnavailable: kind == "unavailable");
        var before = await Snapshot(s); using var request = Request(s, Path(target.CollectionId), Bytes(Replacement()), HttpMethod.Put);
        if (kind == "missing") request.Headers.Authorization = null;
        if (kind == "scope") request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor, Guid.NewGuid()));
        using var response = await Gateway.Client.SendAsync(request); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("closing",404)][InlineData("terminal",404)][InlineData("revoked",403)][InlineData("expired",403)]
    [InlineData("future",403)][InlineData("policy",403)][InlineData("generation",403)][InlineData("dangling",403)]
    public async Task GivenInvalidCurrentActorState_WhenReplacingWithStaleRevision_ThenCurrentStateWinsWithoutMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var target = await Create(s);
        await using (var db = fixture.Context())
        {
            var session = await db.VaultAccessSessions.SingleAsync(x => x.AccountId == s.InternalId);
            if (kind == "revoked") session.Revoked = true;
            if (kind == "expired") session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1);
            if (kind == "future") session.IssuedAt = DateTimeOffset.UtcNow.AddMinutes(1);
            if (kind == "policy") session.PolicyRevision++;
            if (kind == "generation") session.RevocationGeneration++;
            if (kind == "dangling") session.ProfileId = long.MaxValue;
            if (kind == "closing") await db.Accounts.Where(x => x.Id == s.InternalId).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
            if (kind == "terminal") db.TerminalErasures.Add(new() { ResourceId = s.AccountId, ResourceKind = "account", DeletedAt = DateTimeOffset.UtcNow });
            await db.SaveChangesAsync();
        }
        var before = await Snapshot(s); using var response = await Replace(s, target.CollectionId, Replacement(77)); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }

    private async Task WaitForAccountLock()
    {
        await using var db = fixture.Context(); await db.Database.OpenConnectionAsync(); var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (true)
        {
            await using var command = new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%FROM cerberus.account %' AND query LIKE '%FOR UPDATE%')", connection);
            if ((bool)(await command.ExecuteScalarAsync(timeout.Token))!) return;
            await Task.Delay(20, timeout.Token);
        }
    }
    private async Task Visibility(State s, Guid[] records, Guid[] folders)
    {
        using var recordResponse = await Read(s, "/api/records"); Assert.Equal(records.Order(), (await Success<RecordListOutput>(recordResponse)).Items.Select(x => x.RecordId).Order());
        using var folderResponse = await Read(s, "/api/folders"); Assert.Equal(folders.Order(), (await Success<FolderListOutput>(folderResponse)).Items.Select(x => x.FolderId).Order());
        foreach (var id in records) { using var response = await Read(s, "/api/records/" + id); await Success<RecordDetailsOutput>(response); }
        foreach (var id in folders) { using var response = await Read(s, "/api/folders/" + id); await Success<FolderDetailsOutput>(response); }
    }
    private async Task<CollectionCreateInput> Create(State s, Guid[]? records = null, Guid[]? folders = null)
    {
        var input = new CollectionCreateInput(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch, [], records ?? [], folders ?? []);
        using var request = Request(s, "/api/collections", Bytes(input)); using var response = await Gateway.Client.SendAsync(request); await Success<CreateCollectionOutput>(response, 201); return input;
    }
    private async Task<VaultRecord> Record(State s, Guid? folder = null)
    {
        var input = new RecordCreateInput(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch, [], folder);
        using var request = Request(s, "/api/records", Bytes(input)); using var response = await Gateway.Client.SendAsync(request); await Success<CreateRecordOutput>(response, 201);
        await using var db = fixture.Context(); return await db.Records.AsNoTracking().SingleAsync(x => x.PublicId == input.RecordId);
    }
    private async Task<VaultFolder> Folder(State s, Guid? parent = null)
    {
        var input = new FolderCreateInput(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch, [], parent);
        using var request = Request(s, "/api/folders", Bytes(input)); using var response = await Gateway.Client.SendAsync(request); await Success<CreateFolderOutput>(response, 201);
        await using var db = fixture.Context(); return await db.Folders.AsNoTracking().SingleAsync(x => x.PublicId == input.FolderId);
    }
    private async Task<Profile> Profile(State s, ProtectionFixture owner, ProtectionFixture scoped, string mode = "Master", Guid[]? collections = null, Guid[]? records = null, Guid[]? folders = null)
    {
        var id = Guid.NewGuid(); var input = new ProfileCreateInput(id, Envelope(), new(mode, scoped.Material.UnlockVerifier, owner.Wrap(s.AccountId, "profile", id, s.Actor), mode == "PerProfile" ? scoped.Material.PasswordWrapper : null), DateTimeOffset.UnixEpoch, records ?? [], folders ?? [], collections ?? []);
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
    private async Task<CollectionGrant> Grant(State owner, ProtectionFixture author, State recipient, ProtectionFixture keys, Guid id, CollectionGrantAccess access = CollectionGrantAccess.ReadOnly)
    {
        await using var db = fixture.Context(); var collection = await db.Collections.SingleAsync(x => x.PublicId == id);
        var grant = new CollectionGrant { PublicId = Guid.NewGuid(), CollectionId = collection.Id, RecipientAccountId = recipient.InternalId, Access = access };
        grant.RecipientKeyEnvelope = Bytes(author.WrapGrant(owner.AccountId, id, grant.PublicId, recipient.Actor, keys.Material.RecipientKey)); db.CollectionGrants.Add(grant); await db.SaveChangesAsync(); return grant;
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
    private static CollectionMembershipInput Replacement(long revision = 1, Guid[]? records = null, Guid[]? folders = null) => new(revision, Envelope(), new DateTimeOffset(2026, 10, 12, 0, 0, 0, TimeSpan.Zero).AddTicks(19), records ?? [], folders ?? []);
    private static EncryptedEnvelope Envelope() => new("cerberus-content-v1", 1, ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(12)), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(73)), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(16)));
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ProtectionFixture.Json);
    private static DateTimeOffset Normalize(DateTimeOffset value) => value.AddTicks(-(value.Ticks % 10));
    private static string Path(Guid id) => "/api/collections/" + id + "/members";
    private static HttpRequestMessage Request(State s, string path, byte[]? body = null, HttpMethod? method = null)
    {
        var request = new HttpRequestMessage(method ?? HttpMethod.Post, path); if (body is not null) { request.Content = new ByteArrayContent(body); request.Content.Headers.ContentType = new("application/json"); }
        request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor)); request.Headers.Add("X-Cerberus-Vault-Access", s.Access); return request;
    }
    private async Task<HttpResponseMessage> Replace(State s, Guid id, CollectionMembershipInput input) { using var request = Request(s, Path(id), Bytes(input), HttpMethod.Put); return await Gateway.Client.SendAsync(request); }
    private async Task<HttpResponseMessage> Read(State s, string path) { using var request = Request(s, path, method: HttpMethod.Get); return await Gateway.Client.SendAsync(request); }
    private static async Task<T> Success<T>(HttpResponseMessage response, int status = 200) where T : class
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore);
        var raw = await response.Content.ReadAsStringAsync(); using var document = JsonDocument.Parse(raw); Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        if (typeof(T) == typeof(SetCollectionMembershipOutput))
        {
            Assert.Equal(new[] { "collectionId", "editedAt", "folderIds", "recordIds", "revision", "serverSequence" }, document.RootElement.GetProperty("data").EnumerateObject().Select(x => x.Name).Order());
            Assert.Contains("collection_membership_set", document.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetString()));
        }
        return Assert.IsType<T>(JsonSerializer.Deserialize<DataOutput<T>>(raw, ProtectionFixture.Json)!.Data);
    }
    private static async Task Failure(HttpResponseMessage response, int status, string? error = null)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); var raw = await response.Content.ReadAsStringAsync();
        if (status == 413) { Assert.Equal("", raw); return; }
        using var document = JsonDocument.Parse(raw); Assert.False(document.RootElement.GetProperty("success").GetBoolean());
        if (document.RootElement.TryGetProperty("data", out var data)) Assert.Equal(JsonValueKind.Null, data.ValueKind);
        if (error is not null) Assert.Contains(error, document.RootElement.GetProperty("errors").EnumerateArray().Select(x => x.GetString()));
        Assert.DoesNotContain("private fixture fault", raw);
    }
    private async Task<string> Grants(Guid id) { await using var db = fixture.Context(); return JsonSerializer.Serialize(await db.CollectionGrants.AsNoTracking().Where(x => db.Collections.Any(c => c.Id == x.CollectionId && c.PublicId == id)).OrderBy(x => x.Id).ToArrayAsync()); }
    private async Task<string> Snapshot(State s)
    {
        await using var db = fixture.Context(); return JsonSerializer.Serialize(new
        {
            Account = await db.Accounts.AsNoTracking().SingleAsync(x => x.Id == s.InternalId), Protection = await db.VaultProtections.AsNoTracking().SingleAsync(x => x.AccountId == s.InternalId),
            Sessions = await db.VaultAccessSessions.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(),
            Collections = await db.Collections.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(),
            Records = await db.Records.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(), Folders = await db.Folders.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(),
            Profiles = await db.Profiles.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(),
            CR = await db.CollectionRecords.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.CollectionId).ThenBy(x => x.RecordId).ToArrayAsync(),
            CF = await db.CollectionFolders.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.CollectionId).ThenBy(x => x.FolderId).ToArrayAsync(),
            PC = await db.ProfileCollections.AsNoTracking().Where(x => db.Profiles.Any(p => p.Id == x.ProfileId && p.AccountId == s.InternalId)).OrderBy(x => x.ProfileId).ThenBy(x => x.CollectionId).ToArrayAsync(),
            PR = await db.ProfileRecords.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.ProfileId).ThenBy(x => x.RecordId).ToArrayAsync(),
            PF = await db.ProfileFolders.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.ProfileId).ThenBy(x => x.FolderId).ToArrayAsync()
        });
    }
}
