using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using System.Net;
using ArturRios.Cerberus.Domain.Operations;
using ArturRios.Cerberus.Domain.Profiles;
using ArturRios.Cerberus.TestSupport;
using ArturRios.Cerberus.WebApi.Operations;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace ArturRios.Cerberus.WebApi.Tests;

public partial class TrashRestoreHttpTests
{
    [FunctionalTheory][InlineData("record")][InlineData("folder")][InlineData("collection")][InlineData("profile")]
    public async Task GivenNativeHttpDeletionPastOriginalDeadline_WhenHostedWorkerExpires_ThenPurgeAndPublishOnlySafeCounts(string kind)
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);var root=await ExpiryHttpRoot(s,keys,kind);await ExpireHttpRoot(root);
        using(var before=await Send(s,"/api/trash/"+root.Operation+"/restore",HttpMethod.Post,Bytes(new{expectedRevision=2})))await Failure(before,410,"trash_expired");
        var measurements=new ConcurrentBag<(string Name,long Value,int Tags)>();using var listener=new MeterListener();listener.InstrumentPublished=(instrument,current)=>{if(instrument.Meter.Name=="ArturRios.Cerberus.Retention")current.EnableMeasurementEvents(instrument);};listener.SetMeasurementEventCallback<long>((instrument,value,tags,_)=>measurements.Add((instrument.Name,value,tags.Length)));listener.Start();
        using var host=new WebApplicationFactory<Program>();using var client=host.CreateClient();var calls=fixture.IdentityCalls;await HostedRetention(host).PollOnceAsync(default);Assert.Equal(calls,fixture.IdentityCalls);
        await using var db=fixture.Context();Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind==kind&&x.ResourceId==root.Id));Assert.False(await ExpiryHttpExists(root));Assert.False(await db.TrashOperations.AnyAsync(x=>x.PublicId==root.Operation));Assert.True(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="trash/"+root.Operation&&x.CompletedAt!=null));
        using(var read=await Send(s,root.Route+"/"+root.Id))await Failure(read,404,"not_found");using(var restore=await Send(s,"/api/trash/"+root.Operation+"/restore",HttpMethod.Post,Bytes(new{expectedRevision=2})))await Failure(restore,404,"not_found");using(var list=await Send(s)){var data=await Success(list);Assert.Empty(data.GetProperty("items").EnumerateArray());if(Environment.GetEnvironmentVariable("CERBERUS_CAPTURE_UC53_JSON")=="1")await File.WriteAllTextAsync("/tmp/cerberus-uc53-http-"+kind+".json",data.GetRawText());}
        Assert.Contains(measurements,x=>x.Name=="cerberus.retention.completed"&&x.Value>=1);Assert.All(measurements,x=>Assert.Equal(0,x.Tags));
        if(Environment.GetEnvironmentVariable("CERBERUS_CAPTURE_UC53_JSON")=="1")await File.WriteAllTextAsync("/tmp/cerberus-uc53-metrics-"+kind+".json",System.Text.Json.JsonSerializer.Serialize(measurements.Select(x=>new{name=x.Name,value=x.Value,tagCount=x.Tags}).ToArray()));
    }
    [FunctionalFact]
    public async Task GivenWorkerLedgerOutage_WhenReadingAndRetryingExpiry_ThenNeverRestoreExpiredContentAndFinishOriginalJob()
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);var root=await ExpiryHttpRoot(s,keys,"record");await ExpireHttpRoot(root);
        using(var unavailable=new WebApplicationFactory<Program>().WithWebHostBuilder(b=>b.ConfigureServices(services=>services.Replace(ServiceDescriptor.Singleton<IErasureLedger>(new ExpiryUnavailableLedger())))))
        {using var client=unavailable.CreateClient();await HostedRetention(unavailable).PollOnceAsync(default);}
        await using var db=fixture.Context();Assert.True(await ExpiryHttpExists(root));Assert.True(await db.TerminalErasures.AnyAsync(x=>x.ResourceKind=="record"&&x.ResourceId==root.Id));var job=await db.RetentionWorkItems.AsNoTracking().SingleAsync(x=>x.OperationKey=="trash/"+root.Operation);Assert.Null(job.CompletedAt);
        using(var restore=await Send(s,"/api/trash/"+root.Operation+"/restore",HttpMethod.Post,Bytes(new{expectedRevision=2})))await Failure(restore,404,"not_found");using(var list=await Send(s)){var data=await Success(list);Assert.Empty(data.GetProperty("items").EnumerateArray());if(Environment.GetEnvironmentVariable("CERBERUS_CAPTURE_UC53_JSON")=="1")await File.WriteAllTextAsync("/tmp/cerberus-uc53-http-outage.json",data.GetRawText());}
        await db.RetentionWorkItems.Where(x=>x.PublicId==job.PublicId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.ClaimExpiresAt,DateTimeOffset.UtcNow.AddSeconds(-1)));
        using var healthy=new WebApplicationFactory<Program>();using var healthyClient=healthy.CreateClient();await HostedRetention(healthy).PollOnceAsync(default);Assert.False(await ExpiryHttpExists(root));Assert.True(await db.RetentionWorkItems.AnyAsync(x=>x.PublicId==job.PublicId&&x.CompletedAt!=null));
    }
    [FunctionalFact]
    public async Task GivenUncancelledHttpClosurePastOriginalDeadline_WhenHostedWorkerExpires_ThenRemoveCerberusDataAndKeepIdentity()
    {
        using var keys=new ProtectionFixture();var s=await Setup(keys);var id=Guid.NewGuid();using(var created=await Send(s,"/api/records",HttpMethod.Post,Bytes(new{recordId=id,envelope=Envelope(),editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),folderId=(Guid?)null})))Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        using(var request=Request(s,"/api/accounts/me/closure",HttpMethod.Post,Bytes(new{expectedRevision=1}))){request.Headers.Remove("X-Cerberus-Vault-Access");using var closed=await Gateway.Client.SendAsync(request);await Success(closed);}
        await using var db=fixture.Context();var deadline=new DateTimeOffset(2020,1,31,0,0,0,TimeSpan.Zero);await db.Accounts.Where(x=>x.Id==s.Id).ExecuteUpdateAsync(x=>x.SetProperty(a=>a.ClosureRequestedAt,deadline.AddDays(-30)).SetProperty(a=>a.ClosurePurgeAt,deadline));await db.RetentionWorkItems.Where(x=>x.OperationKey=="account-purge/"+s.AccountId).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.DueAt,deadline));
        using var host=new WebApplicationFactory<Program>();using var client=host.CreateClient();var calls=fixture.IdentityCalls;var updates=fixture.IdentityUpdateCalls;await HostedRetention(host).PollOnceAsync(default);Assert.Equal(calls,fixture.IdentityCalls);Assert.Equal(updates,fixture.IdentityUpdateCalls);
        Assert.False(await db.Accounts.AnyAsync(x=>x.Id==s.Id));Assert.False(await db.Records.AnyAsync(x=>x.PublicId==id));Assert.True(await db.RetentionWorkItems.AnyAsync(x=>x.OperationKey=="account-purge/"+s.AccountId&&x.CompletedAt!=null));Assert.Contains(fixture.Users.Values,x=>x.Id==s.Actor&&!x.Deleted);
        using var read=new HttpRequestMessage(HttpMethod.Get,"/api/accounts/me");read.Headers.Authorization=new("Bearer",RegistrationApiFixture.Token(s.Actor));read.Headers.Add("X-Cerberus-Vault-Access",s.Access);using var response=await Gateway.Client.SendAsync(read);await Failure(response,404,"not_found");
        if(Environment.GetEnvironmentVariable("CERBERUS_CAPTURE_UC53_JSON")=="1")await File.WriteAllTextAsync("/tmp/cerberus-uc53-http-account-gone.json",await response.Content.ReadAsStringAsync());
    }
    private sealed record ExpiryHttpResource(string Kind,Guid Id,Guid Operation,string Route);
    private async Task<ExpiryHttpResource> ExpiryHttpRoot(State s,ProtectionFixture keys,string kind)
    {
        using var scoped=new ProtectionFixture();var id=Guid.NewGuid();var env=Envelope();object input=kind switch{
            "record"=>new{recordId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),folderId=(Guid?)null},
            "folder"=>new{folderId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),parentFolderId=(Guid?)null},
            "collection"=>new{collectionId=id,envelope=env,editedAt=DateTimeOffset.UnixEpoch,profileIds=Array.Empty<Guid>(),recordIds=Array.Empty<Guid>(),folderIds=Array.Empty<Guid>()},
            _=>new ProfileCreateInput(id,env,new("Master",scoped.Material.UnlockVerifier,keys.Wrap(s.AccountId,"profile",id,s.Actor),null),DateTimeOffset.UnixEpoch,[],[],[])};
        var route="/api/"+(kind=="collection"?"collections":kind=="profile"?"profiles":kind+"s");using(var created=await Send(s,route,HttpMethod.Post,Bytes(input)))Assert.Equal(HttpStatusCode.Created,created.StatusCode);
        using var deleted=await Send(s,route+"/"+id,HttpMethod.Delete,Bytes(new{expectedRevision=1}));var data=await Success(deleted);return new(kind,id,data.GetProperty("trashOperationId").GetGuid(),route);
    }
    private async Task ExpireHttpRoot(ExpiryHttpResource root)
    {
        await using var db=fixture.Context();var deadline=new DateTimeOffset(2020,1,31,0,0,0,TimeSpan.Zero);var start=deadline.AddDays(-30);
        await db.TrashOperations.Where(x=>x.PublicId==root.Operation).ExecuteUpdateAsync(x=>x.SetProperty(o=>o.DeletedAt,start).SetProperty(o=>o.PurgeAt,deadline));await db.RetentionWorkItems.Where(x=>x.OperationKey=="trash/"+root.Operation).ExecuteUpdateAsync(x=>x.SetProperty(w=>w.DueAt,deadline));
        switch(root.Kind){case "record":await db.Records.Where(x=>x.PublicId==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,start).SetProperty(r=>r.PurgeAt,deadline));break;case "folder":await db.Folders.Where(x=>x.PublicId==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,start).SetProperty(r=>r.PurgeAt,deadline));break;case "collection":await db.Collections.Where(x=>x.PublicId==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,start).SetProperty(r=>r.PurgeAt,deadline));break;default:await db.Profiles.Where(x=>x.PublicId==root.Id).ExecuteUpdateAsync(x=>x.SetProperty(r=>r.DeletedAt,start).SetProperty(r=>r.PurgeAt,deadline));break;}
    }
    private async Task<bool> ExpiryHttpExists(ExpiryHttpResource root)
    {await using var db=fixture.Context();return root.Kind switch{"record"=>await db.Records.AnyAsync(x=>x.PublicId==root.Id),"folder"=>await db.Folders.AnyAsync(x=>x.PublicId==root.Id),"collection"=>await db.Collections.AnyAsync(x=>x.PublicId==root.Id),_=>await db.Profiles.AnyAsync(x=>x.PublicId==root.Id)};}
    private static RetentionWorker HostedRetention(WebApplicationFactory<Program> host)=>host.Services.GetServices<IHostedService>().OfType<RetentionWorker>().Single();
    private sealed class ExpiryUnavailableLedger:IErasureLedger
    {
        public Task RecordAsync(ErasureEntry entry,CancellationToken ct)=>throw new IOException("protected fixture expiry ledger outage");
        public async IAsyncEnumerable<ErasureEntry> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct){ct.ThrowIfCancellationRequested();await Task.CompletedTask;yield break;}
    }
}
