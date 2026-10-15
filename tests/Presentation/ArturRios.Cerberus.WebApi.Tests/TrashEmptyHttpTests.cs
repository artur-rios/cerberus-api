using System.Net;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.TestSupport;
using Microsoft.EntityFrameworkCore;
namespace ArturRios.Cerberus.WebApi.Tests;

public partial class TrashRestoreHttpTests
{
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenActualNativeTrash_WhenEmptyingViaHttp_ThenPhysicalErasureAndExactlyFourSafeCounts(string kind)
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(keys);var id=Guid.NewGuid();var env=Envelope();object input=kind switch{
            "record"=>new{recordId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),folderId=(Guid?)null},
            "folder"=>new{folderId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),parentFolderId=(Guid?)null},
            "collection"=>new{collectionId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),recordIds=Array.Empty<Guid>(),folderIds=Array.Empty<Guid>()},
            _=>new ProfileCreateInput(id,env,new("Master",scoped.Material.UnlockVerifier,keys.Wrap(s.AccountId,"profile",id,s.Actor),null),DateTimeOffset.UnixEpoch,[],[],[])};
        var route="/api/"+(kind=="collection"?"collections":kind=="profile"?"profiles":kind+"s");using(var created=await Send(s,route,HttpMethod.Post,Bytes(input)))Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        using(var trashed=await Send(s,route+"/"+id,HttpMethod.Delete,Bytes(new{expectedRevision=1})))await Success(trashed);
        using var response=await Send(s,"/api/trash",HttpMethod.Delete,Bytes(new{expectedRevision=1}));var data=await Success(response);CheckEmptyResult(data,s.AccountId,1,kind);Assert.DoesNotContain(id.ToString(),data.GetRawText());
        await using var db=fixture.Context();Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind==kind&&x.ResourceId==id));Assert.False(await db.TrashOperations.AnyAsync(x=>x.AccountId==s.Id));
        using(var read=await Send(s,route+"/"+id))await Failure(read,404,"not_found");using(var retry=await Send(s,"/api/trash",HttpMethod.Delete,Bytes(new{expectedRevision=1})))CheckEmptyResult(await Success(retry),s.AccountId,0,null);
        if(Environment.GetEnvironmentVariable("CERBERUS_CAPTURE_UC52_JSON")=="1")await File.WriteAllTextAsync("/tmp/cerberus-uc52-http-"+kind+".json",data.GetRawText());
    }
    [FunctionalTheory][InlineData("{}")][InlineData("{\"expectedRevision\":0}")][InlineData("{\"expectedRevision\":-1}")]
    [InlineData("{\"expectedRevision\":9007199254740992}")][InlineData("{\"expectedRevision\":\"1\"}")][InlineData("{\"expectedRevision\":1.0}")]
    [InlineData("{\"expectedRevision\":1,\"expectedRevision\":1}")][InlineData("{\"ExpectedRevision\":1}")]
    [InlineData("{\"expectedRevision\":1,\"ownerId\":\"x\"}")][InlineData("{\"expectedRevision\":null}")]
    [InlineData("null")][InlineData("[]")][InlineData("bad")][InlineData("")]
    public async Task GivenMalformedEmptyBody_WhenDeletingTrash_Then400AndNoMutation(string raw)
    {using var keys=new ProtectionFixture();var s=await Setup(keys);await CreateTrashRecord(s,[]);var before=await Snapshot(s);using var r=await Send(s,"/api/trash",HttpMethod.Delete,Encoding.UTF8.GetBytes(raw));await Failure(r,400,"validation_failed");Assert.Equal(before,await Snapshot(s));}
    [FunctionalTheory][InlineData("query",400)][InlineData("missingAccess",401)][InlineData("badAccess",400)]
    [InlineData("duplicateAccess",400)][InlineData("authority",400)][InlineData("media",400)]
    [InlineData("oversized",413)][InlineData("noBearerOversized",413)][InlineData("noBearer",401)]
    [InlineData("wrongScope",401)][InlineData("deleted",401)][InlineData("unavailable",503)]
    public async Task GivenInvalidEmptyTransportOrIdentity_WhenDeletingTrash_ThenSafeFailureWithoutIntent(string kind,int status)
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);await CreateTrashRecord(s,[]);var before=await Snapshot(s);using var request=Request(s,"/api/trash"+(kind=="query"?"?ownerId=x":""),HttpMethod.Delete,kind is "oversized" or "noBearerOversized"?new byte[65537]:Bytes(new{expectedRevision=1}));
        if(kind is "missingAccess" or "badAccess"){request.Headers.Remove("X-Cerberus-Vault-Access");if(kind=="badAccess")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access","bad");}
        if(kind=="duplicateAccess")request.Headers.TryAddWithoutValidation("X-Cerberus-Vault-Access",s.Access);if(kind=="authority")request.Headers.Add("X-Cerberus-Owner",Guid.NewGuid().ToString());if(kind=="media")request.Content!.Headers.ContentType=new("text/plain");
        if(kind is "noBearer" or "noBearerOversized")request.Headers.Authorization=null;if(kind=="wrongScope")request.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor,Guid.NewGuid()));
        if(kind is "deleted" or "unavailable")fixture.Users[s.Actor+"@example.test"]=new(s.Actor,"fixture-password",Deleted:kind=="deleted",IdentityUnavailable:kind=="unavailable");
        using var response=await Gateway.Client.SendAsync(request);await Failure(response,status);Assert.Equal(before,await Snapshot(s));await using var db=fixture.Context();Assert.False(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId));
    }
    [FunctionalTheory][InlineData("stale",409,"revision_conflict")][InlineData("revoked",403,"vault_access_denied")]
    [InlineData("expiredSession",403,"vault_access_denied")][InlineData("missingAccount",404,"not_found")]
    public async Task GivenChangedEmptyAuthorityOrAccountRevision_WhenDeletingTrash_ThenNoIntent(string kind,int status,string error)
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);await CreateTrashRecord(s,[]);await using var db=fixture.Context();
        if(kind=="revoked")await db.VaultAccessSessions.Where(x=>x.HandleVerifier==Hash(s.Access)).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.Revoked,true));if(kind=="expiredSession")await db.VaultAccessSessions.Where(x=>x.HandleVerifier==Hash(s.Access)).ExecuteUpdateAsync(x=>x.SetProperty(v=>v.ExpiresAt,DateTimeOffset.UtcNow.AddMinutes(-1)));if(kind=="missingAccount")await db.Accounts.Where(x=>x.Id==s.Id).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.HeimdallPublicId,Guid.NewGuid()));
        var before=await Snapshot(s);using var response=await Send(s,"/api/trash",HttpMethod.Delete,Bytes(new{expectedRevision=kind=="stale"?99:1}));await Failure(response,status,error);Assert.Equal(before,await Snapshot(s));
    }
    [FunctionalFact]
    public async Task GivenValidSelectedNativeProfile_WhenEmptyingAllTrash_Then403AndNoBroadening()
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(keys);var id=Guid.NewGuid();using(var created=await Send(s,"/api/profiles",HttpMethod.Post,Bytes(new ProfileCreateInput(id,Envelope(),new("Master",scoped.Material.UnlockVerifier,keys.Wrap(s.AccountId,"profile",id,s.Actor),null),DateTimeOffset.UnixEpoch,[],[],[]))))Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        await CreateTrashRecord(s,[id]);await using var db=fixture.Context();var p=await db.Profiles.AsNoTracking().SingleAsync(x=>x.PublicId==id);var selected=s with{Access=await Open(s,p,scoped)};var before=await Snapshot(s);using var response=await Send(selected,"/api/trash",HttpMethod.Delete,Bytes(new{expectedRevision=1}));await Failure(response,403,"vault_access_denied");Assert.Equal(before,await Snapshot(s));Assert.Equal(p.Id,(await db.VaultAccessSessions.AsNoTracking().SingleAsync(x=>x.HandleVerifier==Hash(selected.Access))).ProfileId);
    }
    [FunctionalFact]
    public async Task GivenExpiredDamagedTrashAndForeignOwnerTrash_WhenEmptyingViaHttp_ThenOnlyOwnTrashPermanentlyGone()
    {
        using var keys=new ProtectionFixture();using var otherKeys=new ProtectionFixture();var s=await Setup(keys);var foreign=await Setup(otherKeys);var id=await CreateTrashRecord(s,[]);await CreateTrashRecord(foreign,[]);var foreignBefore=await Snapshot(foreign);await using var db=fixture.Context();await db.Records.Where(x=>x.PublicId==id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.PurgeAt,DateTimeOffset.UtcNow.AddMinutes(-1)).SetProperty(r=>r.Envelope,"damaged"u8.ToArray()));
        using var response=await Send(s,"/api/trash",HttpMethod.Delete,Bytes(new{expectedRevision=1}));CheckEmptyResult(await Success(response),s.AccountId,1,"record");Assert.Equal(foreignBefore,await Snapshot(foreign));Assert.False(await db.Records.AnyAsync(x=>x.PublicId==id));
    }
    private static void CheckEmptyResult(JsonElement data,Guid owner,long total,string? kind)
    {
        Fields(data,"accountId","purgedAt","counts","totalPurged");Assert.Equal(owner,data.GetProperty("accountId").GetGuid());Assert.Equal(total,data.GetProperty("totalPurged").GetInt64());var at=data.GetProperty("purgedAt").GetDateTimeOffset();Assert.Equal(TimeSpan.Zero,at.Offset);Assert.Equal(0,at.Ticks%TimeSpan.TicksPerMicrosecond);
        var counts=data.GetProperty("counts").EnumerateArray().ToArray();Assert.Equal(new[]{"collection","folder","profile","record"},counts.Select(x=>x.GetProperty("kind").GetString()).Order());foreach(var c in counts){Fields(c,"kind","count");Assert.Equal(c.GetProperty("kind").GetString()==kind?total:0,c.GetProperty("count").GetInt64());}
    }
}
