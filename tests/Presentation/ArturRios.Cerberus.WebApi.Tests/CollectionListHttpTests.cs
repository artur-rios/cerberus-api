using System.Net;
using System.Net.Http.Json;
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
public class CollectionListHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenHttpCreatedOwnedCollections_WhenPaging_ThenReturnExactOpaqueShapeAndStableHighwaterWithoutMutation()
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var foreign = await Setup(owner);
        await Create(foreign); var hidden = await Create(s); var terminal = await Create(s); var first = await Create(s); var second = await Create(s);
        await using (var db = fixture.Context()) {
            await db.Collections.Where(x => x.PublicId == hidden.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(r => r.DeletedAt, DateTimeOffset.UtcNow));
            db.TerminalErasures.Add(new() { ResourceId = terminal.CollectionId, ResourceKind = "collection", DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
        }
        var before = await Snapshot(s); using var response = await Read(s, "/api/collections?pageSize=1"); var page = await Success<CollectionListOutput>(response, 200);
        Fields(page, "items", "nextCursor"); var item = Assert.Single(page.Items); Fields(item, "collectionId", "revision", "serverSequence", "editedAt", "envelope");
        Assert.Equal(first.CollectionId, item.CollectionId); Assert.Equal(first.Envelope, item.Envelope); Assert.Equal(Normalize(first.EditedAt), item.EditedAt);
        Assert.NotNull(page.NextCursor); Assert.Equal(before, await Snapshot(s)); var later = await Create(s);
        using var next = await Read(s, "/api/collections?pageSize=1&cursor=" + page.NextCursor); var end = await Success<CollectionListOutput>(next, 200);
        Assert.Equal(second.CollectionId, Assert.Single(end.Items).CollectionId); Assert.Null(end.NextCursor); Assert.DoesNotContain(end.Items, x => x.CollectionId == later.CollectionId);
    }

    [FunctionalTheory]
    [InlineData("?pageSize=0")][InlineData("?pageSize=-1")][InlineData("?pageSize=101")][InlineData("?pageSize=2147483648")]
    [InlineData("?pageSize=1.0")][InlineData("?pageSize=+1")][InlineData("?pageSize=%201")][InlineData("?pageSize=01")][InlineData("?pageSize=")]
    [InlineData("?pageSize=1&pageSize=2")][InlineData("?PageSize=1")][InlineData("?profileId=00000000-0000-0000-0000-000000000001")]
    [InlineData("?cursor=")][InlineData("?cursor=bad")][InlineData("?cursor=a&cursor=b")][InlineData("?search=secret")]
    public async Task GivenMalformedOrUnknownQuery_WhenListing_Then400WithoutMutation(string query)
    { using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s); using var r = await Read(s, "/api/collections" + query); await Failure(r, 400, "validation_failed"); Assert.Equal(before, await Snapshot(s)); }

    [FunctionalTheory][InlineData("missing", 401)][InlineData("bad", 400)][InlineData("duplicate", 400)]
    [InlineData("body", 400)][InlineData("oversized", 413)][InlineData("transfer", 400)]
    public async Task GivenMalformedAccessOrGetBody_WhenListing_ThenRejectWithoutMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/collections"); request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor));
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
        var context = await host.Server.SendAsync(c => { c.Request.Method = "GET"; c.Request.Path = "/api/collections";
            c.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(s.Actor); c.Request.Headers["X-Cerberus-Vault-Access"] = kind == "empty" ? "" : s.Access;
            if (kind == "http2") c.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature()); });
        Assert.Equal(400, context.Response.StatusCode); Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString()); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("missing", 401)][InlineData("wrongScope", 401)][InlineData("deleted", 401)][InlineData("unavailable", 503)]
    public async Task GivenInvalidCurrentIdentity_WhenListing_ThenRejectWithoutPersistence(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s);
        if (kind is "deleted" or "unavailable") fixture.Users[s.Actor + "@example.test"] = new(s.Actor, "fixture-password", Deleted: kind == "deleted", IdentityUnavailable: kind == "unavailable");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/collections"); request.Headers.Add("X-Cerberus-Vault-Access", s.Access);
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
        using var response = await Read(request, "/api/collections"); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("actor")][InlineData("access")][InlineData("size")][InlineData("tamper")]
    public async Task GivenCursorSubstitution_WhenContinuing_Then400(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); await Create(s); await Create(s);
        using var initial = await Read(s, "/api/collections?pageSize=1"); var cursor = (await Success<CollectionListOutput>(initial, 200)).NextCursor!;
        if (kind == "actor") s = await Setup(owner);
        if (kind == "access") { var access = ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)); OpaqueAccessHandle.TryHash(access, out var verifier);
            await using var db = fixture.Context(); db.VaultAccessSessions.Add(new() { AccountId = s.InternalId, HandleVerifier = verifier, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 }); await db.SaveChangesAsync(); s = s with { Access = access }; }
        if (kind == "tamper") { Assert.True(ProtocolBinary.TryDecode(cursor, null, out var bytes)); bytes[^1] ^= 1; cursor = ProtocolBinary.Encode(bytes); }
        var before = await Snapshot(s); using var response = await Read(s, "/api/collections?pageSize=" + (kind == "size" ? 2 : 1) + "&cursor=" + cursor);
        await Failure(response, 400, "validation_failed"); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("envelope")][InlineData("revision")][InlineData("zero")][InlineData("duplicate")]
    [InlineData("time")][InlineData("epoch")][InlineData("unavailable")]
    public async Task GivenCorruptVisibleContentOrDependency_WhenListing_Then503WithoutPartialPayload(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var first = await Create(s); var second = await Create(s); await using var db = fixture.Context();
        if (kind == "envelope") await db.Collections.Where(x => x.PublicId == second.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Envelope, new byte[] { 1 }));
        if (kind == "revision") await db.Collections.Where(x => x.PublicId == second.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Revision, 0));
        if (kind is "zero" or "duplicate") { var sequence = (await db.Collections.SingleAsync(x => x.PublicId == first.CollectionId)).ServerSequence;
            await db.Collections.Where(x => x.PublicId == second.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(r => r.ServerSequence, kind == "zero" ? 0 : sequence)); }
        if (kind == "epoch") await db.Collections.Where(x => x.PublicId == second.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(r => r.KeyEpoch, 2));
        if (kind == "time") await db.Collections.Where(x => x.PublicId == second.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(r => r.EditedAt, DateTimeOffset.MinValue));
        if (kind == "unavailable") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection_grant RENAME TO http_fixture_unavailable_collection_grant");
        var before = kind == "unavailable" ? null : await Snapshot(s);
        try { using var response = await Read(s, kind == "duplicate" ? "/api/collections?pageSize=1" : "/api/collections"); await Failure(response, 503, "persistence_unavailable"); }
        finally { if (kind == "unavailable") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.http_fixture_unavailable_collection_grant RENAME TO collection_grant"); }
        if (before is not null) Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalFact]
    public async Task GivenEmptyOwnedInventory_WhenListing_Then200EmptyPageWithoutCursor()
    { using var owner = new ProtectionFixture(); var s = await Setup(owner); using var r = await Read(s, "/api/collections"); var page = await Success<CollectionListOutput>(r, 200); Assert.Empty(page.Items); Assert.Null(page.NextCursor); }
    [FunctionalTheory][InlineData("trash")][InlineData("terminal")]
    public async Task GivenSelectedProfileBecomesHidden_WhenListing_Then403WithoutMutation(string kind)
    {using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner);var p=await Profile(s,owner,scoped);await Create(s,[p.PublicId]);var selected=s with{Access=await Open(s,p,scoped)};await using(var db=fixture.Context()){if(kind=="trash")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));else{db.TerminalErasures.Add(new(){ResourceId=p.PublicId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}var before=await Snapshot(s);using var response=await Read(selected,"/api/collections");await Failure(response,403,"vault_access_denied");Assert.Equal(before,await Snapshot(s));}

    [FunctionalTheory][InlineData("record")][InlineData("profile")][InlineData("collection")][InlineData("grant")][InlineData("account")][InlineData("folder")]
    public async Task GivenSameUuidTerminalKind_WhenListing_ThenOnlyCollectionMarkerOmitsCollection(string kind)
    {using var owner=new ProtectionFixture();var s=await Setup(owner);var input=await Create(s);await using(var db=fixture.Context()){db.TerminalErasures.Add(new(){ResourceId=input.CollectionId,ResourceKind=kind,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}var before=await Snapshot(s);using var response=await Read(s,"/api/collections");var page=await Success<CollectionListOutput>(response,200);Assert.Equal(kind=="collection"?0:1,page.Items.Count);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory]
    [InlineData(CollectionGrantAccess.ReadOnly, false)][InlineData(CollectionGrantAccess.ReadOnly, true)]
    [InlineData(CollectionGrantAccess.ReadWrite, false)][InlineData(CollectionGrantAccess.ReadWrite, true)]
    public async Task GivenNativeGrantsAndOwnedCollections_WhenListing_ThenApplyExactSelectedProfileCollectionLinks(CollectionGrantAccess access, bool selected)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var scoped = new ProtectionFixture(); using var otherKeys = new ProtectionFixture();
        var foreign = await Setup(owner); var s = await Setup(recipient); var shared = await Create(foreign); var outside = await Create(foreign);
        await Grant(foreign, owner, s, recipient, shared.CollectionId, access); await Grant(foreign, owner, s, recipient, outside.CollectionId, access);
        var p = await Profile(s, recipient, scoped, collections: [shared.CollectionId]); var other = await Profile(s, recipient, otherKeys);
        var own = await Create(s, [p.PublicId]); var otherOwn = await Create(s, [other.PublicId]);
        var request = selected ? s with { Access = await Open(s, p, scoped) } : s; var before = await Snapshot(s); var ownerBefore = await Snapshot(foreign);
        using var response = await Read(request, "/api/collections"); var page = await Success<CollectionListOutput>(response, 200);
        Assert.Equal((selected ? new[] { own.CollectionId, shared.CollectionId } : new[] { own.CollectionId, otherOwn.CollectionId, shared.CollectionId, outside.CollectionId }).Order(), page.Items.Select(x => x.CollectionId).Order());
        Assert.Equal(shared.Envelope, page.Items.Single(x => x.CollectionId == shared.CollectionId).Envelope);
        Assert.Equal(before, await Snapshot(s)); Assert.Equal(ownerBefore, await Snapshot(foreign));
    }

    [FunctionalTheory][InlineData("grant")][InlineData("profileCollection")][InlineData("session")]
    public async Task GivenPermissionRevocationBetweenPages_WhenContinuing_ThenReapplyCurrentCollectionVisibility(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var scoped = new ProtectionFixture();
        var foreign = await Setup(owner); var s = await Setup(recipient); var first = await Create(foreign); var second = await Create(foreign);
        await Grant(foreign, owner, s, recipient, first.CollectionId); var grant = await Grant(foreign, owner, s, recipient, second.CollectionId);
        var p = await Profile(s, recipient, scoped, collections: [first.CollectionId, second.CollectionId]); var selected = s with { Access = await Open(s, p, scoped) };
        using var initial = await Read(selected, "/api/collections?pageSize=1"); var page = await Success<CollectionListOutput>(initial, 200); Assert.NotNull(page.NextCursor);
        await using (var db = fixture.Context()) {
            if (kind == "grant") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked));
            if (kind == "profileCollection") await db.ProfileCollections.Where(x => x.ProfileId == p.Id && x.CollectionId == grant.CollectionId).ExecuteDeleteAsync();
            if (kind == "session") { OpaqueAccessHandle.TryHash(selected.Access, out var verifier); await db.VaultAccessSessions.Where(x => x.HandleVerifier == verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true)); }
        }
        var before = await Snapshot(s); using var response = await Read(selected, "/api/collections?pageSize=1&cursor=" + page.NextCursor);
        if (kind == "session") await Failure(response, 403, "vault_access_denied");
        else { var end = await Success<CollectionListOutput>(response, 200); Assert.Empty(end.Items); Assert.Null(end.NextCursor); }
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("wrapper")][InlineData("pins")][InlineData("epoch")]
    public async Task GivenCorruptVisibleNativeEvidenceBeyondPage_WhenListing_Then503WithoutPartialOwnedPayload(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); var foreign = await Setup(owner); var s = await Setup(recipient);
        await Create(s); var shared = await Create(foreign); var grant = await Grant(foreign, owner, s, recipient, shared.CollectionId);
        await using (var db = fixture.Context()) {
            if (kind == "wrapper") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.RecipientKeyEnvelope, new byte[] { 1 }));
            if (kind == "pins") await db.VaultProtections.Where(x => x.AccountId == foreign.InternalId).ExecuteUpdateAsync(x => x.SetProperty(v => v.Material, new byte[] { 1 }));
            if (kind == "epoch") await db.Collections.Where(x => x.PublicId == shared.CollectionId).ExecuteUpdateAsync(x => x.SetProperty(c => c.KeyEpoch, 2));
        }
        var before = await Snapshot(s); var ownerBefore = await Snapshot(foreign); using var response = await Read(s, "/api/collections?pageSize=1"); await Failure(response, 503, "persistence_unavailable");
        Assert.Equal(before, await Snapshot(s)); Assert.Equal(ownerBefore, await Snapshot(foreign));
    }

    [FunctionalFact]
    public async Task GivenUnselectedCorruptGrantAndDirectFolder_WhenListing_ThenAdmitOnlyExactOwnedCollectionSelection()
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var scoped = new ProtectionFixture();
        var foreign = await Setup(owner); var s = await Setup(recipient); var foreignCollection = await Create(foreign); var grant = await Grant(foreign, owner, s, recipient, foreignCollection.CollectionId);
        var folder = await Folder(s); var containing = await Create(s);
        await using (var db = fixture.Context()) {
            var collection = await db.Collections.SingleAsync(x => x.PublicId == containing.CollectionId);
            db.CollectionFolders.Add(new() { AccountId = s.InternalId, CollectionId = collection.Id, FolderId = folder.Id });
            await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.RecipientKeyEnvelope, new byte[] { 1 })); await db.SaveChangesAsync();
        }
        var p = await Profile(s, recipient, scoped, folder); var visible = await Create(s, [p.PublicId]); var selected = s with { Access = await Open(s, p, scoped) }; var before = await Snapshot(s);
        using var response = await Read(selected, "/api/collections"); var page = await Success<CollectionListOutput>(response, 200);
        Assert.Equal(visible.CollectionId, Assert.Single(page.Items).CollectionId); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("folder")][InlineData("record")]
    public async Task GivenAuthenticatedCursorForAnotherResource_WhenListing_Then400(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); OpaqueAccessHandle.TryHash(s.Access, out var verifier);
        using var host = new WebApplicationFactory<Program>();
        var options = host.Services.GetRequiredService<ArturRios.Cerberus.Shared.Configuration.CerberusOptions>();
        var cursor = kind == "folder" ? new FolderListCursor(options).Encode(new(s.Actor, verifier, 1, 1, 3)) : new RecordListCursor(options).Encode(new(s.Actor, verifier, 1, 1, 3));
        using var response = await Read(s, "/api/collections?pageSize=1&cursor=" + cursor); await Failure(response, 400, "validation_failed");
    }

    private async Task<CollectionGrant> Grant(State owner, ProtectionFixture author, State recipient, ProtectionFixture recipientKeys, Guid collectionId, CollectionGrantAccess access = CollectionGrantAccess.ReadOnly)
    {
        await using var db = fixture.Context(); var collection = await db.Collections.SingleAsync(x => x.PublicId == collectionId);
        var grant = new CollectionGrant { PublicId = Guid.NewGuid(), CollectionId = collection.Id, RecipientAccountId = recipient.InternalId, Access = access };
        grant.RecipientKeyEnvelope = Bytes(author.WrapGrant(owner.AccountId, collectionId, grant.PublicId, recipient.Actor, recipientKeys.Material.RecipientKey));
        db.CollectionGrants.Add(grant); await db.SaveChangesAsync(); return grant;
    }

    private async Task<CollectionCreateInput> Create(State s,Guid[]? profiles=null){var input=Input(profiles);using var response=await Send(s,input);await Success<CreateCollectionOutput>(response,201);return input;}
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
        if (typeof(T) == typeof(CollectionListOutput)) {
            var data = document.RootElement.GetProperty("data");
            Assert.Equal(new[] { "items", "nextCursor" }, data.EnumerateObject().Select(x => x.Name).Order());
            foreach (var item in data.GetProperty("items").EnumerateArray())
                Assert.Equal(new[] { "collectionId", "editedAt", "envelope", "revision", "serverSequence" }, item.EnumerateObject().Select(x => x.Name).Order());
        }
        return Assert.IsType<T>(JsonSerializer.Deserialize<DataOutput<T>>(text, ProtectionFixture.Json)!.Data);
    }
    private static void Fields(object value,params string[] names){using var d=JsonDocument.Parse(JsonSerializer.Serialize(value,ProtectionFixture.Json));Assert.Equal(names.Order(),d.RootElement.EnumerateObject().Select(x=>x.Name).Order());}
    private static async Task Failure(HttpResponseMessage r,int status,string? error=null){Assert.Equal((HttpStatusCode)status,r.StatusCode);Assert.True(r.Headers.CacheControl?.NoStore);if(status==413){Assert.Equal("",await r.Content.ReadAsStringAsync());return;}using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.False(d.RootElement.GetProperty("success").GetBoolean());if(d.RootElement.TryGetProperty("data",out var data))Assert.Equal(JsonValueKind.Null,data.ValueKind);if(error is not null)Assert.Contains(error,d.RootElement.GetProperty("errors").EnumerateArray().Select(x=>x.GetString()));}
    private async Task<string> Snapshot(State s,bool skipMembers=false){await using var db=fixture.Context();return JsonSerializer.Serialize(new{Protected=await Protected(s),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>x.RecipientAccountId==s.InternalId || db.Collections.Any(c=>c.Id==x.CollectionId && c.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync(),Members=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),CollectionFolders=skipMembers?[]:await db.CollectionFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileCollections=await db.ProfileCollections.AsNoTracking().Where(x=>db.Profiles.Any(p=>p.Id==x.ProfileId && p.AccountId==s.InternalId)).OrderBy(x=>x.ProfileId).ThenBy(x=>x.CollectionId).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),ProfileFolders=await db.ProfileFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileRecords=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync()});}
}
