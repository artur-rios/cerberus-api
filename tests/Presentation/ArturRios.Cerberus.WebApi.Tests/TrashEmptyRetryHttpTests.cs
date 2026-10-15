using System.Net;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.TestSupport;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
namespace ArturRios.Cerberus.WebApi.Tests;

public partial class TrashRestoreHttpTests
{
    [FunctionalFact]
    public async Task GivenLedgerOutageAfterBulkIntent_WhenEmptyingViaHttp_Then503HidesRestoreAndSafeRetryPurges()
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);var id=await CreateTrashRecord(s,[]);await using var db=fixture.Context();var op=await db.TrashOperations.AsNoTracking().SingleAsync(x=>x.RootResourceId==id&&x.AccountId==s.Id);
        using(var host=new WebApplicationFactory<Program>().WithWebHostBuilder(b=>b.ConfigureServices(services=>services.Replace(ServiceDescriptor.Singleton<IErasureLedger>(new EmptyUnavailableLedger())))))
        {using var client=host.CreateClient();using var request=Request(s,"/api/trash",HttpMethod.Delete,Bytes(new{expectedRevision=1}));using var response=await client.SendAsync(request);await Failure(response,503,"persistence_unavailable");}
        Assert.True(await db.Records.AnyAsync(x=>x.PublicId==id));Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="record"&&x.ResourceId==id));var work=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId);Assert.Null(work.CompletedAt);
        using(var list=await Send(s))Assert.Empty((await Success(list)).GetProperty("items").EnumerateArray());using(var restore=await Send(s,"/api/trash/"+op.PublicId+"/restore",HttpMethod.Post,Bytes(new{expectedRevision=2})))await Failure(restore,404,"not_found");using(var busy=await Send(s,"/api/trash",HttpMethod.Delete,Bytes(new{expectedRevision=1})))await Failure(busy,503,"persistence_unavailable");
        await db.RetentionWorkItems.Where(x=>x.Id==work.Id).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));using(var retry=await Send(s,"/api/trash",HttpMethod.Delete,Bytes(new{expectedRevision=1})))CheckEmptyResult(await Success(retry),s.AccountId,1,"record");Assert.False(await db.Records.AnyAsync(x=>x.PublicId==id));Assert.NotNull((await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.Id==work.Id)).CompletedAt);
    }
    [FunctionalFact]
    public async Task GivenMixedNativeFourKindTrashAndActiveRecord_WhenEmptyingViaHttp_ThenAllFourKindsGoneAndActiveCipherExact()
    {
        using var keys=new ProtectionFixture();using var scoped=new ProtectionFixture();var s=await Setup(keys);var active=Guid.NewGuid();using(var r=await Send(s,"/api/records",HttpMethod.Post,Bytes(new{recordId=active,envelope=Envelope(),editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),folderId=(Guid?)null})))Assert.Equal(HttpStatusCode.Created,r.StatusCode);
        string before;using(var read=await Send(s,"/api/records/"+active))before=(await Success(read)).GetRawText();await CreateTrashRecord(s,[]);
        foreach(var kind in new[]{"folder","collection","profile"}){
            var id=Guid.NewGuid();var env=Envelope();object input=kind switch{
                "folder"=>new{folderId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),parentFolderId=(Guid?)null},
                "collection"=>new{collectionId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),recordIds=Array.Empty<Guid>(),folderIds=Array.Empty<Guid>()},
                _=>new ProfileCreateInput(id,env,new("Master",scoped.Material.UnlockVerifier,keys.Wrap(s.AccountId,"profile",id,s.Actor),null),DateTimeOffset.UnixEpoch,[],[],[])};
            var route="/api/"+(kind=="collection"?"collections":kind=="profile"?"profiles":"folders");using(var created=await Send(s,route,HttpMethod.Post,Bytes(input)))Assert.Equal(HttpStatusCode.Created,created.StatusCode);using(var trashed=await Send(s,route+"/"+id,HttpMethod.Delete,Bytes(new{expectedRevision=1})))await Success(trashed);
        }
        using var response=await Send(s,"/api/trash",HttpMethod.Delete,Bytes(new{expectedRevision=1}));var data=await Success(response);Fields(data,"accountId","purgedAt","counts","totalPurged");Assert.Equal(4,data.GetProperty("totalPurged").GetInt64());Assert.Equal(4,data.GetProperty("counts").GetArrayLength());Assert.All(data.GetProperty("counts").EnumerateArray(),c=>Assert.Equal(1,c.GetProperty("count").GetInt64()));
        using(var read=await Send(s,"/api/records/"+active))Assert.Equal(before,(await Success(read)).GetRawText());await using var db=fixture.Context();Assert.Equal(1,await db.Records.CountAsync(x=>x.AccountId==s.Id));Assert.False(await db.TrashOperations.AnyAsync(x=>x.AccountId==s.Id));Assert.False(await db.Folders.AnyAsync(x=>x.AccountId==s.Id));Assert.False(await db.Collections.AnyAsync(x=>x.AccountId==s.Id));Assert.False(await db.Profiles.AnyAsync(x=>x.AccountId==s.Id));
        if(Environment.GetEnvironmentVariable("CERBERUS_CAPTURE_UC52_JSON")=="1")await File.WriteAllTextAsync("/tmp/cerberus-uc52-http-mixed.json",data.GetRawText());
    }
    [FunctionalFact]
    public async Task GivenPresentEmptyRawVaultHeader_WhenEmptyingTrash_Then400WithoutIntent()
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);await CreateTrashRecord(s,[]);var before=await Snapshot(s);using var host=new WebApplicationFactory<Program>();var body=Bytes(new{expectedRevision=1});
        var context=await host.Server.SendAsync(c=>{c.Request.Method="DELETE";c.Request.Path="/api/trash";c.Request.ContentType="application/json";c.Request.ContentLength=body.Length;c.Request.Body=new MemoryStream(body);c.Request.Headers.Authorization="Bearer "+RegistrationApiFixture.Token(s.Actor);c.Request.Headers["X-Cerberus-Vault-Access"]="";});
        Assert.Equal(400,context.Response.StatusCode);Assert.Contains("no-store",context.Response.Headers.CacheControl.ToString());Assert.Equal(before,await Snapshot(s));await using var db=fixture.Context();Assert.False(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="trash-erase/"+s.AccountId));
    }
    private sealed class EmptyUnavailableLedger:IErasureLedger
    {
        public Task RecordAsync(ErasureEntry entry,CancellationToken ct)=>throw new IOException("fixture bulk erasure ledger outage");
        public async IAsyncEnumerable<ErasureEntry> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct){ct.ThrowIfCancellationRequested();await Task.CompletedTask;yield break;}
    }
}
