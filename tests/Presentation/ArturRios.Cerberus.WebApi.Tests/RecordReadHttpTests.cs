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
public class RecordReadHttpTests(RegistrationApiFixture fixture) : WebApiTest<Program>(EnvironmentType.Local)
{
    [FunctionalFact]
    public async Task GivenHttpCreatedOwnedRecord_WhenGetting_ThenReturnEightFieldsAndCurrentDirectRelationshipsWithoutMutation()
    {
        using var owner=new ProtectionFixture();using var key1=new ProtectionFixture();using var key2=new ProtectionFixture();var s=await Setup(owner);var root=await Folder(s);var child=await Folder(s,root.Id);
        var p=await Profile(s,owner,key1);var q=await Profile(s,owner,key2);var record=await Create(s,[p.PublicId,q.PublicId],child.PublicId);var before=await Snapshot(s);
        using var response=await Read(s,"/api/records/"+record.RecordId);var result=await Success<RecordDetailsOutput>(response,200);Fields(result,"recordId","revision","serverSequence","editedAt","envelope","profileIds","folderId","collectionIds");
        Assert.Equal(record.RecordId,result.RecordId);Assert.Equal(record.Envelope,result.Envelope);Assert.Equal(Normalize(record.EditedAt),result.EditedAt);Assert.Equal(new[]{p.PublicId,q.PublicId}.Order(),result.ProfileIds);Assert.Equal(child.PublicId,result.FolderId);Assert.Empty(result.CollectionIds);
        await using var db=fixture.Context();var persisted=await db.Records.AsNoTracking().SingleAsync(x=>x.PublicId==record.RecordId);Assert.Equal(persisted.Revision,result.Revision);Assert.Equal(persisted.ServerSequence,result.ServerSequence);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("Master",false)][InlineData("Master",true)][InlineData("PerProfile",false)][InlineData("PerProfile",true)]
    public async Task GivenNativeSelectedProfile_WhenGetting_ThenHideOtherProfilesAndParentWithoutFolderRoute(string mode,bool folderRoute)
    {
        using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();using var other=new ProtectionFixture();var s=await Setup(owner);var root=await Folder(s);var child=await Folder(s,root.Id);
        var p=await Profile(s,owner,scoped,folderRoute?root:null,mode);var q=await Profile(s,owner,other);var record=await Create(s,[p.PublicId,q.PublicId],child.PublicId);var outside=await Create(s,[q.PublicId]);var descendant=await Create(s,folder:child.PublicId);
        var selected=s with{Access=await Open(s,p,scoped)};var before=await Snapshot(s);using var response=await Read(selected,"/api/records/"+record.RecordId);var result=await Success<RecordDetailsOutput>(response,200);
        Assert.Equal(new[]{p.PublicId},result.ProfileIds);Assert.Equal(folderRoute?child.PublicId:null,result.FolderId);Assert.Empty(result.CollectionIds);Assert.Equal(record.Envelope,result.Envelope);
        using var denied=await Read(selected,"/api/records/"+outside.RecordId);await Failure(denied,404,"not_found");using var dynamic=await Read(selected,"/api/records/"+descendant.RecordId);
        if(folderRoute){var item=await Success<RecordDetailsOutput>(dynamic,200);Assert.Empty(item.ProfileIds);Assert.Equal(child.PublicId,item.FolderId);}else await Failure(dynamic,404,"not_found");Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData(CollectionGrantAccess.ReadOnly,false,false)][InlineData(CollectionGrantAccess.ReadOnly,true,false)][InlineData(CollectionGrantAccess.ReadOnly,true,true)]
    [InlineData(CollectionGrantAccess.ReadWrite,false,true)][InlineData(CollectionGrantAccess.ReadWrite,true,false)][InlineData(CollectionGrantAccess.ReadWrite,true,true)]
    public async Task GivenNativeRecipientGrant_WhenGetting_ThenExposeOnlyIncludedCurrentRecipientRelationships(CollectionGrantAccess access,bool folderRoute,bool selectedScope)
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();using var ownerKey=new ProtectionFixture();using var recipientKey=new ProtectionFixture();var foreign=await Setup(owner);var s=await Setup(recipient);var root=await Folder(foreign);var child=await Folder(foreign,root.Id);var ownerProfile=await Profile(foreign,owner,ownerKey,root);
        var shared=await Create(foreign,[ownerProfile.PublicId],child.PublicId);var outside=await Create(foreign);var(collection,grant)=await Share(foreign,owner,s,recipient,access,folderRoute?[]:[shared.RecordId],folderRoute?[root.Id]:[]);
        var p=await Profile(s,recipient,recipientKey,collections:[collection.PublicId]);var request=selectedScope?s with{Access=await Open(s,p,recipientKey)}:s;var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);
        using var response=await Read(request,"/api/records/"+shared.RecordId);var item=await Success<RecordDetailsOutput>(response,200);Assert.Empty(item.ProfileIds);Assert.Equal(new[]{collection.PublicId},item.CollectionIds);Assert.Equal(folderRoute?child.PublicId:null,item.FolderId);Assert.Equal(shared.Envelope,item.Envelope);
        using var hidden=await Read(request,"/api/records/"+outside.RecordId);await Failure(hidden,404,"not_found");Assert.Equal(before,await Snapshot(s));Assert.Equal(ownerBefore,await Snapshot(foreign));
        await using(var db=fixture.Context())await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.State,CollectionGrantState.Revoked));var after=await Snapshot(s);using var revoked=await Read(request,"/api/records/"+shared.RecordId);await Failure(revoked,404,"not_found");Assert.Equal(after,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("bad")][InlineData("zero")][InlineData("uppercase")][InlineData("compact")][InlineData("braced")][InlineData("urn")]
    public async Task GivenNoncanonicalPublicId_WhenGetting_Then400WithoutMutation(string kind)
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var id=Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");var text=kind switch{"bad"=>"bad","zero"=>Guid.Empty.ToString(),"uppercase"=>id.ToString().ToUpperInvariant(),"compact"=>id.ToString("N"),"braced"=>id.ToString("B"),_=>"urn:uuid:"+id};var before=await Snapshot(s);using var r=await Read(s,"/api/records/"+text);await Failure(r,400,"validation_failed");Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalTheory][InlineData("?owner=other")][InlineData("?profileId=other")][InlineData("?pageSize=1")][InlineData("?cursor=opaque")][InlineData("?search=secret")][InlineData("?id=other")]
    public async Task GivenAnyQueryOverride_WhenGetting_Then400WithoutMutation(string query)
    {using var owner=new ProtectionFixture();var s=await Setup(owner);var before=await Snapshot(s);using var r=await Read(s,"/api/records/11111111-1111-1111-1111-111111111111"+query);await Failure(r,400,"validation_failed");Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("envelope")][InlineData("revision")][InlineData("zero")][InlineData("time")][InlineData("unavailable")]
    public async Task GivenCorruptVisibleContentOrDependency_WhenGetting_Then503WithoutPartialPayload(string kind)
    {
        using var owner=new ProtectionFixture();var s=await Setup(owner);var target=await Create(s);await using var db=fixture.Context();
        if(kind=="envelope")await db.Records.Where(x=>x.PublicId==target.RecordId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Envelope,new byte[]{1}));if(kind=="revision")await db.Records.Where(x=>x.PublicId==target.RecordId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.Revision,0));if(kind=="zero")await db.Records.Where(x=>x.PublicId==target.RecordId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.ServerSequence,0));if(kind=="time")await db.Records.Where(x=>x.PublicId==target.RecordId).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.EditedAt,DateTimeOffset.MinValue));if(kind=="unavailable")await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.collection_record RENAME TO http_fixture_unavailable_collection_record");
        var before=await Snapshot(s,kind=="unavailable");try{using var response=await Read(s,"/api/records/"+target.RecordId);await Failure(response,503,"persistence_unavailable");}finally{if(kind=="unavailable")await db.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.http_fixture_unavailable_collection_record RENAME TO collection_record");}Assert.Equal(before,await Snapshot(s,kind=="unavailable"));
    }
    [FunctionalTheory][InlineData(false,false)][InlineData(false,true)][InlineData(true,false)][InlineData(true,true)]
    public async Task GivenCyclicOrHiddenAncestry_WhenGetting_ThenDistinguishRelevant503FromHidden404(bool hidden,bool selectedScope)
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await Setup(owner);var root=await Folder(s);var child=await Folder(s,root.Id);var p=await Profile(s,owner,key,root);var target=await Create(s,[p.PublicId],child.PublicId);var request=selectedScope?s with{Access=await Open(s,p,key)}:s;
        await using(var db=fixture.Context())await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,child.Id).SetProperty(f=>f.DeletedAt,hidden?(DateTimeOffset?)DateTimeOffset.UtcNow:null));var before=await Snapshot(s);using var response=await Read(request,"/api/records/"+target.RecordId);await Failure(response,hidden?404:503,hidden?"not_found":"persistence_unavailable");Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenUnknownTarget_WhenGetting_ThenNonrevealing404WithoutMutation()
    {using var owner=new ProtectionFixture();var s=await Setup(owner);var before=await Snapshot(s);using var r=await Read(s,"/api/records/"+Guid.NewGuid());await Failure(r,404,"not_found");Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("missing", 401)][InlineData("bad", 400)][InlineData("duplicate", 400)]
    [InlineData("body", 400)][InlineData("oversized", 413)][InlineData("transfer", 400)]
    public async Task GivenMalformedAccessOrGetBody_WhenGetting_ThenRejectWithoutMutation(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s);
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/records/11111111-1111-1111-1111-111111111111"); request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor));
        if (kind != "missing") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", kind == "bad" ? "bad" : s.Access);
        if (kind == "duplicate") request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access", s.Access);
        if (kind is "body" or "oversized") request.Content = new StringContent(kind == "body" ? "{}" : new string(' ', 65537));
        if (kind == "transfer") { request.Content = new StringContent("{}"); request.Headers.TransferEncodingChunked = true; }
        using var response = await Gateway.Client.SendAsync(request); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }


    [FunctionalTheory][InlineData("empty")][InlineData("http2")]
    public async Task GivenRawEmptyHeaderOrUnknownLengthBody_WhenGetting_Then400(string kind)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s); using var host = new WebApplicationFactory<Program>();
        var context = await host.Server.SendAsync(c => { c.Request.Method = "GET"; c.Request.Path = "/api/records/11111111-1111-1111-1111-111111111111";
            c.Request.Headers.Authorization = "Bearer " + RegistrationApiFixture.Token(s.Actor); c.Request.Headers["X-Cerberus-Vault-Access"] = kind == "empty" ? "" : s.Access;
            if (kind == "http2") c.Features.Set<IHttpRequestBodyDetectionFeature>(new BodyFeature()); });
        Assert.Equal(400, context.Response.StatusCode); Assert.Contains("no-store", context.Response.Headers.CacheControl.ToString()); Assert.Equal(before, await Snapshot(s));
    }


    [FunctionalTheory][InlineData("missing", 401)][InlineData("wrongScope", 401)][InlineData("deleted", 401)][InlineData("unavailable", 503)]
    public async Task GivenInvalidCurrentIdentity_WhenGetting_ThenRejectWithoutPersistence(string kind, int status)
    {
        using var owner = new ProtectionFixture(); var s = await Setup(owner); var before = await Snapshot(s);
        if (kind is "deleted" or "unavailable") fixture.Users[s.Actor + "@example.test"] = new(s.Actor, "fixture-password", Deleted: kind == "deleted", IdentityUnavailable: kind == "unavailable");
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/records/11111111-1111-1111-1111-111111111111"); request.Headers.Add("X-Cerberus-Vault-Access", s.Access);
        if (kind != "missing") request.Headers.Authorization = new("Bearer", RegistrationApiFixture.Token(s.Actor, kind == "wrongScope" ? Guid.NewGuid() : null));
        using var response = await Gateway.Client.SendAsync(request); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }


    [FunctionalTheory][InlineData("missing", 404)][InlineData("closing", 404)][InlineData("terminal", 404)]
    [InlineData("revoked", 403)][InlineData("expired", 403)][InlineData("future", 403)][InlineData("policy", 403)]
    [InlineData("generation", 403)][InlineData("dangling", 403)]
    public async Task GivenInvalidCurrentAccountOrSession_WhenGetting_ThenFailClosed(string kind, int status)
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
        using var response = await Read(request, "/api/records/11111111-1111-1111-1111-111111111111"); await Failure(response, status); Assert.Equal(before, await Snapshot(s));
    }


    [FunctionalTheory][InlineData("trash")][InlineData("terminal")]
    public async Task GivenSelectedProfileBecomesHidden_WhenGetting_Then403WithoutMutation(string kind)
    {using var owner=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(owner);var p=await Profile(s,owner,scoped);await Create(s,[p.PublicId]);var selected=s with{Access=await Open(s,p,scoped)};await using(var db=fixture.Context()){if(kind=="trash")await db.Profiles.Where(x=>x.Id==p.Id).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.DeletedAt,DateTimeOffset.UtcNow));else{db.TerminalErasures.Add(new(){ResourceId=p.PublicId,ResourceKind="profile",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}var before=await Snapshot(s);using var response=await Read(selected,"/api/records/11111111-1111-1111-1111-111111111111");await Failure(response,403,"vault_access_denied");Assert.Equal(before,await Snapshot(s));}


    [FunctionalTheory][InlineData("wrapper")][InlineData("pins")][InlineData("epoch")]
    public async Task GivenCorruptRelevantNativeGrant_WhenGetting_Then503WithoutAnyPayload(string kind)
    {using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();var foreign=await Setup(owner);var s=await Setup(recipient);var shared=await Create(foreign);await Create(s);var(collection,grant)=await Share(foreign,owner,s,recipient,CollectionGrantAccess.ReadOnly,[shared.RecordId],[]);await using(var db=fixture.Context()){if(kind=="wrapper")await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,new byte[]{1}));if(kind=="pins")await db.VaultProtections.Where(x=>x.AccountId==foreign.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Material,new byte[]{1}));if(kind=="epoch")await db.Collections.Where(x=>x.Id==collection.Id).ExecuteUpdateAsync(x=>x.SetProperty(c=>c.KeyEpoch,2));}var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);using var response=await Read(s,"/api/records/"+shared.RecordId);await Failure(response,503,"persistence_unavailable");Assert.Equal(before,await Snapshot(s));Assert.Equal(ownerBefore,await Snapshot(foreign));}


    [FunctionalTheory][InlineData("trash")][InlineData("terminal")][InlineData("owner")]
    public async Task GivenSharedContentBecomesHidden_WhenGetting_ThenOmitBeforeNativeInspection(string kind)
    {using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();var foreign=await Setup(owner);var s=await Setup(recipient);var root=await Folder(foreign);var child=await Folder(foreign,root.Id);var shared=await Create(foreign,[],child.PublicId);var(collection,grant)=await Share(foreign,owner,s,recipient,CollectionGrantAccess.ReadOnly,[shared.RecordId],[]);await using(var db=fixture.Context()){await db.CollectionGrants.Where(x=>x.Id==grant.Id).ExecuteUpdateAsync(x=>x.SetProperty(g=>g.RecipientKeyEnvelope,new byte[]{1}));if(kind=="trash")await db.Folders.Where(x=>x.Id==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.DeletedAt,DateTimeOffset.UtcNow));if(kind=="owner")await db.Accounts.Where(x=>x.Id==foreign.InternalId).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.State,AccountState.ClosurePending));if(kind=="terminal"){db.TerminalErasures.Add(new(){ResourceId=root.PublicId,ResourceKind="folder",DeletedAt=DateTimeOffset.UtcNow});await db.SaveChangesAsync();}}var before=await Snapshot(s);using var response=await Read(s,"/api/records/"+shared.RecordId);await Failure(response,404,"not_found");Assert.Equal(before,await Snapshot(s));}

    [FunctionalFact]
    public async Task GivenDirectProfileLinkRemoved_WhenGettingAgain_ThenRefreshReferencesAndDenyFormerSelection()
    {
        using var owner=new ProtectionFixture();using var key=new ProtectionFixture();var s=await Setup(owner);var p=await Profile(s,owner,key);var target=await Create(s,[p.PublicId]);var selected=s with{Access=await Open(s,p,key)};
        using(var first=await Read(selected,"/api/records/"+target.RecordId)){var item=await Success<RecordDetailsOutput>(first,200);Assert.Equal(new[]{p.PublicId},item.ProfileIds);}
        await using(var db=fixture.Context())await db.ProfileRecords.Where(x=>x.ProfileId==p.Id).ExecuteDeleteAsync();var before=await Snapshot(s);
        using(var owned=await Read(s,"/api/records/"+target.RecordId)){var item=await Success<RecordDetailsOutput>(owned,200);Assert.Empty(item.ProfileIds);Assert.Equal(target.Envelope,item.Envelope);}
        using var denied=await Read(selected,"/api/records/"+target.RecordId);await Failure(denied,404,"not_found");Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenSharedDescendantMovedOutsideCollection_WhenGettingAgain_ThenReevaluateActualFolderMembership()
    {
        using var owner=new ProtectionFixture();using var recipient=new ProtectionFixture();var foreign=await Setup(owner);var s=await Setup(recipient);var root=await Folder(foreign);var child=await Folder(foreign,root.Id);var outside=await Folder(foreign);var target=await Create(foreign,folder:child.PublicId);await Share(foreign,owner,s,recipient,CollectionGrantAccess.ReadOnly,[],[root.Id]);
        using(var first=await Read(s,"/api/records/"+target.RecordId)){var item=await Success<RecordDetailsOutput>(first,200);Assert.Equal(child.PublicId,item.FolderId);}
        await using(var db=fixture.Context())await db.Folders.Where(x=>x.Id==child.Id).ExecuteUpdateAsync(x=>x.SetProperty(f=>f.ParentFolderId,outside.Id));var before=await Snapshot(s);var ownerBefore=await Snapshot(foreign);using var denied=await Read(s,"/api/records/"+target.RecordId);await Failure(denied,404,"not_found");Assert.Equal(before,await Snapshot(s));Assert.Equal(ownerBefore,await Snapshot(foreign));
    }
    private async Task<RecordCreateInput> Create(State s,Guid[]? profiles=null,Guid? folder=null){var input=Input(profiles,folder);using var response=await Send(s,input);await Success<CreateRecordOutput>(response,201);return input;}
    private async Task<(VaultCollection,CollectionGrant)> Share(State owner,ProtectionFixture author,State recipient,ProtectionFixture recipientKeys,CollectionGrantAccess access,Guid[] records,long[] folders){await using var db=fixture.Context();var collection=new VaultCollection{PublicId=Guid.NewGuid(),AccountId=owner.InternalId,Envelope=Bytes(Envelope()),EditedAt=DateTimeOffset.UnixEpoch};db.Collections.Add(collection);await db.SaveChangesAsync();var grant=new CollectionGrant{PublicId=Guid.NewGuid(),CollectionId=collection.Id,RecipientAccountId=recipient.InternalId,Access=access};grant.RecipientKeyEnvelope=Bytes(author.WrapGrant(owner.AccountId,collection.PublicId,grant.PublicId,recipient.Actor,recipientKeys.Material.RecipientKey));db.CollectionGrants.Add(grant);foreach(var id in records){var record=await db.Records.SingleAsync(x=>x.PublicId==id);db.CollectionRecords.Add(new(){AccountId=owner.InternalId,CollectionId=collection.Id,RecordId=record.Id});}db.CollectionFolders.AddRange(folders.Select(id=>new CollectionFolder{AccountId=owner.InternalId,CollectionId=collection.Id,FolderId=id}));await db.SaveChangesAsync();return(collection,grant);}
    private sealed class BodyFeature:IHttpRequestBodyDetectionFeature{public bool CanHaveBody=>true;}
    private sealed record State(Guid Actor,Guid AccountId,long InternalId,string Access);
    private async Task<State> Setup(ProtectionFixture client)
    {var actor=Guid.NewGuid();fixture.Users[actor+"@example.test"]=new(actor,"fixture-password");await using var db=fixture.Context();var account=new Account{PublicId=Guid.NewGuid(),HeimdallPublicId=actor,DetailsEnvelope=Bytes(Envelope())};db.Accounts.Add(account);await db.SaveChangesAsync();var access=ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32));OpaqueAccessHandle.TryHash(access,out var verifier);db.VaultProtections.Add(new(){AccountId=account.Id,Material=Bytes(client.Material)});db.VaultAccessSessions.Add(new(){AccountId=account.Id,HandleVerifier=verifier,IssuedAt=DateTimeOffset.UtcNow.AddMinutes(-1),ExpiresAt=DateTimeOffset.UtcNow.AddHours(1),PolicyRevision=1,RevocationGeneration=1});await db.SaveChangesAsync();return new(actor,account.PublicId,account.Id,access);}
    private async Task<VaultFolder> Folder(State s,long? parent=null){var f=new VaultFolder{PublicId=Guid.NewGuid(),AccountId=s.InternalId,ParentFolderId=parent,Envelope=Bytes(Envelope()),EditedAt=DateTimeOffset.UnixEpoch};await using var db=fixture.Context();db.Folders.Add(f);await db.SaveChangesAsync();return f;}
    private async Task<Profile> Profile(State s,ProtectionFixture owner,ProtectionFixture scoped,VaultFolder? folder=null,string mode="Master",Guid[]? collections=null)
    {var id=Guid.NewGuid();var input=new ProfileCreateInput(id,Envelope(),new(mode,scoped.Material.UnlockVerifier,owner.Wrap(s.AccountId,"profile",id,s.Actor),mode=="PerProfile"?scoped.Material.PasswordWrapper:null),DateTimeOffset.UnixEpoch,[],folder is null?[]:[folder.PublicId],collections??[]);using var request=Request(s,Bytes(input),"/api/profiles");using var r=await Gateway.Client.SendAsync(request);Assert.Equal(HttpStatusCode.Created,r.StatusCode);await using var db=fixture.Context();return await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==id);}
    private async Task<string> Open(State s,Profile p,ProtectionFixture scoped)
    {await using var db=fixture.Context();var revision=(await db.Profiles.AsNoTracking().SingleAsync(x=>x.Id==p.Id)).Revision;var raw=Bytes(new{expectedRevision=revision});using var challengeRequest=Request(s,Bytes(new{expectedRevision=revision,requestHash=ProtocolBinary.Encode(SHA256.HashData(raw))}),"/api/profiles/"+p.PublicId+"/access/challenges");challengeRequest.Headers.Remove("X-Cerberus-Vault-Access");using var issued=await Gateway.Client.SendAsync(challengeRequest);var challenge=(await Success<IssueProfileAccessChallengeOutput>(issued,201)).Challenge;using var open=Request(s,raw,"/api/profiles/"+p.PublicId+"/access");open.Headers.Remove("X-Cerberus-Vault-Access");open.Headers.Add("X-Cerberus-Challenge-Id",challenge.ChallengeId.ToString());open.Headers.Add("X-Cerberus-Proof",scoped.Sign(challenge));using var response=await Gateway.Client.SendAsync(open);return (await Success<OpenProfileAccessOutput>(response,200)).VaultAccess;}
    private static RecordCreateInput Input(Guid[]? profiles=null,Guid? folder=null)=>new(Guid.NewGuid(),Envelope(),DateTimeOffset.UnixEpoch.AddTicks(19),profiles??[],folder);
    private static EncryptedEnvelope Envelope(long epoch=1)=>new("cerberus-content-v1",epoch,ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(32)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(12)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(73)),ProtocolBinary.Encode(RandomNumberGenerator.GetBytes(16)));
    private static byte[] Bytes<T>(T value)=>JsonSerializer.SerializeToUtf8Bytes(value,ProtectionFixture.Json);
    private static DateTimeOffset Normalize(DateTimeOffset value)=>value.AddTicks(-(value.Ticks%10));
    private static HttpRequestMessage Request(State s,byte[] body,string path="/api/records"){var r=new HttpRequestMessage(HttpMethod.Post,path){Content=new ByteArrayContent(body)};r.Content.Headers.ContentType=new("application/json");r.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));r.Headers.Add("X-Cerberus-Vault-Access",s.Access);return r;}
    private async Task<HttpResponseMessage> Send(State s,RecordCreateInput input){using var request=Request(s,Bytes(input));return await Gateway.Client.SendAsync(request);}
    private async Task<HttpResponseMessage> Read(State s,string path){using var request=new HttpRequestMessage(HttpMethod.Get,path);request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));request.Headers.Add("X-Cerberus-Vault-Access",s.Access);return await Gateway.Client.SendAsync(request);}
    private async Task<string> Protected(State s){await using var db=fixture.Context();return JsonSerializer.Serialize(new{Account=await db.Accounts.AsNoTracking().SingleAsync(x=>x.Id==s.InternalId),Protection=await db.VaultProtections.AsNoTracking().SingleAsync(x=>x.AccountId==s.InternalId),Sessions=await db.VaultAccessSessions.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync()});}
    private async Task<string> Snapshot(State s,bool skipMembers=false){await using var db=fixture.Context();return JsonSerializer.Serialize(new{Protected=await Protected(s),Collections=await db.Collections.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Grants=await db.CollectionGrants.AsNoTracking().Where(x=>x.RecipientAccountId==s.InternalId || db.Collections.Any(c=>c.Id==x.CollectionId && c.AccountId==s.InternalId)).OrderBy(x=>x.Id).ToArrayAsync(),Members=skipMembers?[]:await db.CollectionRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.RecordId).ToArrayAsync(),CollectionFolders=await db.CollectionFolders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.CollectionId).ThenBy(x=>x.FolderId).ToArrayAsync(),ProfileCollections=await db.ProfileCollections.AsNoTracking().Where(x=>db.Profiles.Any(p=>p.Id==x.ProfileId && p.AccountId==s.InternalId)).OrderBy(x=>x.ProfileId).ThenBy(x=>x.CollectionId).ToArrayAsync(),Records=await db.Records.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Profiles=await db.Profiles.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),Folders=await db.Folders.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.Id).ToArrayAsync(),ProfileRecords=await db.ProfileRecords.AsNoTracking().Where(x=>x.AccountId==s.InternalId).OrderBy(x=>x.ProfileId).ThenBy(x=>x.RecordId).ToArrayAsync()});}
    private static async Task<T> Success<T>(HttpResponseMessage r,int status) where T:class{Assert.Equal((HttpStatusCode)status,r.StatusCode);Assert.True(r.Headers.CacheControl?.NoStore);var result=await r.Content.ReadFromJsonAsync<DataOutput<T>>();Assert.True(result!.Success);return Assert.IsType<T>(result.Data);}
    private static void Fields(object value,params string[] names){using var d=JsonDocument.Parse(JsonSerializer.Serialize(value,ProtectionFixture.Json));Assert.Equal(names.Order(),d.RootElement.EnumerateObject().Select(x=>x.Name).Order());}
    private static async Task Failure(HttpResponseMessage r,int status,string? error=null){Assert.Equal((HttpStatusCode)status,r.StatusCode);Assert.True(r.Headers.CacheControl?.NoStore);if(status==413){Assert.Equal("",await r.Content.ReadAsStringAsync());return;}using var d=JsonDocument.Parse(await r.Content.ReadAsStringAsync());Assert.False(d.RootElement.GetProperty("success").GetBoolean());if(d.RootElement.TryGetProperty("data",out var data))Assert.Equal(JsonValueKind.Null,data.ValueKind);if(error is not null)Assert.Contains(error,d.RootElement.GetProperty("errors").EnumerateArray().Select(x=>x.GetString()));}
}
