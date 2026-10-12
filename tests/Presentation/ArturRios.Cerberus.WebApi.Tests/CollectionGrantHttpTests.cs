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

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class CollectionGrantHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory]
    [InlineData("wide", "read-only")][InlineData("wide", "read-write")]
    [InlineData("Master", "read-only")][InlineData("Master", "read-write")]
    [InlineData("PerProfile", "read-only")][InlineData("PerProfile", "read-write")]
    public async Task GivenNativeOwnerAndRecipient_WhenGranting_Then201ExactDirectRootsAndImmediateDynamicVisibility(string mode, string access)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var s = await Setup(owner); var r = await Setup(recipient);
        var folder = await Folder(s); var child = await Folder(s, folder); var inherited = await Record(s, child);
        var direct = await Record(s, folder); var outside = await Record(s); var privateFolder = await Folder(s);
        var target = await Create(s, [outside, direct], [folder]);
        var profile = await Profile(s, owner, key, mode == "wide" ? "Master" : mode, [target]);
        var actor = mode == "wide" ? s : s with { Access = await Open(s, profile, key) };
        var input = Input(s, owner, r, recipient, target, access); var before = await Stable(s, r); var grants = await Grants(target);
        using (var hidden = await Read(r, "/api/collections/" + target)) await Failure(hidden, 404, "not_found");
        using (var response = await Grant(actor, target, input))
        {
            var data = await Success<GrantCollectionAccessOutput>(response, 201);
            Assert.Equal(target, data.CollectionId); Assert.Equal(input.GrantId, data.GrantId); Assert.Equal(1, data.Revision); Assert.Equal(access, data.Access);
            Assert.Equal(new[] { outside, direct }.Order(), data.RecordIds); Assert.Equal(new[] { folder }, data.FolderIds);
        }
        Assert.Equal(before, await Stable(s, r)); Assert.NotEqual(grants, await Grants(target));
        await Visibility(r, [target], [outside, direct, inherited], [folder, child]);
        using (var response = await Read(r, "/api/collections/" + target))
        {
            var data = await Success<CollectionDetailsOutput>(response); Assert.Empty(data.ProfileIds);
            Assert.Equal(new[] { outside, direct }.Order(), data.RecordIds); Assert.Equal(new[] { folder }, data.FolderIds);
        }
        using (var hidden = await Read(r, "/api/folders/" + privateFolder)) await Failure(hidden, 404, "not_found");
        var future = await Record(s, child);
        using (var response = await Read(r, "/api/records/" + future)) await Success<RecordDetailsOutput>(response);
        using (var response = await Read(r, "/api/collections/" + target)) Assert.DoesNotContain(future, (await Success<CollectionDetailsOutput>(response)).RecordIds);
    }

    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenSelectedRecipient_WhenGranting_ThenRemainIsolatedUntilExplicitOwnProfileAssociation(string mode)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var s = await Setup(owner); var r = await Setup(recipient); var folder = await Folder(s); var record = await Record(s, folder);
        var target = await Create(s, [record], [folder]); var p = await Profile(r, recipient, key, mode); var selected = r with { Access = await Open(r, p, key) };
        var before = await Stable(s, r);
        using (var response = await Grant(s, target, Input(s, owner, r, recipient, target))) await Success<GrantCollectionAccessOutput>(response, 201);
        Assert.Equal(before, await Stable(s, r)); await Visibility(selected, [], [], []);
        foreach (var path in new[] { "/api/collections/" + target, "/api/records/" + record, "/api/folders/" + folder })
        { using var response = await Read(selected, path); await Failure(response, 404, "not_found"); }
        using (var request = Request(r, "/api/profiles/" + p.PublicId + "/associations", Bytes(new ProfileAssociationInput(p.Revision, [], [], [target])), HttpMethod.Put))
        using (var response = await Gateway.Client.SendAsync(request)) await Success<SetProfileAssociationsOutput>(response);
        await Visibility(selected, [target], [record], [folder]);
        using (var response = await Read(selected, "/api/collections/" + target)) Assert.Equal(new[] { p.PublicId }, (await Success<CollectionDetailsOutput>(response)).ProfileIds);
    }

    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenUnlinkedSelectedOwner_WhenGranting_Then404WithoutWideningSelection(string mode)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var key = new ProtectionFixture();
        var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s); var p = await Profile(s, owner, key, mode);
        var selected = s with { Access = await Open(s, p, key) }; var before = await Stable(s, r);
        using var response = await Grant(selected, target, Input(s, owner, r, recipient, target)); await Failure(response, 404, "not_found");
        Assert.Equal(before, await Stable(s, r)); Assert.Equal("[]", await Grants(target));
    }

    [FunctionalFact]
    public async Task GivenRecipientWithoutSessionOrProfile_WhenGranting_ThenCreateWithoutProvisioningRecipientState()
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        await using (var db = fixture.Context()) await db.VaultAccessSessions.Where(x => x.AccountId == r.InternalId).ExecuteDeleteAsync();
        var before = await Stable(s, r); using var response = await Grant(s, target, Input(s, owner, r, recipient, target));
        await Success<GrantCollectionAccessOutput>(response, 201); Assert.Equal(before, await Stable(s, r));
    }

    [FunctionalFact]
    public async Task GivenRevokedGrant_WhenExplicitlyRegranting_ThenKeepIdentityAdvanceOnceAndRestoreVisibility()
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient);
        var target = await Create(s); var initial = Input(s, owner, r, recipient, target);
        using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        await using var db = fixture.Context(); var old = await db.CollectionGrants.AsNoTracking().SingleAsync(x => x.PublicId == initial.GrantId);
        await db.CollectionGrants.Where(x => x.Id == old.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked).SetProperty(g => g.RecipientKeyEnvelope, new byte[] { 1 }));
        using (var hidden = await Read(r, "/api/collections/" + target)) await Failure(hidden, 404, "not_found");
        var input = Input(s, owner, r, recipient, target, "read-write", initial.GrantId, 1); var before = await Stable(s, r);
        using (var response = await Grant(s, target, input))
        { var data = await Success<GrantCollectionAccessOutput>(response, 201); Assert.Equal(initial.GrantId, data.GrantId); Assert.Equal(2, data.Revision); Assert.Equal("read-write", data.Access); }
        var current = await db.CollectionGrants.AsNoTracking().SingleAsync(x => x.PublicId == initial.GrantId);
        Assert.Equal(old.Id, current.Id); Assert.Equal(old.CollectionId, current.CollectionId); Assert.Equal(old.RecipientAccountId, current.RecipientAccountId);
        Assert.NotEqual(old.ConcurrencyStamp, current.ConcurrencyStamp); Assert.Equal(Bytes(input.KeyEnvelope), current.RecipientKeyEnvelope);
        Assert.Equal(before, await Stable(s, r)); var winner = await Grants(target);
        using (var replay = await Grant(s, target, input)) await Failure(replay, 409, "revision_conflict");
        Assert.Equal(winner, await Grants(target)); await Visibility(r, [target], [], []);
    }

    [FunctionalTheory][InlineData("active")][InlineData("missingExpected")][InlineData("stale")][InlineData("wrongId")][InlineData("reserved")]
    public async Task GivenConflictingGrantState_WhenPosting_Then409AndPreserveWinner(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target);
        using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        await using (var db = fixture.Context())
        {
            if (kind != "active") await db.CollectionGrants.Where(x => x.PublicId == initial.GrantId).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked));
            if (kind == "reserved") { db.TerminalErasures.Add(new() { ResourceId = initial.GrantId, ResourceKind = "grant", DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        }
        var input = Input(s, owner, r, recipient, target, id: kind == "wrongId" ? null : initial.GrantId, expected: kind is "active" or "missingExpected" ? null : kind == "stale" ? 2 : 1);
        var before = await Stable(s, r); var grants = await Grants(target);
        using var failure = await Grant(s, target, input); await Failure(failure, 409, "revision_conflict"); Assert.Equal(before, await Stable(s, r)); Assert.Equal(grants, await Grants(target));
    }

    [FunctionalTheory][InlineData("read-only", false)][InlineData("read-write", false)][InlineData("read-only", true)][InlineData("read-write", true)]
    public async Task GivenVisibleForeignCollection_WhenGranting_ThenValidateRelevantNativeEvidenceBefore403(string access, bool corrupt)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var initial = Input(s, owner, r, recipient, target, access);
        using (var response = await Grant(s, target, initial)) await Success<GrantCollectionAccessOutput>(response, 201);
        if (corrupt) { await using var db = fixture.Context(); await db.CollectionGrants.Where(x => x.PublicId == initial.GrantId).ExecuteUpdateAsync(x => x.SetProperty(g => g.RecipientKeyEnvelope, new byte[] { 1 })); }
        var before = await Stable(s, r); var grants = await Grants(target);
        using var failure = await Grant(r, target, initial with { RecipientAccountId = Guid.NewGuid() });
        await Failure(failure, corrupt ? 503 : 403, corrupt ? "persistence_unavailable" : "vault_access_denied");
        Assert.Equal(before, await Stable(s, r)); Assert.Equal(grants, await Grants(target));
    }

    [FunctionalTheory][InlineData("owner")][InlineData("collection")][InlineData("epoch")][InlineData("identity")]
    [InlineData("recipientPin")][InlineData("authorPin")][InlineData("signature")][InlineData("revision")][InlineData("grant")]
    public async Task GivenIncorrectNativeBinding_WhenGranting_Then400AndNoMutation(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var other = new ProtectionFixture();
        var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s); var input = Input(s, owner, r, recipient, target);
        var envelope = (kind == "authorPin" ? other : owner).WrapGrant(kind == "owner" ? Guid.NewGuid() : s.AccountId,
            kind == "collection" ? Guid.NewGuid() : target, kind == "grant" ? Guid.NewGuid() : input.GrantId,
            kind == "identity" ? Guid.NewGuid() : r.Actor, kind == "recipientPin" ? other.Material.RecipientKey : recipient.Material.RecipientKey,
            kind == "epoch" ? 2 : 1, kind == "revision" ? 2 : 1);
        if (kind == "signature") envelope = envelope with { Signature = ProtectionFixture.Encode(new byte[64]) };
        var before = await Stable(s, r); using var response = await Grant(s, target, input with { KeyEnvelope = envelope });
        await Failure(response, 400, "validation_failed"); Assert.Equal(before, await Stable(s, r)); Assert.Equal("[]", await Grants(target));
    }

    [FunctionalTheory][InlineData("recipientMissing",404)][InlineData("recipientClosing",404)][InlineData("recipientTerminal",404)][InlineData("unprotected",404)]
    [InlineData("self",400)][InlineData("ownerPins",503)][InlineData("recipientPins",503)][InlineData("collectionEnvelope",503)][InlineData("collectionEpoch",503)]
    [InlineData("ownerClosing",404)][InlineData("ownerTerminal",404)][InlineData("deleted",404)][InlineData("terminal",404)][InlineData("missing",404)]
    [InlineData("expired",403)][InlineData("revoked",403)][InlineData("policy",403)][InlineData("generation",403)]
    public async Task GivenCurrentAccountTargetOrSessionState_WhenGranting_ThenExactSafeFailureWithoutMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var input = Input(s, owner, r, recipient, target);
        await using (var db = fixture.Context())
        {
            if (kind is "recipientMissing" or "self") input = input with { RecipientAccountId = kind == "self" ? s.AccountId : Guid.NewGuid() };
            if (kind is "ownerClosing" or "recipientClosing") await db.Accounts.Where(x => x.Id == (kind == "ownerClosing" ? s.InternalId : r.InternalId)).ExecuteUpdateAsync(x => x.SetProperty(a => a.State, AccountState.ClosurePending));
            if (kind == "unprotected") await db.VaultProtections.Where(x => x.AccountId == r.InternalId).ExecuteDeleteAsync();
            if (kind is "ownerPins" or "recipientPins") await db.VaultProtections.Where(x => x.AccountId == (kind == "ownerPins" ? s.InternalId : r.InternalId)).ExecuteUpdateAsync(x => x.SetProperty(p => p.Material, new byte[] { 1 }));
            if (kind == "collectionEnvelope") await db.Collections.Where(x => x.PublicId == target).ExecuteUpdateAsync(x => x.SetProperty(c => c.Envelope, new byte[] { 1 }));
            if (kind == "collectionEpoch") await db.Collections.Where(x => x.PublicId == target).ExecuteUpdateAsync(x => x.SetProperty(c => c.KeyEpoch, 2));
            if (kind == "deleted") await db.Collections.Where(x => x.PublicId == target).ExecuteUpdateAsync(x => x.SetProperty(c => c.DeletedAt, DateTimeOffset.UtcNow));
            if (kind is "terminal" or "ownerTerminal" or "recipientTerminal") db.TerminalErasures.Add(new() { ResourceKind = kind == "terminal" ? "collection" : "account", ResourceId = kind == "terminal" ? target : kind == "ownerTerminal" ? s.AccountId : r.AccountId, DeletedAt = DateTimeOffset.UtcNow });
            if (kind is "expired" or "revoked" or "policy" or "generation")
            { var session = await db.VaultAccessSessions.SingleAsync(x => x.AccountId == s.InternalId); if (kind == "expired") session.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); if (kind == "revoked") session.Revoked = true; if (kind == "policy") session.PolicyRevision++; if (kind == "generation") session.RevocationGeneration++; }
            await db.SaveChangesAsync();
        }
        var before = await Stable(s, r); using var response = await Grant(s, kind == "missing" ? Guid.NewGuid() : target, input);
        await Failure(response, status, status switch { 400 => "validation_failed", 403 => "vault_access_denied", 404 => "not_found", _ => "persistence_unavailable" });
        Assert.Equal(before, await Stable(s, r)); Assert.Equal("[]", await Grants(target));
    }

    [FunctionalTheory][InlineData("outage")][InlineData("insert")][InlineData("regrant")]
    public async Task GivenDatabaseFailure_WhenGranting_Then503AtomicRollbackAndCleanRetry(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s); var input = Input(s, owner, r, recipient, target);
        await using var db = fixture.Context();
        if (kind == "regrant")
        { using var initial = await Grant(s, target, input); await Success<GrantCollectionAccessOutput>(initial, 201); await db.CollectionGrants.Where(x => x.PublicId == input.GrantId).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked)); input = Input(s, owner, r, recipient, target, id: input.GrantId, expected: 1); }
        var before = await Stable(s, r); var grants = await Grants(target);
        if (kind == "outage") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection_grant RENAME TO fixture_unavailable_grant");
        else await db.Database.ExecuteSqlRawAsync("CREATE FUNCTION cerberus.grant_http_fault() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'private fixture fault'; END $$; CREATE TRIGGER grant_http_fault AFTER INSERT OR UPDATE ON cerberus.collection_grant FOR EACH ROW EXECUTE FUNCTION cerberus.grant_http_fault()");
        try { using var response = await Grant(s, target, input); await Failure(response, 503, "persistence_unavailable"); }
        finally { if (kind == "outage") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_grant RENAME TO collection_grant"); else await db.Database.ExecuteSqlRawAsync("DROP TRIGGER grant_http_fault ON cerberus.collection_grant; DROP FUNCTION cerberus.grant_http_fault()"); }
        Assert.Equal(before, await Stable(s, r)); Assert.Equal(grants, await Grants(target));
        using var retry = await Grant(s, target, input); await Success<GrantCollectionAccessOutput>(retry, 201);
    }

    [FunctionalTheory][InlineData("grantId")][InlineData("recipientAccountId")][InlineData("expectedRevision")][InlineData("access")][InlineData("keyEnvelope")]
    public async Task GivenMissingRequiredField_WhenGranting_Then400WithoutMutation(string field)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var body = JsonSerializer.SerializeToNode(Input(s, owner, r, recipient, target), ProtectionFixture.Json)!.AsObject(); body.Remove(field);
        using var request = Request(s, Path(target), Encoding.UTF8.GetBytes(body.ToJsonString())); using var response = await Gateway.Client.SendAsync(request);
        await Failure(response, 400, "validation_failed"); Assert.Equal("[]", await Grants(target));
    }

    [FunctionalTheory][InlineData("duplicate")][InlineData("case")][InlineData("unknown")][InlineData("context")]
    [InlineData("stringRevision")][InlineData("fractionRevision")][InlineData("zeroRevision")][InlineData("negativeRevision")][InlineData("maxRevision")][InlineData("unsafeRevision")]
    [InlineData("nullAccess")][InlineData("caseAccess")][InlineData("nullEnvelope")][InlineData("duplicateEpoch")][InlineData("caseEpoch")][InlineData("unknownEnvelope")]
    [InlineData("upperGrant")][InlineData("compactRecipient")][InlineData("zeroGrant")][InlineData("zeroRecipient")]
    public async Task GivenInvalidStrictJson_WhenGranting_Then400WithoutMutation(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        var body = JsonSerializer.SerializeToNode(Input(s, owner, r, recipient, target), ProtectionFixture.Json)!.AsObject();
        switch (kind)
        {
            case "case": body.Remove("expectedRevision"); body["ExpectedRevision"] = null; break;
            case "unknown": body["plaintext"] = "secret"; break; case "context": body["collectionId"] = target.ToString(); break;
            case "stringRevision": body["expectedRevision"] = "1"; break; case "fractionRevision": body["expectedRevision"] = 1.5; break;
            case "zeroRevision": body["expectedRevision"] = 0; break; case "negativeRevision": body["expectedRevision"] = -1; break;
            case "maxRevision": body["expectedRevision"] = 9007199254740991L; break; case "unsafeRevision": body["expectedRevision"] = 9007199254740992L; break;
            case "nullAccess": body["access"] = null; break; case "caseAccess": body["access"] = "Read-Only"; break;
            case "nullEnvelope": body["keyEnvelope"] = null; break;
            case "caseEpoch": body["keyEnvelope"]!.AsObject().Remove("keyEpoch"); body["keyEnvelope"]!["KeyEpoch"] = 1; break;
            case "unknownEnvelope": body["keyEnvelope"]!["ownerId"] = s.AccountId.ToString(); break;
            case "upperGrant": body["grantId"] = "AAAAAAAA-AAAA-4AAA-8AAA-AAAAAAAAAAAA"; break;
            case "compactRecipient": body["recipientAccountId"] = r.AccountId.ToString("N"); break;
            case "zeroGrant": body["grantId"] = Guid.Empty.ToString(); break; case "zeroRecipient": body["recipientAccountId"] = Guid.Empty.ToString(); break;
        }
        var raw = body.ToJsonString(); if (kind == "duplicate") raw = raw.Insert(1, "\"expectedRevision\":null,");
        if (kind == "duplicateEpoch") raw = raw.Replace("\"keyEpoch\":1", "\"keyEpoch\":1,\"keyEpoch\":1");
        var before = await Stable(s, r); using var request = Request(s, Path(target), Encoding.UTF8.GetBytes(raw)); using var response = await Gateway.Client.SendAsync(request);
        await Failure(response, 400, "validation_failed"); Assert.Equal(before, await Stable(s, r)); Assert.Equal("[]", await Grants(target));
    }

    [FunctionalTheory][InlineData("bad")][InlineData("zero")][InlineData("uppercase")][InlineData("compact")][InlineData("braced")][InlineData("urn")]
    public async Task GivenNoncanonicalRoute_WhenGranting_Then400(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var id = Guid.Parse("aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa");
        var text = kind switch { "bad" => "bad", "zero" => Guid.Empty.ToString(), "uppercase" => id.ToString().ToUpperInvariant(), "compact" => id.ToString("N"), "braced" => id.ToString("B"), _ => "urn:uuid:" + id };
        using var request = Request(s, "/api/collections/" + text + "/grants", Bytes(Input(s, owner, r, recipient, id))); using var response = await Gateway.Client.SendAsync(request);
        await Failure(response, 400, "validation_failed");
    }

    [FunctionalTheory][InlineData("query",400)][InlineData("duplicate",400)][InlineData("bad",400)][InlineData("missing",401)]
    [InlineData("utf8",400)][InlineData("bom",400)][InlineData("compression",400)][InlineData("charset",400)][InlineData("oversized",413)]
    [InlineData("noMedia",400)][InlineData("text",400)][InlineData("suffix",400)][InlineData("prefix",400)][InlineData("brokenMedia",400)]
    public async Task GivenInvalidTransportOrAccess_WhenGranting_ThenStrictFailureWithoutMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s); var before = await Stable(s, r);
        var raw = kind == "oversized" ? Encoding.UTF8.GetBytes(new string(' ', 65537)) : kind == "utf8" ? [0xc3, 0x28] : Bytes(Input(s, owner, r, recipient, target));
        if (kind == "bom") raw = new byte[] { 239, 187, 191 }.Concat(raw).ToArray();
        using var request = Request(s, Path(target) + (kind == "query" ? "?profileId=secret" : ""), raw);
        if (kind is "missing" or "bad") request.Headers.Remove("X-Cerberus-Vault-Access");
        if (kind == "bad") request.Headers.Add("X-Cerberus-Vault-Access", "bad");
        if (kind == "duplicate") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", s.Access);
        if (kind == "compression") request.Content!.Headers.ContentEncoding.Add("gzip");
        if (kind == "charset") request.Content!.Headers.ContentType!.CharSet = "utf-16";
        if (kind is "noMedia" or "text" or "suffix" or "prefix" or "brokenMedia")
        { request.Content!.Headers.Remove("Content-Type"); if (kind != "noMedia") request.Content.Headers.TryAddWithoutValidation("Content-Type", kind switch { "text" => "text/json", "suffix" => "application/problem+json", "prefix" => "application/json-forged", _ => "application/json; charset=\"utf-8" }); }
        using var response = await Gateway.Client.SendAsync(request); await Failure(response, status, status == 401 ? "vault_access_required" : status == 400 ? "validation_failed" : null);
        Assert.Equal(before, await Stable(s, r)); Assert.Equal("[]", await Grants(target));
    }

    [FunctionalTheory][InlineData("application/json")][InlineData("Application/Json")][InlineData("application/json; charset=utf-8")][InlineData("application/json; charset=\"UTF-8\"")]
    public async Task GivenExactJsonMediaType_WhenGranting_Then201(string media)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        using var request = Request(s, Path(target), Bytes(Input(s, owner, r, recipient, target))); request.Content!.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(media);
        using var response = await Gateway.Client.SendAsync(request); await Success<GrantCollectionAccessOutput>(response, 201);
    }

    [FunctionalFact]
    public async Task GivenRawEmptyAccessHeader_WhenGranting_Then400AndNoStore()
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        using var host = new WebApplicationFactory<Program>(); var raw = Bytes(Input(s, owner, r, recipient, target));
        var context = await host.Server.SendAsync(c => { c.Request.Method = "POST"; c.Request.Path = Path(target); c.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(s.Actor); c.Request.Headers["X-Cerberus-Vault-Access"] = ""; c.Request.ContentType = "application/json"; c.Request.ContentLength = raw.Length; c.Request.Body = new MemoryStream(raw); });
        Assert.Equal(400, context.Response.StatusCode); Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString()); Assert.Equal("[]", await Grants(target));
    }

    [FunctionalTheory][InlineData("missing",401)][InlineData("scope",401)][InlineData("deleted",401)][InlineData("unavailable",503)]
    public async Task GivenRejectedCurrentIdentity_WhenGranting_ThenSafeFailureWithoutMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var s = await Setup(owner); var r = await Setup(recipient); var target = await Create(s);
        if (kind is "deleted" or "unavailable") fixture.Users[s.Actor + "@example.test"] = new(s.Actor, "fixture-password", Deleted: kind == "deleted", IdentityUnavailable: kind == "unavailable");
        using var request = Request(s, Path(target), Bytes(Input(s, owner, r, recipient, target))); if (kind == "missing") request.Headers.Authorization = null;
        if (kind == "scope") request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor, Guid.NewGuid()));
        var before = await Stable(s, r); using var response = await Gateway.Client.SendAsync(request); await Failure(response, status, status == 401 ? "authentication_required" : "identity_unavailable");
        Assert.Equal(before, await Stable(s, r)); Assert.Equal("[]", await Grants(target));
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
        if (typeof(T) == typeof(GrantCollectionAccessOutput))
        {
            Assert.Equal(new[] { "access", "collectionId", "folderIds", "grantId", "recordIds", "revision" }, document.RootElement.GetProperty("data").EnumerateObject().Select(x => x.Name).Order());
            Assert.Equal(new[] { "collection_access_granted" }, document.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetString()));
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
    private async Task<string> Stable(State s, State r)
    {
        await using var db = fixture.Context();
        return await db.Database.SqlQuery<string>($"""
            SELECT jsonb_build_object(
              'accounts',(SELECT jsonb_agg(to_jsonb(a) ORDER BY id) FROM cerberus.account a WHERE id IN ({s.InternalId},{r.InternalId})),
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
