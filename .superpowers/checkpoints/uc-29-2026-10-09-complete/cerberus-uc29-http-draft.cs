using System.Net;
using System.Net.Http.Json;
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
using ArturRios.Cerberus.Query.Folders;
using ArturRios.Cerberus.Query.Profiles;
using ArturRios.Cerberus.Query.Records;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Configuration.Enums;
using ArturRios.Output;
using ArturRios.Util.Test.Functional;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Registration host")]
public class CollectionCreateHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalTheory][InlineData("empty")][InlineData("profiles")][InlineData("mixed")][InlineData("overlap")]
    public async Task GivenActualOwnedProfilesFoldersAndRecords_WhenCreatingCollection_ThenReturnExactMetadataAndExposeInitialRelationships(string kind)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); using var second = new ProtectionFixture(); var s = await Setup(owner);
        var p = await Profile(s, owner, scoped); var q = await Profile(s, owner, second); var root = await Folder(s); var child = await Folder(s, root.PublicId); var record = await Record(s, child.PublicId);
        var input = Input(kind == "empty" ? [] : [p.PublicId, q.PublicId], kind is "mixed" or "overlap" ? [root.PublicId] : [], kind == "overlap" ? [record.PublicId] : []);
        var before = await Snapshot(s); using var response = await Send(s, input); var output = await Success<CreateCollectionOutput>(response, 201); await CheckCreated(s, input, before, output);
        foreach (var id in input.ProfileIds)
        { using var get = await Read(s, "/api/profiles/" + id); Assert.Contains(input.CollectionId, (await Success<ProfileDetailsOutput>(get, 200)).CollectionIds); }
        using var list = await Read(s, "/api/profiles"); var profiles = await Success<ProfileListOutput>(list, 200); foreach (var id in input.ProfileIds) Assert.Contains(input.CollectionId, profiles.Items.Single(x => x.ProfileId == id).CollectionIds);
        if (input.FolderIds.Length != 0)
        {
            using var get = await Read(s, "/api/folders/" + child.PublicId); Assert.Contains(input.CollectionId, (await Success<FolderDetailsOutput>(get, 200)).CollectionIds);
            using var recordGet = await Read(s, "/api/records/" + record.PublicId); Assert.Contains(input.CollectionId, (await Success<RecordDetailsOutput>(recordGet, 200)).CollectionIds);
        }
    }

    [FunctionalTheory][InlineData("Master", "PF")][InlineData("PerProfile", "PF")][InlineData("Master", "PC")][InlineData("PerProfile", "PC")]
    [InlineData("Master", "PR")][InlineData("PerProfile", "PR")]
    public async Task GivenActualNativeSelectedAccess_WhenCreating_ThenKeepSelectionAndCurrentTypedMemberVisibility(string mode, string route)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); using var otherKey = new ProtectionFixture(); var s = await Setup(owner);
        var p = await Profile(s, owner, scoped, mode); var other = await Profile(s, owner, otherKey); var root = await Folder(s, profiles: route == "PF" ? [p.PublicId] : []); var child = await Folder(s, root.PublicId); var r = await Record(s, child.PublicId, route == "PR" ? [p.PublicId] : []);
        if (route == "PC") { using var initial = await Send(s, Input([p.PublicId], [root.PublicId])); await Success<CreateCollectionOutput>(initial, 201); }
        var selected = s with { Access = await Open(s, p.PublicId, scoped) }; var input = Input([p.PublicId], route == "PR" ? [] : [root.PublicId], [r.PublicId]); var before = await Snapshot(s);
        using var created = await Send(selected, input); await CheckCreated(s, input, before, await Success<CreateCollectionOutput>(created, 201));
        using var getProfile = await Read(selected, "/api/profiles/" + p.PublicId); Assert.Contains(input.CollectionId, (await Success<ProfileDetailsOutput>(getProfile, 200)).CollectionIds);
        using var getRecord = await Read(selected, "/api/records/" + r.PublicId); var detail = await Success<RecordDetailsOutput>(getRecord, 200); Assert.Contains(input.CollectionId, detail.CollectionIds);
        if (route == "PR")
        { Assert.Null(detail.FolderId); using var hidden = await Read(selected, "/api/folders/" + child.PublicId); await Failure(hidden, 404); }
        else
        {
            using var getFolder = await Read(selected, "/api/folders/" + child.PublicId); Assert.Contains(input.CollectionId, (await Success<FolderDetailsOutput>(getFolder, 200)).CollectionIds);
            using var folders = await Read(selected, "/api/folders"); Assert.Contains((await Success<FolderListOutput>(folders, 200)).Items, x => x.FolderId == child.PublicId);
        }
        using var records = await Read(selected, "/api/records"); Assert.Contains((await Success<RecordListOutput>(records, 200)).Items, x => x.RecordId == r.PublicId);
        using var hiddenProfile = await Read(selected, "/api/profiles/" + other.PublicId); await Failure(hiddenProfile, 404);
        OpaqueAccessHandle.TryHash(selected.Access, out var verifier); await using var db = fixture.Context(); Assert.Equal(p.Id, (await db.VaultAccessSessions.SingleAsync(x => x.HandleVerifier == verifier)).ProfileId);
    }

    [FunctionalTheory][InlineData("empty", 403)][InlineData("other", 404)][InlineData("multiple", 404)][InlineData("folder", 404)][InlineData("record", 404)][InlineData("recordOnlyFolder", 404)]
    public async Task GivenActualSelectedHandle_WhenExpandingInitialMembership_ThenRejectAndPreserveGraph(string kind, int status)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); using var otherKey = new ProtectionFixture(); var s = await Setup(owner); var p = await Profile(s, owner, scoped); var q = await Profile(s, owner, otherKey); var f = await Folder(s); var r = await Record(s, f.PublicId, kind == "recordOnlyFolder" ? [p.PublicId] : []);
        s = s with { Access = await Open(s, p.PublicId, scoped) }; var input = Input(kind == "empty" ? [] : kind == "other" ? [q.PublicId] : kind == "multiple" ? [p.PublicId, q.PublicId] : [p.PublicId], kind is "folder" or "recordOnlyFolder" ? [f.PublicId] : [], kind == "record" ? [r.PublicId] : []);
        var before = await Snapshot(s); using var response = await Send(s, input); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly, false)][InlineData(CollectionGrantAccess.ReadWrite, false)][InlineData(CollectionGrantAccess.ReadOnly, true)][InlineData(CollectionGrantAccess.ReadWrite, true)]
    public async Task GivenActuallyReadableForeignNativeMember_WhenCreating_Then404AndBothGraphsUnchanged(CollectionGrantAccess access, bool folder)
    {
        using var owner = new ProtectionFixture(); using var client = new ProtectionFixture(); var source = await Setup(owner); var s = await Setup(client); var f = await Folder(source); var r = await Record(source, f.PublicId);
        var c = new VaultCollection { PublicId = Guid.NewGuid(), AccountId = source.InternalId, Envelope = Bytes(Envelope()), EditedAt = DateTimeOffset.UnixEpoch };
        await using (var db = fixture.Context())
        {
            db.Collections.Add(c); await db.SaveChangesAsync(); db.CollectionFolders.Add(new() { AccountId = source.InternalId, CollectionId = c.Id, FolderId = f.Id }); db.CollectionRecords.Add(new() { AccountId = source.InternalId, CollectionId = c.Id, RecordId = r.Id });
            var id = Guid.NewGuid(); db.CollectionGrants.Add(new() { PublicId = id, CollectionId = c.Id, RecipientAccountId = s.InternalId, Access = access, RecipientKeyEnvelope = Bytes(owner.WrapGrant(source.AccountId, c.PublicId, id, s.Actor, client.Material.RecipientKey)) }); await db.SaveChangesAsync();
        }
        using var visible = await Read(s, folder ? "/api/folders/" + f.PublicId : "/api/records/" + r.PublicId); Assert.Equal(HttpStatusCode.OK, visible.StatusCode); Assert.True(visible.Headers.CacheControl?.NoStore);
        var before = await Snapshot(s); var foreign = await Snapshot(source); using var response = await Send(s, Input(folders: folder ? [f.PublicId] : [], records: folder ? [] : [r.PublicId])); await Failure(response, 404, "not_found"); Assert.Equal(before, await Snapshot(s)); Assert.Equal(foreign, await Snapshot(source));
    }

    [FunctionalTheory][InlineData("profile", "missing")][InlineData("folder", "missing")][InlineData("record", "missing")]
    [InlineData("profile", "foreign")][InlineData("folder", "foreign")][InlineData("record", "foreign")]
    [InlineData("profile", "trash")][InlineData("folder", "trash")][InlineData("record", "trash")]
    [InlineData("profile", "terminal")][InlineData("folder", "terminal")][InlineData("record", "terminal")]
    public async Task GivenHiddenTypedInitialRelationship_WhenCreating_ThenNonrevealing404WithoutChanges(string kind, string state)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await Setup(owner); var foreign = await Setup(owner); var targetOwner = state == "foreign" ? foreign : s;
        var p = await Profile(targetOwner, owner, scoped); var f = await Folder(targetOwner); var r = await Record(targetOwner); var id = kind == "profile" ? p.PublicId : kind == "folder" ? f.PublicId : r.PublicId;
        await using (var db = fixture.Context())
        {
            if (state == "terminal") { db.TerminalErasures.Add(new() { ResourceKind = kind, ResourceId = id, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
            if (state == "trash") { var sql = "UPDATE cerberus." + kind + " SET deleted_at=statement_timestamp() WHERE public_id={0}"; await db.Database.ExecuteSqlRawAsync(sql, id); }
        }
        if (state == "missing") id = Guid.NewGuid(); var input = Input(kind == "profile" ? [id] : [], kind == "folder" ? [id] : [], kind == "record" ? [id] : []);
        var before = await Snapshot(s); var other = await Snapshot(foreign); using var response = await Send(s, input); await Failure(response, 404, "not_found"); Assert.Equal(before, await Snapshot(s)); Assert.Equal(other, await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData("closing", 404)][InlineData("terminal", 404)][InlineData("revoked", 403)][InlineData("expired", 403)][InlineData("future", 403)][InlineData("policy", 403)][InlineData("generation", 403)][InlineData("selection", 403)]
    public async Task GivenInvalidCurrentAccountOrSession_WhenCreating_ThenFailClosedWithoutMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); await using (var db = fixture.Context())
        {
            var a = await db.Accounts.SingleAsync(x => x.Id == s.InternalId); var v = await db.VaultAccessSessions.SingleAsync(x => x.AccountId == s.InternalId);
            if (kind == "closing") a.State = AccountState.ClosurePending; if (kind == "terminal") db.TerminalErasures.Add(new() { ResourceKind = "account", ResourceId = s.AccountId, DeletedAt = DateTimeOffset.UtcNow });
            if (kind == "revoked") v.Revoked = true; if (kind == "expired") v.ExpiresAt = DateTimeOffset.UtcNow.AddMinutes(-1); if (kind == "future") v.IssuedAt = DateTimeOffset.UtcNow.AddMinutes(1); if (kind == "policy") v.PolicyRevision++; if (kind == "generation") v.RevocationGeneration++; if (kind == "selection") v.ProfileId = long.MaxValue; await db.SaveChangesAsync();
        }
        var before = await Snapshot(s); using var response = await Send(s, Input()); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("missing", 401)][InlineData("wrongScope", 401)][InlineData("deleted", 401)][InlineData("unavailable", 503)]
    public async Task GivenInvalidCurrentHeimdallIdentity_WhenCreating_ThenRejectBeforePersistence(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); if (kind is "deleted" or "unavailable") fixture.Users[s.Actor + "@example.test"] = new(s.Actor, "fixture-password", Deleted: kind == "deleted", IdentityUnavailable: kind == "unavailable");
        var before = await Snapshot(s); using var request = Request(s, Bytes(Input())); request.Headers.Authorization = kind == "missing" ? null : new("Bearer", RegistrationApiFixture.Token(s.Actor, kind == "wrongScope" ? Guid.NewGuid() : null)); using var response = await Gateway.Client.SendAsync(request); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("active")][InlineData("trash")][InlineData("foreign")][InlineData("terminal")]
    public async Task GivenRetainedOrReservedCollectionId_WhenCreating_ThenConflictAndPreserveWinner(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var other = kind == "foreign" ? await Setup(owner) : s; var input = Input();
        await using (var db = fixture.Context())
        { if (kind == "terminal") db.TerminalErasures.Add(new() { ResourceKind = "collection", ResourceId = input.CollectionId, DeletedAt = DateTimeOffset.UtcNow }); else db.Collections.Add(new() { PublicId = input.CollectionId, AccountId = other.InternalId, Envelope = Bytes(Envelope()), EditedAt = DateTimeOffset.UnixEpoch, DeletedAt = kind == "trash" ? DateTimeOffset.UtcNow : null }); await db.SaveChangesAsync(); }
        var before = await Snapshot(s); var foreign = await Snapshot(other); using var response = await Send(s, input); await Failure(response, 409, "revision_conflict"); Assert.Equal(before, await Snapshot(s)); Assert.Equal(foreign, await Snapshot(other));
    }

    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenConcurrentCreationAndLostResponseRetry_WhenCreating_ThenOneWinnerAndNoSecondLinks(bool differentOwners)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var other = differentOwners ? await Setup(owner) : s; var input = Input(); var responses = await Task.WhenAll(Send(s, input), Send(other, input));
        try { await Success<CreateCollectionOutput>(Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Created), 201); await Failure(Assert.Single(responses, x => x.StatusCode != HttpStatusCode.Created), 409); }
        finally { foreach (var response in responses) response.Dispose(); }
        var before = await Snapshot(s); using var retry = await Send(s, input); await Failure(retry, 409); Assert.Equal(before, await Snapshot(s)); await using var db = fixture.Context(); Assert.Single(await db.Collections.Where(x => x.PublicId == input.CollectionId).ToArrayAsync());
    }

    public static TheoryData<string> InvalidBodies => new(
        "missingCollectionId", "missingEnvelope", "missingEditedAt", "missingProfileIds", "missingFolderIds", "missingRecordIds",
        "nullEnvelope", "nullProfiles", "nullFolders", "nullRecords", "zeroId", "duplicateProfiles", "duplicateFolders", "duplicateRecords",
        "zeroProfile", "zeroFolder", "zeroRecord", "upperId", "compactId", "upperProfile", "upperFolder", "upperRecord", "numericId", "numericFolder",
        "badFormat", "epoch0", "epoch2", "badSalt", "paddedNonce", "emptyCiphertext", "badTag", "timeOffset", "timeDefault", "time9Ticks",
        "actor", "accountId", "vaultAccess", "name", "fields", "keyWrappers", "grants", "proof", "expectedRevision", "parentFolderId", "collectionIds", "unknown",
        "duplicate", "case", "numericEpoch", "fractionEpoch", "nestedUnknown", "nestedMissing", "nestedDuplicate", "nestedCase", "numericProfiles", "numericRecords",
        "invalidUtf8", "bom", "gzip", "charset", "oversized", "null", "array");

    [FunctionalFact]
    public async Task GivenUnmodifiedStrictBodyFixture_WhenCreating_ThenAcceptAllSixFieldsAndReturnExactMetadata()
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var body = StrictBody("valid");
        Assert.Equal(new[] { "collectionId", "editedAt", "envelope", "folderIds", "profileIds", "recordIds" }, JsonNode.Parse(body)!.AsObject().Select(x => x.Key).Order());
        var before = await Snapshot(s); using var request = Request(s, Encoding.UTF8.GetBytes(body)); using var response = await Gateway.Client.SendAsync(request);
        var output = await Success<CreateCollectionOutput>(response, 201); await CheckCreated(s, JsonSerializer.Deserialize<CollectionCreateInput>(body, ProtectionFixture.Json)!, before, output);
    }

    [FunctionalTheory][MemberData(nameof(InvalidBodies))]
    public async Task GivenMalformedOrForgedBody_WhenCreating_ThenRejectIntendedDefectWithoutMutation(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var body = StrictBody(kind); var bytes = kind == "invalidUtf8" ? new byte[] { 255 } : kind == "bom" ? new byte[] { 239, 187, 191 }.Concat(Encoding.UTF8.GetBytes(body)).ToArray() : Encoding.UTF8.GetBytes(body);
        var before = await Snapshot(s); using var request = Request(s, bytes); if (kind == "gzip") request.Content!.Headers.ContentEncoding.Add("gzip"); if (kind == "charset") request.Content!.Headers.ContentType!.CharSet = "iso-8859-1";
        using var response = await Gateway.Client.SendAsync(request); await Failure(response, kind == "oversized" ? 413 : 400, kind == "oversized" ? null : "validation_failed"); Assert.Equal(before, await Snapshot(s));
    }

    private static string StrictBody(string kind)
    {
        var body = JsonSerializer.SerializeToNode(Input(), ProtectionFixture.Json)!.AsObject(); var envelope = body["envelope"]!.AsObject();
        var missing = kind switch { "missingCollectionId" => "collectionId", "missingEnvelope" => "envelope", "missingEditedAt" => "editedAt", "missingProfileIds" => "profileIds", "missingFolderIds" => "folderIds", "missingRecordIds" => "recordIds", _ => null }; if (missing is not null) body.Remove(missing);
        if (kind == "nullEnvelope") body["envelope"] = null; if (kind == "nullProfiles") body["profileIds"] = null; if (kind == "nullFolders") body["folderIds"] = null; if (kind == "nullRecords") body["recordIds"] = null;
        if (kind == "zeroId") body["collectionId"] = Guid.Empty;
        var array = kind is "duplicateProfiles" or "zeroProfile" or "upperProfile" or "numericProfiles" ? "profileIds" : kind is "duplicateFolders" or "zeroFolder" or "upperFolder" or "numericFolder" ? "folderIds" : "recordIds";
        if (kind.StartsWith("duplicate", StringComparison.Ordinal) && kind != "duplicate") body[array] = new JsonArray(JsonValue.Create("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"), JsonValue.Create("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"));
        if (kind is "zeroProfile" or "zeroFolder" or "zeroRecord") body[array] = new JsonArray(JsonValue.Create(Guid.Empty));
        if (kind is "upperProfile" or "upperFolder" or "upperRecord") body[array] = new JsonArray(JsonValue.Create("AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"));
        if (kind is "numericProfiles" or "numericFolder" or "numericRecords") body[array] = new JsonArray(JsonValue.Create(1));
        if (kind == "upperId") body["collectionId"] = "AAAAAAAA-AAAA-AAAA-AAAA-AAAAAAAAAAAA"; if (kind == "compactId") body["collectionId"] = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"; if (kind == "numericId") body["collectionId"] = 1;
        if (kind == "badFormat") envelope["format"] = "unknown"; if (kind is "epoch0" or "epoch2") envelope["keyEpoch"] = kind == "epoch0" ? 0 : 2;
        if (kind == "badSalt") envelope["keySalt"] = "bad"; if (kind == "paddedNonce") envelope["nonce"] = envelope["nonce"]!.GetValue<string>() + "="; if (kind == "emptyCiphertext") envelope["ciphertext"] = ""; if (kind == "badTag") envelope["tag"] = "bad";
        if (kind == "timeOffset") body["editedAt"] = DateTimeOffset.UnixEpoch.ToOffset(TimeSpan.FromHours(1)); if (kind == "timeDefault") body["editedAt"] = default(DateTimeOffset); if (kind == "time9Ticks") body["editedAt"] = new DateTimeOffset(9, TimeSpan.Zero);
        if (kind is "actor" or "accountId" or "vaultAccess" or "name" or "fields" or "keyWrappers" or "grants" or "proof" or "expectedRevision" or "parentFolderId" or "collectionIds" or "unknown") body[kind] = null;
        if (kind == "numericEpoch") envelope["keyEpoch"] = "1"; if (kind == "fractionEpoch") envelope["keyEpoch"] = 1.5; if (kind == "nestedUnknown") envelope["name"] = "plaintext"; if (kind == "nestedMissing") envelope.Remove("keyEpoch");
        var raw = body.ToJsonString(); if (kind == "duplicate") raw = raw.Insert(1, "\"profileIds\":[],"); if (kind == "case") raw = raw.Replace("profileIds", "ProfileIds"); if (kind == "nestedCase") raw = raw.Replace("keyEpoch", "KeyEpoch"); if (kind == "nestedDuplicate") raw = raw.Replace("\"envelope\":{", "\"envelope\":{\"keyEpoch\":1,"); if (kind == "null") raw = "null"; if (kind == "array") raw = "[]"; if (kind == "oversized") raw = new string(' ', 65537) + raw;
        return raw;
    }

    [FunctionalTheory][InlineData("missing", 401)][InlineData("bad", 400)][InlineData("duplicate", 400)][InlineData("empty", 400)][InlineData("query", 400)][InlineData("profileOverride", 400)]
    public async Task GivenMissingOrMalformedAccessOrQuery_WhenCreating_ThenRejectWithoutChanges(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var raw = Bytes(Input()); var before = await Snapshot(s);
        if (kind == "empty")
        {
            using var host = new WebApplicationFactory<Program>(); var context = await host.Server.SendAsync(c =>
            { c.Request.Method = "POST"; c.Request.Path = "/api/collections"; c.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(s.Actor); c.Request.Headers["X-Cerberus-Vault-Access"] = ""; c.Request.ContentType = "application/json"; c.Request.Body = new MemoryStream(raw); c.Request.ContentLength = raw.Length; });
            Assert.Equal(status, context.Response.StatusCode); Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString());
        }
        else
        {
            using var request = Request(s, raw, kind == "query" ? "/api/collections?unknown=1" : kind == "profileOverride" ? "/api/collections?profileId=" + Guid.NewGuid() : "/api/collections");
            if (kind == "missing") request.Headers.Remove("X-Cerberus-Vault-Access"); if (kind == "bad") { request.Headers.Remove("X-Cerberus-Vault-Access"); request.Headers.Add("X-Cerberus-Vault-Access", "bad"); } if (kind == "duplicate") request.Headers.Add("X-Cerberus-Vault-Access", s.Access);
            using var response = await Gateway.Client.SendAsync(request); await Failure(response, status);
        }
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("minimum")][InlineData("pre2000")][InlineData("pre2000Last")][InlineData("maximum")][InlineData("precision")]
    public async Task GivenSupportedClientTimestamp_WhenCreating_ThenReturnTheStoredMicrosecondFloor(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var input = Input() with { EditedAt = kind switch { "minimum" => new(10, TimeSpan.Zero), "pre2000" => new DateTimeOffset(1999, 12, 31, 23, 59, 59, TimeSpan.Zero).AddTicks(19), "pre2000Last" => new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero).AddTicks(-1), "maximum" => DateTimeOffset.MaxValue, _ => DateTimeOffset.UnixEpoch.AddTicks(19) } };
        var before = await Snapshot(s); using var response = await Send(s, input); await CheckCreated(s, input, before, await Success<CreateCollectionOutput>(response, 201));
    }

    [FunctionalTheory][InlineData("account")][InlineData("profile")][InlineData("folder")][InlineData("record")][InlineData("grant")]
    public async Task GivenOtherKindTerminalMarker_WhenCreatingCollection_ThenTypedIdentitySurvives(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var input = Input(); await using (var db = fixture.Context()) { db.TerminalErasures.Add(new() { ResourceKind = kind, ResourceId = input.CollectionId, DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync(); }
        var before = await Snapshot(s); using var response = await Send(s, input); await CheckCreated(s, input, before, await Success<CreateCollectionOutput>(response, 201)); await using var check = fixture.Context(); Assert.True(await check.TerminalErasures.AnyAsync(x => x.ResourceKind == kind && x.ResourceId == input.CollectionId));
    }

    [FunctionalFact]
    public async Task GivenSamePublicIdInDifferentTypedMembers_WhenCreatingCollection_ThenPersistEachIndependentKind()
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var id = Guid.NewGuid(); var f = await Folder(s, id: id); var r = await Record(s, id: id); var input = Input(folders: [f.PublicId], records: [r.PublicId]) with { CollectionId = id };
        var before = await Snapshot(s); using var response = await Send(s, input); await CheckCreated(s, input, before, await Success<CreateCollectionOutput>(response, 201));
    }

    [FunctionalTheory][InlineData("PC")][InlineData("CF")][InlineData("CR")]
    public async Task GivenActualLinkMutationOutage_WhenCreating_Then503RollsBackAllStateAndSameIdCanRetry(string kind)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await Setup(owner); var p = await Profile(s, owner, scoped); var f = await Folder(s); var r = await Record(s);
        var input = Input([p.PublicId], [f.PublicId], [r.PublicId]); var before = await Snapshot(s); var table = kind == "PC" ? "profile_collection" : kind == "CF" ? "collection_folder" : "collection_record";
        await using var db = fixture.Context(); var install = "CREATE FUNCTION cerberus.reject_http_collection_fixture() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'controlled collection link outage after mutation' USING ERRCODE='08006'; END $$; CREATE TRIGGER reject_http_collection_fixture AFTER INSERT ON cerberus." + table + " FOR EACH ROW EXECUTE FUNCTION cerberus.reject_http_collection_fixture();";
        await db.Database.ExecuteSqlRawAsync(install);
        try { using var failed = await Send(s, input); await Failure(failed, 503, "persistence_unavailable"); Assert.Equal(before, await Snapshot(s)); }
        finally { var remove = "DROP TRIGGER IF EXISTS reject_http_collection_fixture ON cerberus." + table + "; DROP FUNCTION IF EXISTS cerberus.reject_http_collection_fixture();"; await db.Database.ExecuteSqlRawAsync(remove); }
        using var retry = await Send(s, input); await CheckCreated(s, input, before, await Success<CreateCollectionOutput>(retry, 201));
    }

    [FunctionalFact]
    public async Task GivenUnavailableCollectionDependency_WhenCreating_ThenSafe503AndNoPartialState()
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s); await using var db = fixture.Context(); await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection RENAME TO fixture_unavailable_collection");
        try { using var response = await Send(s, Input()); await Failure(response, 503, "persistence_unavailable"); }
        finally { await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_unavailable_collection RENAME TO collection"); }
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("Master", "complete", 200)][InlineData("PerProfile", "complete", 200)]
    [InlineData("Master", "omit", 400)][InlineData("Master", "extra", 400)][InlineData("Master", "foreign", 400)][InlineData("Master", "stale", 409)][InlineData("Master", "typedAlias", 200)]
    public async Task GivenActualHttpCreatedCollection_WhenNativeRotating_ThenRequireCompleteCurrentTypedInventoryBeforeProofConsumption(string mode, string kind, int status)
    {
        using var owner = new ProtectionFixture(); using var scoped = new ProtectionFixture(); var s = await Setup(owner); var p = await Profile(s, owner, scoped, mode); var f = await Folder(s); var r = await Record(s, f.PublicId);
        var input = Input([p.PublicId], [f.PublicId], [r.PublicId]) with { CollectionId = kind == "typedAlias" ? r.PublicId : Guid.NewGuid() }; using (var create = await Send(s, input)) await Success<CreateCollectionOutput>(create, 201);
        var selected = await Open(s, p.PublicId, scoped);
        await using var db = fixture.Context(); p = await db.Profiles.AsNoTracking().SingleAsync(x => x.Id == p.Id); f = await db.Folders.AsNoTracking().SingleAsync(x => x.Id == f.Id); r = await db.Records.AsNoTracking().SingleAsync(x => x.Id == r.Id);
        var c = await db.Collections.AsNoTracking().SingleAsync(x => x.PublicId == input.CollectionId); var wrappers = JsonSerializer.Deserialize<ProfileKeyWrappers>(p.KeyWrappers, ProtectionFixture.Json)!;
        var replacements = new List<ContentReplacement>
        {
            new("account", s.AccountId, 1, Envelope(2)),
            new("profile", p.PublicId, p.Revision, Envelope(2), wrappers with { MasterKeyWrapper = owner.Wrap(s.AccountId, "profile", p.PublicId, s.Actor, 2, p.Revision + 1), PasswordWrapper = mode == "PerProfile" ? scoped.Rewrap().PasswordWrapper : null }),
            new("folder", f.PublicId, f.Revision, Envelope(2)), new("record", r.PublicId, r.Revision, Envelope(2))
        };
        if (kind != "omit") replacements.Add(new("collection", kind == "foreign" ? Guid.NewGuid() : c.PublicId, kind == "stale" ? c.Revision + 1 : c.Revision, Envelope(2)));
        if (kind == "extra") replacements.Add(new("collection", Guid.NewGuid(), 1, Envelope(2)));
        var change = new ProtectionChange(s.AccountId, 1, 1, "rotate-content", owner.Rewrap(), replacements.ToArray()); var raw = Bytes(change); var challenge = await Challenge(s, raw); var before = await Snapshot(s);
        using var request = Request(s, raw, "/api/vault/protection"); request.Method = HttpMethod.Put; request.Headers.Add("X-Cerberus-Challenge-Id", challenge.ChallengeId.ToString()); request.Headers.Add("X-Cerberus-Proof", owner.Sign(challenge)); using var response = await Gateway.Client.SendAsync(request);
        if (status != 200) { await Failure(response, status); Assert.Equal(before, await Snapshot(s)); }
        else
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); var current = await db.Collections.AsNoTracking().SingleAsync(x => x.Id == c.Id); Assert.Equal(c.Revision + 1, current.Revision); Assert.Equal(2, current.KeyEpoch); Assert.Equal(Bytes(replacements.Single(x => x.ResourceKind == "collection").Envelope), current.Envelope); Assert.Equal(c.EditedAt, current.EditedAt);
            var previous = JsonNode.Parse(before)!.AsObject(); var after = JsonNode.Parse(await Snapshot(s))!.AsObject(); foreach (var table in new[] { "ProfileCollections", "CollectionFolders", "CollectionRecords", "ProfileFolders", "ProfileRecords" }) Assert.Equal(previous[table]!.ToJsonString(), after[table]!.ToJsonString());
            using var stale = await Send(s with { Access = selected }, Input([p.PublicId])); await Failure(stale, 403, "vault_access_denied");
        }
        Assert.Equal(status == 200, (await db.VaultUnlockChallenges.AsNoTracking().SingleAsync(x => x.PublicId == challenge.ChallengeId)).Consumed);
    }

    private sealed record State(Guid Actor, Guid AccountId, long InternalId, string Access);
    private async Task<State> Setup(ProtectionFixture owner)
    {
        var actor = Guid.NewGuid(); fixture.Users[actor + "@example.test"] = new(actor, "fixture-password"); await using var db = fixture.Context(); var a = new Account { PublicId = Guid.NewGuid(), HeimdallPublicId = actor, DetailsEnvelope = Bytes(Envelope()) }; db.Accounts.Add(a); await db.SaveChangesAsync();
        var access = ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)); OpaqueAccessHandle.TryHash(access, out var verifier); db.VaultProtections.Add(new() { AccountId = a.Id, Material = Bytes(owner.Material) }); db.VaultAccessSessions.Add(new() { AccountId = a.Id, HandleVerifier = verifier, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 }); await db.SaveChangesAsync(); return new(actor, a.PublicId, a.Id, access);
    }
    private async Task<Profile> Profile(State s, ProtectionFixture owner, ProtectionFixture scoped, string mode = "Master")
    {
        var id = Guid.NewGuid(); var input = new ProfileCreateInput(id, Envelope(), new(mode, scoped.Material.UnlockVerifier, owner.Wrap(s.AccountId, "profile", id, s.Actor), mode == "PerProfile" ? scoped.Material.PasswordWrapper : null), DateTimeOffset.UnixEpoch, [], [], []);
        using var request = Request(s, Bytes(input), "/api/profiles"); using var response = await Gateway.Client.SendAsync(request); await Success<CreateProfileOutput>(response, 201); await using var db = fixture.Context(); return await db.Profiles.AsNoTracking().SingleAsync(x => x.PublicId == id);
    }
    private async Task<VaultFolder> Folder(State s, Guid? parent = null, Guid[]? profiles = null, Guid? id = null)
    {
        var input = new FolderCreateInput(id ?? Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch, profiles ?? [], parent); using var request = Request(s, Bytes(input), "/api/folders"); using var response = await Gateway.Client.SendAsync(request); await Success<CreateFolderOutput>(response, 201); await using var db = fixture.Context(); return await db.Folders.AsNoTracking().SingleAsync(x => x.PublicId == input.FolderId);
    }
    private async Task<VaultRecord> Record(State s, Guid? folder = null, Guid[]? profiles = null, Guid? id = null)
    {
        var input = new RecordCreateInput(id ?? Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch, profiles ?? [], folder); using var request = Request(s, Bytes(input), "/api/records"); using var response = await Gateway.Client.SendAsync(request); await Success<CreateRecordOutput>(response, 201); await using var db = fixture.Context(); return await db.Records.AsNoTracking().SingleAsync(x => x.PublicId == input.RecordId);
    }
    private async Task<string> Open(State s, Guid id, ProtectionFixture scoped)
    {
        await using var db = fixture.Context(); var revision = await db.Profiles.Where(x => x.PublicId == id).Select(x => x.Revision).SingleAsync(); var raw = Bytes(new { expectedRevision = revision });
        using var challengeRequest = Request(s, Bytes(new { expectedRevision = revision, requestHash = ProtocolBinary.Encode(SHA256.HashData(raw)) }), "/api/profiles/" + id + "/access/challenges"); challengeRequest.Headers.Remove("X-Cerberus-Vault-Access"); using var issued = await Gateway.Client.SendAsync(challengeRequest); var challenge = (await Success<IssueProfileAccessChallengeOutput>(issued, 201)).Challenge;
        using var request = Request(s, raw, "/api/profiles/" + id + "/access"); request.Headers.Remove("X-Cerberus-Vault-Access"); request.Headers.Add("X-Cerberus-Challenge-Id", challenge.ChallengeId.ToString()); request.Headers.Add("X-Cerberus-Proof", scoped.Sign(challenge)); using var response = await Gateway.Client.SendAsync(request); return (await Success<OpenProfileAccessOutput>(response, 200)).VaultAccess;
    }
    private async Task<VaultProofChallenge> Challenge(State s, byte[] raw)
    {
        using var request = Request(s, Bytes(new { operation = "change-protection", requestHash = ProtocolBinary.Encode(SHA256.HashData(raw)) }), "/api/vault/challenges"); request.Headers.Remove("X-Cerberus-Vault-Access"); using var response = await Gateway.Client.SendAsync(request); Assert.Equal(HttpStatusCode.Created, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync()); return document.RootElement.GetProperty("data").GetProperty("challenge").Deserialize<VaultProofChallenge>(ProtectionFixture.Json)!;
    }
    private async Task CheckCreated(State s, CollectionCreateInput input, string before, CreateCollectionOutput output)
    {
        Assert.Equal(input.CollectionId, output.CollectionId); Assert.Equal(1, output.Revision); Assert.InRange(output.ServerSequence, 1, ProtocolBinary.MaxInteger); Assert.Equal(input.EditedAt.Ticks - input.EditedAt.Ticks % 10, output.EditedAt.Ticks); Assert.Equal(TimeSpan.Zero, output.EditedAt.Offset);
        await using var db = fixture.Context(); var row = await db.Collections.AsNoTracking().SingleAsync(x => x.PublicId == input.CollectionId); Assert.Equal(s.InternalId, row.AccountId); Assert.Equal(Bytes(input.Envelope), row.Envelope); Assert.Equal(1, row.KeyEpoch); Assert.Null(row.DeletedAt); Assert.Null(row.PurgeAt); Assert.Equal(output.ServerSequence, row.ServerSequence); Assert.Equal(output.EditedAt, row.EditedAt);
        Assert.Equal(input.ProfileIds.Order(), await (from pc in db.ProfileCollections join p in db.Profiles on pc.ProfileId equals p.Id where pc.CollectionId == row.Id orderby p.PublicId select p.PublicId).ToArrayAsync());
        Assert.Equal(input.FolderIds.Order(), await (from cf in db.CollectionFolders join f in db.Folders on cf.FolderId equals f.Id where cf.CollectionId == row.Id && cf.AccountId == s.InternalId orderby f.PublicId select f.PublicId).ToArrayAsync());
        Assert.Equal(input.RecordIds.Order(), await (from cr in db.CollectionRecords join r in db.Records on cr.RecordId equals r.Id where cr.CollectionId == row.Id && cr.AccountId == s.InternalId orderby r.PublicId select r.PublicId).ToArrayAsync());
        var old = JsonNode.Parse(before)!.AsObject(); var after = JsonNode.Parse(await Snapshot(s))!.AsObject();
        foreach (var table in new[] { "Collections", "ProfileCollections", "CollectionFolders", "CollectionRecords" }) { var array = after[table]!.AsArray(); foreach (var item in array.ToArray()) if (item![table == "Collections" ? "Id" : "CollectionId"]!.GetValue<long>() == row.Id) array.Remove(item); }
        foreach (var (table, ids) in new[] { ("Profiles", input.ProfileIds), ("Folders", input.FolderIds), ("Records", input.RecordIds) }) foreach (var item in after[table]!.AsArray())
        {
            if (!ids.Contains(item!["PublicId"]!.GetValue<Guid>())) continue; var previous = old[table]!.AsArray().Single(x => x!["Id"]!.GetValue<long>() == item["Id"]!.GetValue<long>())!;
            Assert.Equal(previous["Revision"]!.GetValue<long>() + 1, item["Revision"]!.GetValue<long>()); Assert.True(item["ServerSequence"]!.GetValue<long>() > previous["ServerSequence"]!.GetValue<long>()); Assert.InRange(item["ServerSequence"]!.GetValue<long>(), 1, ProtocolBinary.MaxInteger); Assert.NotEqual(previous["ConcurrencyStamp"]!.ToJsonString(), item["ConcurrencyStamp"]!.ToJsonString());
            foreach (var key in new[] { "Revision", "ServerSequence", "ConcurrencyStamp" }) item[key] = previous[key]!.DeepClone();
        }
        Assert.Equal(old.ToJsonString(), after.ToJsonString());
    }
    private async Task<string> Snapshot(State s)
    {
        await using var db = fixture.Context(); return JsonSerializer.Serialize(new
        {
            Accounts = await db.Accounts.AsNoTracking().Where(x => x.Id == s.InternalId).ToArrayAsync(), Protection = await db.VaultProtections.AsNoTracking().Where(x => x.AccountId == s.InternalId).ToArrayAsync(), Profiles = await db.Profiles.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(), Folders = await db.Folders.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(), Records = await db.Records.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(), Collections = await db.Collections.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(),
            ProfileFolders = await db.ProfileFolders.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.ProfileId).ThenBy(x => x.FolderId).ToArrayAsync(), ProfileRecords = await db.ProfileRecords.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.ProfileId).ThenBy(x => x.RecordId).ToArrayAsync(), ProfileCollections = await db.ProfileCollections.AsNoTracking().Where(x => db.Profiles.Any(p => p.AccountId == s.InternalId && p.Id == x.ProfileId)).OrderBy(x => x.ProfileId).ThenBy(x => x.CollectionId).ToArrayAsync(), CollectionFolders = await db.CollectionFolders.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.CollectionId).ThenBy(x => x.FolderId).ToArrayAsync(), CollectionRecords = await db.CollectionRecords.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.CollectionId).ThenBy(x => x.RecordId).ToArrayAsync(),
            Grants = await db.CollectionGrants.AsNoTracking().Where(x => x.RecipientAccountId == s.InternalId || db.Collections.Any(c => c.Id == x.CollectionId && c.AccountId == s.InternalId)).OrderBy(x => x.Id).ToArrayAsync(), Sessions = await db.VaultAccessSessions.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(), Operations = await db.TrashOperations.AsNoTracking().Where(x => x.AccountId == s.InternalId).OrderBy(x => x.Id).ToArrayAsync(), Entries = await db.TrashEntries.AsNoTracking().Where(x => db.TrashOperations.Any(o => o.AccountId == s.InternalId && o.Id == x.OperationId)).OrderBy(x => x.Id).ToArrayAsync(), Queue = await db.RetentionWorkItems.AsNoTracking().Where(x => db.TrashOperations.Any(o => o.AccountId == s.InternalId && x.OperationKey == "trash/" + o.PublicId)).OrderBy(x => x.Id).ToArrayAsync()
        });
    }
    private static CollectionCreateInput Input(Guid[]? profiles = null, Guid[]? folders = null, Guid[]? records = null) => new(Guid.NewGuid(), Envelope(), DateTimeOffset.UnixEpoch.AddTicks(19), profiles ?? [], records ?? [], folders ?? []);
    private static EncryptedEnvelope Envelope(long epoch = 1) => new("cerberus-content-v1", epoch, ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(12)), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(73)), ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(16)));
    private static byte[] Bytes<T>(T value) => JsonSerializer.SerializeToUtf8Bytes(value, ProtectionFixture.Json);
    private static HttpRequestMessage Request(State s, byte[] body, string path = "/api/collections")
    { var request = new HttpRequestMessage(HttpMethod.Post, path) { Content = new ByteArrayContent(body) }; request.Content.Headers.ContentType = new("application/json"); request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor)); request.Headers.Add("X-Cerberus-Vault-Access", s.Access); return request; }
    private async Task<HttpResponseMessage> Send(State s, CollectionCreateInput input) { using var request = Request(s, Bytes(input)); return await Gateway.Client.SendAsync(request); }
    private async Task<HttpResponseMessage> Read(State s, string path) { using var request = new HttpRequestMessage(HttpMethod.Get, path); request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor)); request.Headers.Add("X-Cerberus-Vault-Access", s.Access); return await Gateway.Client.SendAsync(request); }
    private static async Task<T> Success<T>(HttpResponseMessage response, int status) where T : class
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); var text = await response.Content.ReadAsStringAsync(); using var document = JsonDocument.Parse(text);
        Assert.True(document.RootElement.GetProperty("success").GetBoolean());
        if (typeof(T) == typeof(CreateCollectionOutput)) { Assert.Contains("collection_created", document.RootElement.GetProperty("messages").EnumerateArray().Select(x => x.GetString())); Assert.Equal(new[] { "collectionId", "editedAt", "revision", "serverSequence" }, document.RootElement.GetProperty("data").EnumerateObject().Select(x => x.Name).Order()); }
        return Assert.IsType<T>(JsonSerializer.Deserialize<DataOutput<T>>(text, ProtectionFixture.Json)!.Data);
    }
    private static async Task Failure(HttpResponseMessage response, int status, string? error = null)
    {
        Assert.Equal((HttpStatusCode)status, response.StatusCode); Assert.True(response.Headers.CacheControl?.NoStore); var text = await response.Content.ReadAsStringAsync(); if (status == 413) { Assert.Equal("", text); return; }
        using var document = JsonDocument.Parse(text); Assert.False(document.RootElement.GetProperty("success").GetBoolean()); if (document.RootElement.TryGetProperty("data", out var data)) Assert.Equal(JsonValueKind.Null, data.ValueKind); if (error is not null) Assert.Contains(error, document.RootElement.GetProperty("errors").EnumerateArray().Select(x => x.GetString()));
    }
}
