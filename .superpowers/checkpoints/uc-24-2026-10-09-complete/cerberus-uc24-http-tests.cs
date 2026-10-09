using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Command.Profiles;
using ArturRios.Cerberus.Command.Records;
using ArturRios.Cerberus.Command.Folders;
using ArturRios.Cerberus.Domain.Access;
using ArturRios.Cerberus.Domain.Accounts;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.Domain.Protection;
using ArturRios.Cerberus.Domain.Records;
using ArturRios.Cerberus.Domain.Folders;
using ArturRios.Cerberus.Domain.Resources;
using ArturRios.Cerberus.Query.Records;
using ArturRios.Cerberus.Query.Folders;
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
public class FolderListHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenHttpCreatedOwnedFolders_WhenPaging_ThenReturnExactOpaqueShapeAndStableHighwaterWithoutMutation()
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var foreign = await Setup(owner);
        await Create(foreign); var hidden = await Create(s); var terminal = await Create(s); var first = await Create(s); var second = await Create(s);
        await using (var db = fixture.Context()) {
            await db.Folders.Where(x => x.PublicId == hidden.FolderId).ExecuteUpdateAsync(x => x.SetProperty(r => r.DeletedAt, DateTimeOffset.UtcNow));
            db.TerminalErasures.Add(new() { ResourceId = terminal.FolderId, ResourceKind = "folder", DeletedAt = DateTimeOffset.UtcNow }); await db.SaveChangesAsync();
        }
        var before = await Snapshot(s); using var response = await Read(s, "/api/folders?pageSize=1"); var page = await Success<FolderListOutput>(response, 200);
        Fields(page, "items", "nextCursor"); var item = Assert.Single(page.Items); Fields(item, "folderId", "revision", "serverSequence", "editedAt", "envelope");
        Assert.Equal(first.FolderId, item.FolderId); Assert.Equal(first.Envelope, item.Envelope); Assert.Equal(Normalize(first.EditedAt), item.EditedAt);
        Assert.NotNull(page.NextCursor); Assert.Equal(before, await Snapshot(s)); var later = await Create(s);
        using var next = await Read(s, "/api/folders?pageSize=1&cursor=" + page.NextCursor); var end = await Success<FolderListOutput>(next, 200);
        Assert.Equal(second.FolderId, Assert.Single(end.Items).FolderId); Assert.Null(end.NextCursor); Assert.DoesNotContain(end.Items, x => x.FolderId == later.FolderId);
    }

    [FunctionalTheory][InlineData("grant")][InlineData("member")][InlineData("profileCollection")][InlineData("session")]
    public async Task GivenPermissionRevocationBetweenPages_WhenContinuing_ThenApplyCurrentVisibility(string kind)
    {
        using var owner = new ProtectionFixture(); using var recipient = new ProtectionFixture(); using var scoped = new ProtectionFixture();
        var foreign = await Setup(owner); var s = await Setup(recipient); var first = await Create(foreign); var second = await Create(foreign);
        var (collection, grant) = await Share(foreign, owner, s, recipient, CollectionGrantAccess.ReadOnly, [first.FolderId, second.FolderId], []);
        var p = await Profile(s, recipient, scoped, collections: [collection.PublicId]); var selected = s with { Access = await Open(s, p, scoped) };
        using var initial = await Read(selected, "/api/folders?pageSize=1"); var page = await Success<FolderListOutput>(initial, 200); Assert.NotNull(page.NextCursor);
        await using (var db = fixture.Context()) {
            if (kind == "grant") await db.CollectionGrants.Where(x => x.Id == grant.Id).ExecuteUpdateAsync(x => x.SetProperty(g => g.State, CollectionGrantState.Revoked));
            if (kind == "member") await db.CollectionFolders.Where(x => x.CollectionId == collection.Id).ExecuteDeleteAsync();
            if (kind == "profileCollection") await db.ProfileCollections.Where(x => x.ProfileId == p.Id).ExecuteDeleteAsync();
            if (kind == "session") { OpaqueAccessHandle.TryHash(selected.Access, out var verifier); await db.VaultAccessSessions.Where(x => x.HandleVerifier == verifier).ExecuteUpdateAsync(x => x.SetProperty(v => v.Revoked, true)); }
        }
        var before = await Snapshot(s); using var next = await Read(selected, "/api/folders?pageSize=1&cursor=" + page.NextCursor);
        if (kind == "session") await Failure(next, 403, "vault_access_denied"); else { var end = await Success<FolderListOutput>(next, 200); Assert.Empty(end.Items); Assert.Null(end.NextCursor); }
        Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory]
    [InlineData("?pageSize=0")][InlineData("?pageSize=-1")][InlineData("?pageSize=101")][InlineData("?pageSize=2147483648")]
    [InlineData("?pageSize=1.0")][InlineData("?pageSize=+1")][InlineData("?pageSize=%201")][InlineData("?pageSize=01")][InlineData("?pageSize=")]
    [InlineData("?pageSize=1&pageSize=2")][InlineData("?PageSize=1")][InlineData("?profileId=00000000-0000-0000-0000-000000000001")]
    [InlineData("?cursor=")][InlineData("?cursor=bad")][InlineData("?cursor=a&cursor=b")][InlineData("?search=secret")]
    public async Task GivenMalformedOrUnknownQuery_WhenListing_Then400WithoutMutation(string query)
    { using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s); using var r = await Read(s, "/api/folders" + query); await Failure(r, 400, "validation_failed"); Assert.Equal(before, await Snapshot(s)); }

    [FunctionalTheory][InlineData("missing", 401)][InlineData("bad", 400)][InlineData("duplicate", 400)]
    [InlineData("body", 400)][InlineData("oversized", 413)][InlineData("transfer", 400)]
    public async Task GivenMalformedAccessOrGetBody_WhenListing_ThenRejectWithoutMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/folders"); request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor));
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
        var context = await host.Server.SendAsync(c => { c.Request.Method = "GET"; c.Request.Path = "/api/folders";
            c.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(s.Actor); c.Request.Headers["X-Cerberus-Vault-Access"] = kind == "empty" ? "" : s.Access;
            if (kind == "http2") c.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature()); });
        Assert.Equal(400, context.Response.StatusCode); Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString()); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("missing", 401)][InlineData("wrongScope", 401)][InlineData("deleted", 401)][InlineData("unavailable", 503)]
    public async Task GivenInvalidCurrentIdentity_WhenListing_ThenRejectWithoutPersistence(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s);
        if (kind is "deleted" or "unavailable") fixture.Users[s.Actor + "@example.test"] = new(s.Actor, "fixture-password", Deleted: kind == "deleted", IdentityUnavailable: kind == "unavailable");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/folders"); request.Headers.Add("X-Cerberus-Vault-Access", s.Access);
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
        using var response = await Read(request, "/api/folders"); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("actor")][InlineData("access")][InlineData("size")][InlineData("tamper")]
    public async Task GivenCursorSubstitution_WhenContinuing_Then400(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); await Create(s); await Create(s);
        using var initial = await Read(s, "/api/folders?pageSize=1"); var cursor = (await Success<FolderListOutput>(initial, 200)).NextCursor!;
        if (kind == "actor") s = await Setup(owner);
        if (kind == "access") { var access = ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)); OpaqueAccessHandle.TryHash(access, out var verifier);
            await using var db = fixture.Context(); db.VaultAccessSessions.Add(new() { AccountId = s.InternalId, HandleVerifier = verifier, IssuedAt = DateTimeOffset.UtcNow.AddMinutes(-1), ExpiresAt = DateTimeOffset.UtcNow.AddHours(1), PolicyRevision = 1, RevocationGeneration = 1 }); await db.SaveChangesAsync(); s = s with { Access = access }; }
        if (kind == "tamper") { Assert.True(ProtocolBinary.TryDecode(cursor, null, out var bytes)); bytes[^1] ^= 1; cursor = ProtocolBinary.Encode(bytes); }
        var before = await Snapshot(s); using var response = await Read(s, "/api/folders?pageSize=" + (kind == "size" ? 2 : 1) + "&cursor=" + cursor);
        await Failure(response, 400, "validation_failed"); Assert.Equal(before, await Snapshot(s));
    }

    [FunctionalTheory][InlineData("envelope")][InlineData("revision")][InlineData("zero")][InlineData("duplicate")]
    [InlineData("time")][InlineData("unavailable")]
    public async Task GivenCorruptVisibleContentOrDependency_WhenListing_Then503WithoutPartialPayload(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var first = await Create(s); var second = await Create(s); await using var db = fixture.Context();
        if (kind == "envelope") await db.Folders.Where(x => x.PublicId == second.FolderId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Envelope, new byte[] { 1 }));
        if (kind == "revision") await db.Folders.Where(x => x.PublicId == second.FolderId).ExecuteUpdateAsync(x => x.SetProperty(r => r.Revision, 0));
        if (kind is "zero" or "duplicate") { var sequence = (await db.Folders.SingleAsync(x => x.PublicId == first.FolderId)).ServerSequence;
            await db.Folders.Where(x => x.PublicId == second.FolderId).ExecuteUpdateAsync(x => x.SetProperty(r => r.ServerSequence, kind == "zero" ? 0 : sequence)); }
        if (kind == "time") await db.Folders.Where(x => x.PublicId == second.FolderId).ExecuteUpdateAsync(x => x.SetProperty(r => r.EditedAt, DateTimeOffset.MinValue));
        if (kind == "unavailable") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection_folder RENAME TO http_fixture_unavailable_collection_folder");
        var before = await Snapshot(s, kind == "unavailable");
        try { using var response = await Read(s, kind == "duplicate" ? "/api/folders?pageSize=1" : "/api/folders"); await Failure(response, 503, "persistence_unavailable"); }
        finally { if (kind == "unavailable") await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.http_fixture_unavailable_collection_folder RENAME TO collection_folder"); }
        Assert.Equal(before, await Snapshot(s, kind == "unavailable"));
    }

    [FunctionalFact]
    public async Task GivenEmptyOwnedInventory_WhenListing_Then200EmptyPageWithoutCursor()
    { using var owner = new ProtectionFixture(); var s = await Setup(owner); using var r = await Read(s, "/api/folders"); var page = await Success<FolderListOutput>(r, 200); Assert.Empty(page.Items); Assert.Null(page.NextCursor); }
    [FunctionalTheory][InlineData("trash")][InlineData("terminal")]
    public async Task GivenSelectedProfileBecomesHidden_WhenListing_Then403WithoutMutation(string kind)
    {using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner);var p=await Profile(s,owner,scoped);await Create(s,[p.PublicId]);var selected=s with{Access=await Open(s,p,scoped)};await using(var db=fixture.Context()){if(kind=="trash")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));else{db.TerminalErasures.Add(new(){ResourceId=p.PublicId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}var before=await Snapshot(s);using var response=await Read(selected,"/api/folders");await Failure(response,403,"vault_access_denied");Assert.Equal(before,await Snapshot(s));}

    [FunctionalTheory][InlineData("wrapper")][InlineData("pins")][InlineData("epoch")]
    public async Task GivenCorruptRelevantNativeGrant_WhenListing_Then503WithoutAnyOwnedOrSharedPayload(string kind)
    {using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();var foreign=await Setup(owner);var s=await Setup(recipient);var shared=await Create(foreign);await Create(s);var(collection,grant)=await Share(foreign,owner,s,recipient,CollectionGrantAccess.ReadOnly,[shared.FolderId],[]);await using(var db=fixture.Context()){if(kind=="wrapper")await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,new byte[]{1}));if(kind=="pins")await db.VaultProtections.Where(x=>x.AccountId==foreign.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Material,new byte[]{1}));if(kind=="epoch")await db.Collections.Where(x=>x.Id==collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.KeyEpoch,2));}var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);using var response=await Read(s,"/api/folders");await Failure(response,503,"persistence_unavailable");Assert.Equal(before,await Snapshot(s));Assert.Equal(ownerBefore,await Snapshot(foreign));}

    [FunctionalTheory][InlineData("trash")][InlineData("terminal")][InlineData("owner")]
    public async Task GivenSharedContentBecomesHidden_WhenListing_ThenOmitBeforeNativeInspection(string kind)
    {using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();var foreign=await Setup(owner);var s=await Setup(recipient);var root=await Folder(foreign);var child=await Folder(foreign,root.Id);var shared=await Create(foreign,[],child.PublicId);var(collection,grant)=await Share(foreign,owner,s,recipient,CollectionGrantAccess.ReadOnly,[shared.FolderId],[]);await using(var db=fixture.Context()){await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,new byte[]{1}));if(kind=="trash")await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.DeletedAt,DateTimeOffset.UtcNow));if(kind=="owner")await db.Accounts.Where(x=>x.Id==foreign.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));if(kind=="terminal"){db.TerminalErasures.Add(new(){ResourceId=root.PublicId,ResourceKind="folder",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}var before=await Snapshot(s);using var response=await Read(s,"/api/folders");var page=await Success<FolderListOutput>(response,200);Assert.Empty(page.Items);Assert.Null(page.NextCursor);Assert.Equal(before,await Snapshot(s));}


    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenFreshNativeSelectedHandle_WhenListingFolders_ThenResolveDirectAndDescendantScopeWithoutOtherProfileLeakage(string mode)
    {using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();using var otherKey=new ProtectionFixture();var s=await Setup(owner);var root=await Folder(s);var child=await Folder(s,root.Id);var p=await Profile(s,owner,scoped,root,mode);var other=await Profile(s,owner,otherKey);var direct=await Create(s,[p.PublicId]);var descendant=await Create(s,[],child.PublicId);await Create(s,[other.PublicId]);await Create(s);var selected=s with{Access=await Open(s,p,scoped)};var before=await Snapshot(s);using var r=await Read(selected,"/api/folders");var list=await Success<FolderListOutput>(r,200);Assert.Equal(new[]{root.PublicId,child.PublicId,direct.FolderId,descendant.FolderId}.Order(),list.Items.Select(x=>x.FolderId).Order());Assert.Equal(before,await Snapshot(s));foreach(var item in list.Items)Fields(item,"folderId","revision","serverSequence","editedAt","envelope");}
    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly,false)][InlineData(CollectionGrantAccess.ReadOnly,true)][InlineData(CollectionGrantAccess.ReadWrite,false)][InlineData(CollectionGrantAccess.ReadWrite,true)]
    public async Task GivenNativeGrantedFolderRootAndOverlappingLeaf_WhenListing_ThenDoNotExposeUnsharedParentOrSibling(CollectionGrantAccess access,bool selected)
    {using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var scoped=new ProtectionFixture();var foreign=await Setup(owner);var s=await Setup(recipient);var parent=await Folder(foreign);var root=await Folder(foreign,parent.Id);var child=await Folder(foreign,root.Id);var leaf=await Create(foreign,[],child.PublicId);await Folder(foreign,parent.Id);var own=await Create(s);var(collection,_)=await Share(foreign,owner,s,recipient,access,[root.PublicId,leaf.FolderId],[root.Id]);var p=await Profile(s,recipient,scoped,collections:[collection.PublicId]);var request=selected?s with{Access=await Open(s,p,scoped)}:s;var before=await Snapshot(s);var foreignBefore=await Snapshot(foreign);using var r=await Read(request,"/api/folders");var list=await Success<FolderListOutput>(r,200);Assert.Equal((selected?new[]{root.PublicId,child.PublicId,leaf.FolderId}:new[]{root.PublicId,child.PublicId,leaf.FolderId,own.FolderId}).Order(),list.Items.Select(x=>x.FolderId).Order());Assert.DoesNotContain(list.Items,x=>x.FolderId==parent.PublicId);Assert.Equal(leaf.Envelope,list.Items.Single(x=>x.FolderId==leaf.FolderId).Envelope);Assert.Equal(before,await Snapshot(s));Assert.Equal(foreignBefore,await Snapshot(foreign));}
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenSelectedOwnedCollection_WhenListing_ThenIncludeOnlyMemberFolderAndDescendants(string mode)
    {using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner);var root=await Folder(s);var child=await Folder(s,root.Id);await Create(s);await using var db=fixture.Context();var collection=new VaultCollection{PublicId=Guid.NewGuid(),AccountId=s.InternalId,Envelope=Bytes(Envelope()),EditedAt=DateTimeOffset.UnixEpoch};db.Collections.Add(collection);await db.SaveChangesAsync();db.CollectionFolders.Add(new(){AccountId=s.InternalId,CollectionId=collection.Id,FolderId=root.Id});await db.SaveChangesAsync();var p=await Profile(s,owner,scoped,mode:mode,collections:[collection.PublicId]);var selected=s with{Access=await Open(s,p,scoped)};var before=await Snapshot(s);using var r=await Read(selected,"/api/folders");var page=await Success<FolderListOutput>(r,200);Assert.Equal(new[]{root.PublicId,child.PublicId}.Order(),page.Items.Select(x=>x.FolderId).Order());Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly,false)][InlineData(CollectionGrantAccess.ReadOnly,true)][InlineData(CollectionGrantAccess.ReadWrite,false)][InlineData(CollectionGrantAccess.ReadWrite,true)]
    public async Task GivenNativeRecordOnlyGrant_WhenListingFolders_ThenOmitContainingFolderAndUnusedCorruptGrant(CollectionGrantAccess access,bool selected)
    {using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var scoped=new ProtectionFixture();var foreign=await Setup(owner);var s=await Setup(recipient);var folder=await Folder(foreign);var input=new RecordCreateInput(Guid.NewGuid(),Envelope(),DateTimeOffset.UnixEpoch,[],folder.PublicId);using(var request=Request(foreign,Bytes(input),"/api/records")){using var created=await Gateway.Client.SendAsync(request);await Success<CreateRecordOutput>(created,201);}var(collection,grant)=await Share(foreign,owner,s,recipient,access,[],[]);await using(var db=fixture.Context()){var record=await db.Records.SingleAsync(x=>x.PublicId==input.RecordId);db.CollectionRecords.Add(new(){AccountId=foreign.InternalId,CollectionId=collection.Id,RecordId=record.Id});await db.SaveChangesAsync();}var p=await Profile(s,recipient,scoped,collections:[collection.PublicId]);var requestState=selected?s with{Access=await Open(s,p,scoped)}:s;using(var records=await Read(requestState,"/api/records")){var page=await Success<RecordListOutput>(records,200);Assert.Equal(input.RecordId,Assert.Single(page.Items).RecordId);}await using(var db=fixture.Context())await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(x=>x.RecipientKeyEnvelope,new byte[]{1}));var before=await Snapshot(s);var foreignBefore=await Snapshot(foreign);using var response=await Read(requestState,"/api/folders");var folders=await Success<FolderListOutput>(response,200);Assert.Empty(folders.Items);Assert.Null(folders.NextCursor);Assert.Equal(before,await Snapshot(s));Assert.Equal(foreignBefore,await Snapshot(foreign));}
    [FunctionalTheory][InlineData("Master")][InlineData("PerProfile")]
    public async Task GivenNativeSelectedRecordOnlyProfile_WhenListingFolders_ThenDoNotExposeContainingFolder(string mode)
    {using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner);var folder=await Folder(s);var input=new RecordCreateInput(Guid.NewGuid(),Envelope(),DateTimeOffset.UnixEpoch,[],folder.PublicId);using(var request=Request(s,Bytes(input),"/api/records")){using var created=await Gateway.Client.SendAsync(request);await Success<CreateRecordOutput>(created,201);}var p=await Profile(s,owner,scoped,mode:mode,records:[input.RecordId]);var selected=s with{Access=await Open(s,p,scoped)};using(var records=await Read(selected,"/api/records"))Assert.Equal(input.RecordId,Assert.Single((await Success<RecordListOutput>(records,200)).Items).RecordId);var before=await Snapshot(s);using var r=await Read(selected,"/api/folders");var page=await Success<FolderListOutput>(r,200);Assert.Empty(page.Items);Assert.Null(page.NextCursor);Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("record")][InlineData("profile")]
    public async Task GivenRealOtherResourceCursor_WhenListingFolders_ThenRejectItsPurpose(string kind)
    {using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();using var otherKey=new ProtectionFixture();var s=await Setup(owner);if(kind=="record"){for(var n=0;n<2;n++){using var request=Request(s,Bytes(new RecordCreateInput(Guid.NewGuid(),Envelope(),DateTimeOffset.UnixEpoch,[],null)),"/api/records");using var r=await Gateway.Client.SendAsync(request);await Success<CreateRecordOutput>(r,201);}}else{await Profile(s,owner,scoped);await Profile(s,owner,otherKey);}string cursor;using(var initial=await Read(s,kind=="record"?"/api/records?pageSize=1":"/api/profiles?pageSize=1")){Assert.Equal(HttpStatusCode.OK,initial.StatusCode);using var json=JsonDocument.Parse(await initial.Content.ReadAsStringAsync());cursor=json.RootElement.GetProperty("data").GetProperty("nextCursor").GetString()!;Assert.NotEmpty(cursor);}var before=await Snapshot(s);using var response=await Read(s,"/api/folders?pageSize=1&cursor="+cursor);await Failure(response,400,"validation_failed");Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData(false)][InlineData(true)]
    public async Task GivenCorruptOverlappingOrNonpageGrant_WhenListing_Then503WithoutOwnedOrSharedPartialContent(bool nonpage)
    {using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();var foreign=await Setup(owner);var s=await Setup(recipient);await Create(s);var first=await Create(foreign);var second=await Create(foreign);await Share(foreign,owner,s,recipient,CollectionGrantAccess.ReadOnly,[first.FolderId],[]);var(_,bad)=await Share(foreign,owner,s,recipient,CollectionGrantAccess.ReadOnly,[nonpage?second.FolderId:first.FolderId],[]);await using(var db=fixture.Context())await db.CollectionGrants.Where(x=>x.Id==bad.Id).ExecuteUpdateAsync(x=>x.SetProperty(x=>x.RecipientKeyEnvelope,new byte[]{1}));var before=await Snapshot(s);var foreignBefore=await Snapshot(foreign);using var r=await Read(s,"/api/folders?pageSize=1");await Failure(r,503,"persistence_unavailable");Assert.Equal(before,await Snapshot(s));Assert.Equal(foreignBefore,await Snapshot(foreign));}
    [FunctionalTheory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task GivenVisibleCycleOrHiddenCyclicAncestor_WhenListing_ThenFailOnlyForRelevantVisibleCorruption(bool selected,bool hidden)
    {using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner);var root=await Folder(s);await Folder(s,root.Id);var p=await Profile(s,owner,scoped,root);var visible=await Create(s,[p.PublicId]);var request=selected?s with{Access=await Open(s,p,scoped)}:s;await using(var db=fixture.Context())await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(x=>x.ParentFolderId,root.Id).SetProperty(x=>x.DeletedAt,hidden?DateTimeOffset.UtcNow:(DateTimeOffset?)null));var before=await Snapshot(s);using var response=await Read(request,"/api/folders");if(hidden){var page=await Success<FolderListOutput>(response,200);Assert.Equal(visible.FolderId,Assert.Single(page.Items).FolderId);}else await Failure(response,503,"persistence_unavailable");Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("record")][InlineData("profile")][InlineData("collection")][InlineData("grant")][InlineData("account")][InlineData("folder")]
    public async Task GivenSameUuidTerminalKind_WhenListing_ThenOnlyFolderMarkerOmitsFolder(string kind)
    {using var owner=new ProtectionFixture();var s=await Setup(owner);var input=await Create(s);await using(var db=fixture.Context()){db.TerminalErasures.Add(new(){ResourceId=input.FolderId,ResourceKind=kind,DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}var before=await Snapshot(s);using var response=await Read(s,"/api/folders");var page=await Success<FolderListOutput>(response,200);Assert.Equal(kind=="folder"?0:1,page.Items.Count);Assert.Equal(before,await Snapshot(s));}
    private async Task<FolderCreateInput> Create(State s,Guid[]? profiles=null,Guid? folder=null){var input=Input(profiles,folder);using var response=await Send(s,input);await Success<CreateFolderOutput>(response,201);return input;}
    private async Task<(VaultCollection,CollectionGrant)> Share(State owner,ProtectionFixture author,State recipient,ProtectionFixture recipientKeys,CollectionGrantAccess access,Guid[] direct,long[] folders)
    {await using var db=fixture.Context();var collection=new VaultCollection{PublicId=Guid.NewGuid(),AccountId=owner.InternalId,Envelope=Bytes(Envelope()),EditedAt=DateTimeOffset.UnixEpoch};db.Collections.Add(collection);await db.SaveChangesAsync();var grant=new CollectionGrant{PublicId=Guid.NewGuid(),CollectionId=collection.Id,RecipientAccountId=recipient.InternalId,Access=access};grant.RecipientKeyEnvelope=Bytes(author.WrapGrant(owner.AccountId,collection.PublicId,grant.PublicId,recipient.Actor,recipientKeys.Material.RecipientKey));db.CollectionGrants.Add(grant);var ids=(await db.Folders.Where(x=>direct.Contains(x.PublicId)).Select(x=>x.Id).ToArrayAsync()).Concat(folders).Distinct();db.CollectionFolders.AddRange(ids.Select(id=>new CollectionFolder{AccountId=owner.InternalId,CollectionId=collection.Id,FolderId=id}));await db.SaveChangesAsync();return(collection,grant);}
    private sealed class BodyFeature:IHttpRequestBodyDetectionFeature{public bool CanHaveBody=>true;}
    private sealed record State(Guid Actor,Guid AccountId,long InternalId,string Access);
    private async Task<State> Setup(ProtectionFixture client)
    {var actor=Guid.NewGuid();fixture.Users[actor+"@example.test"]=new(actor,"fixture-password");await using var db=fixture.Context();var account=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=Bytes(Envelope())};db.Accounts.Add(account);await db.SaveChangesAsync();var access=ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(access,out var verifier);db.VaultProtections.Add(new(){AccountId=account.Id,Material=Bytes(client.Material)});db.VaultAccessSessions.Add(new(){AccountId=account.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return new(actor,account.PublicId,account.Id,access);}
    private async Task<VaultFolder> Folder(State s,long? parent=null){var f=new VaultFolder{PublicId=Guid.NewGuid(),AccountId=s.InternalId,ParentFolderId=parent,Envelope=Bytes(Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.Context();db.Folders.Add(f);await db.SaveChangesAsync();return f;}
    private async Task<Profile> Profile(State s,ProtectionFixture owner,ProtectionFixture scoped,VaultFolder? folder=null,string mode="Master",Guid[]? collections=null,Guid[]? records=null)
    {var id=Guid.NewGuid();var input=new ProfileCreateInput(id,Envelope(),new(mode,scoped.Material.UnlockVerifier,owner.Wrap(s.AccountId,"profile",id,s.Actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null),DateTimeOffset.UnixEpoch,records??[],folder is null?[]:[folder.PublicId],collections??[]);using var request=Request(s,Bytes(input),"/api/profiles");using var r=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Created,r.StatusCode);await using var db=fixture.Context();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==id);}
    private async Task<string> Open(State s,Profile p,ProtectionFixture scoped)
    {await using var db=fixture.Context();var revision=(await db.Profiles.AsNoTracking().SingleAsync(x=>x.Id==p.Id)).Revision;var raw=Bytes(new{expectedRevision=revision});using var challengeRequest=Request(s,Bytes(new{expectedRevision=revision,requestHash=ProtocolBinary.Encode(SHA256.HashData(raw))}),"/api/profiles/"+p.PublicId+"/access/challenges");challengeRequest.Headers.Remove("X-Cerberus-Vault-Access");using var issued=await Gateway.Client.SendAsync(challengeRequest);var challenge=(await Success<IssueProfileAccessChallengeOutput>(issued,201)).Challenge;using var open=Request(s,raw,"/api/profiles/"+p.PublicId+"/access");open.Headers.Remove("X-Cerberus-Vault-Access");open.Headers.Add("X-Cerberus-Challenge-Id",challenge.ChallengeId.ToString());open.Headers.Add("X-Cerberus-Proof",scoped.Sign(challenge));using var response=await Gateway.Client.SendAsync(open);return (await Success<OpenProfileAccessOutput>(response,200)).VaultAccess;}
    private static FolderCreateInput Input(Guid[]? profiles=null,Guid? folder=null)=>new(Guid.NewGuid(),Envelope(),DateTimeOffset.UnixEpoch.AddTicks(19),profiles??[],folder);
    private static EncryptedEnvelope Envelope(long epoch=1)=>new("cerberus-content-v1",epoch,ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(12)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(73)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(16)));
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private static DateTimeOffset Normalize(DateTimeOffset value)=>value.AddTicks(-(value.Ticks%10));
    private static HttpRequestMessage Request(State s,byte[] body,string path="/api/folders"){var r=new HttpRequestMessage(HttpMethod.Post,path){Content=new ByteArrayContent(body)};r.Content.Headers.ContentType=new("application/json");r.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));r.Headers.Add("X-Cerberus-Vault-Access",s.Access);return r;}
    private async Task<HttpResponseMessage> Send(State s,FolderCreateInput input){using var request=Request(s,Bytes(input));return await Gateway.Client.SendAsync(request);}
    private async Task<HttpResponseMessage> Read(State s,string path){using var request=new HttpRequestMessage(HttpMethod.Get,path);request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));request.Headers.Add("X-Cerberus-Vault-Access",s.Access);return await Gateway.Client.SendAsync(request);}
    private async Task<string> Protected(State s){await using var db=fixture.Context();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleAsync(x=>x.AccountId==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync()});}
    private static async Task<T> Success<T>(HttpResponseMessage r,int status) where T:class{Assert.Equal((HttpStatusCode)status,r.StatusCode);Assert.True(r.Headers.CacheControl?.NoStore);var result=await r.Content.ReadFromJsonAsync<DataOutput<T>>();Assert.True(result!.Success);return Assert.IsType<T>(result.Data);}
    private static void Fields(object value,params string[] names){using var d=JsonDocument.Parse(JsonSerializer.Serialize(value,ProtectionFixture.Json));Assert.Equal(names.Order(),d.RootElement.EnumerateObject().Select(x=>x.Name).Order());}
    private static async Task Failure(HttpResponseMessage r,int status,string? error=null){Assert.Equal((HttpStatusCode)status,r.StatusCode);Assert.True(r.Headers.CacheControl?.NoStore);if(status==413){Assert.Equal("",await r.Content.ReadAsStringAsync());return;}using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.False(d.RootElement.GetProperty("success").GetBoolean());if(d.RootElement.TryGetProperty("data",out var data))Assert.Equal(JsonValueKind.Null,data.ValueKind);if(error is not null)Assert.Contains(error,d.RootElement.GetProperty("errors").EnumerateArray().Select(x=>x.GetString()));}
    private async Task<string> Snapshot(State s,bool skipMembers=false){await using var db=fixture.Context();return JsonSerializer.Serialize(new{Protected=await Protected(s),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>x.RecipientAccountId==s.InternalId || db.Collections.Any(c=>c.Id==x.CollectionId && c.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync(),Members=await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),CollectionFolders=skipMembers?[]:await db.CollectionFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileCollections=await db.ProfileCollections.AsNoTracking().Where(x=>db.Profiles.Any(p=>p.Id==x.ProfileId && p.AccountId==s.InternalId)).OrderBy(x=>x.ProfileId).ThenBy(x=>x.CollectionId).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),ProfileFolders=await db.ProfileFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileRecords=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync()});}
}
