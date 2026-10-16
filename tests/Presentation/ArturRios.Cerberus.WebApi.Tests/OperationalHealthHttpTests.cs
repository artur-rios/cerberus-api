using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using ArturRios.Cerberus.Domain.Accounts;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.WebApi.Tests;

[Collection("Operational health host")]
public sealed class OperationalHealthHttpTests
{
    private readonly OperationalHealthApiFixture _fixture;
    public OperationalHealthHttpTests(OperationalHealthApiFixture fixture){_fixture=fixture;fixture.Reset();}
    [FunctionalTheory][InlineData("")][InlineData("?details=true")][InlineData("?operator=true&component=database")]
    public async Task GivenAnonymousOrForgedOptIn_WhenCheckingLiveness_ThenOnlyProcessStatusAndNoDependencyCalls(string query)
    {
        _fixture.Mode="all503";await using var host=await _fixture.StartAsync();host.Client.DefaultRequestHeaders.TryAddWithoutValidation("Authorization","Bearer not-a-token");host.Client.DefaultRequestHeaders.TryAddWithoutValidation("X-Health-Details","true");
        using var response=await host.Client.GetAsync("/health/live"+query);await Minimal(response,HttpStatusCode.OK,"healthy","live");Assert.Empty(_fixture.Calls);
    }
    [FunctionalTheory][InlineData("",HttpStatusCode.OK,"healthy")][InlineData("all503",HttpStatusCode.ServiceUnavailable,"unhealthy")][InlineData("lost-owner",HttpStatusCode.ServiceUnavailable,"unhealthy")][InlineData("malformed",HttpStatusCode.ServiceUnavailable,"unhealthy")]
    public async Task GivenConfiguredDependencyState_WhenCheckingReadiness_ThenMinimalResultAndOnlyNonUserGets(string mode,HttpStatusCode status,string expected)
    {
        var id=await Canary();_fixture.Mode=mode;await using var host=await _fixture.StartAsync();host.Client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",_fixture.Token("person"));
        using var response=await host.Client.GetAsync("/health/ready?details=true");await Minimal(response,status,expected,"ready-"+(mode==""?"healthy":mode));
        Assert.NotEmpty(_fixture.Calls);Assert.All(_fixture.Calls,x=>{Assert.Equal("GET",x.Method);Assert.Equal("Bearer "+_fixture.ServiceToken,x.Bearer);Assert.True(x.Path.StartsWith("/api/scopes/",StringComparison.Ordinal)||x.Path.StartsWith("/api/persons/",StringComparison.Ordinal));});await Unchanged(id);
    }
    [FunctionalTheory][InlineData("person")][InlineData("operator")][InlineData("application")]
    public async Task GivenSeparateOperationalGrant_WhenCheckingDetails_ThenRedactedComponentsWithActualCallerAuthority(string actor)
    {
        var id=await Canary();await using var host=await _fixture.StartAsync();var token=_fixture.Token(actor);using var request=new HttpRequestMessage(HttpMethod.Get,"/health/details");request.Headers.Authorization=new("Bearer",token);
        using var response=await host.Client.SendAsync(request);await Details(response,HttpStatusCode.OK,"healthy","details-"+actor);var caller=_fixture.Calls.Where(x=>x.Bearer=="Bearer "+token).ToArray();
        Assert.Equal(actor=="operator"?2:1,caller.Length);Assert.All(caller,x=>Assert.Equal("GET",x.Method));Assert.All(_fixture.Calls,x=>Assert.Equal("GET",x.Method));
        Assert.Contains(caller,x=>x.Path==(actor=="application"?"/api/applications/self":"/api/persons/"+(actor=="operator"?OperationalHealthApiFixture.Operator:OperationalHealthApiFixture.Person)));await Unchanged(id);
    }
    [FunctionalTheory][InlineData("person")][InlineData("operator")][InlineData("application")][InlineData("configured-service")]
    public async Task GivenOrdinaryIdentityWithoutSeparateGrant_WhenCheckingDetails_Then403BeforeProbes(string actor)
    {
        await using var host=await _fixture.StartAsync(grants:false);host.Client.DefaultRequestHeaders.Authorization=new("Bearer",actor=="configured-service"?_fixture.ServiceToken:_fixture.Token(actor));
        using var response=await host.Client.GetAsync("/health/details");await Error(response,HttpStatusCode.Forbidden,"operational_access_required","details-forbidden");Assert.Empty(_fixture.Calls);
    }
    [FunctionalTheory][InlineData("missing")][InlineData("invalid")][InlineData("foreign-person")][InlineData("foreign-app")][InlineData("duplicate-header")]
    public async Task GivenUnauthenticatedDetailsRequest_WhenChecking_Then401WithoutComponents(string actor)
    {
        await using var host=await _fixture.StartAsync();using var request=new HttpRequestMessage(HttpMethod.Get,"/health/details?details=true");
        if(actor!="missing")request.Headers.TryAddWithoutValidation("Authorization",actor=="duplicate-header"?new[]{"Bearer "+_fixture.Token("person"),"Bearer "+_fixture.Token("person")}:new[]{"Bearer "+(actor=="invalid"?"not-a-token":_fixture.Token(actor=="foreign-app"?"application":"person",true))});
        using var response=await host.Client.SendAsync(request);await Error(response,HttpStatusCode.Unauthorized,"authentication_required","details-unauthenticated");Assert.Empty(_fixture.Calls);
    }
    [FunctionalTheory][InlineData("person","deleted-person",HttpStatusCode.Unauthorized,"authentication_required")][InlineData("operator","lost-owner",HttpStatusCode.Unauthorized,"authentication_required")]
    [InlineData("application","revoke-app",HttpStatusCode.Unauthorized,"authentication_required")][InlineData("person","caller503",HttpStatusCode.ServiceUnavailable,"identity_unavailable")]
    public async Task GivenGrantedCallerLosesLiveAuthority_WhenCheckingDetails_ThenFailWithoutComponents(string actor,string mode,HttpStatusCode status,string code)
    {
        await using var host=await _fixture.StartAsync();_fixture.Mode=mode;host.Client.DefaultRequestHeaders.Authorization=new("Bearer",_fixture.Token(actor));using var response=await host.Client.GetAsync("/health/details");await Error(response,status,code,"details-error-"+(int)status);
        Assert.All(_fixture.Calls,x=>Assert.Equal("GET",x.Method));Assert.DoesNotContain(_fixture.Calls,x=>x.Bearer=="Bearer "+_fixture.ServiceToken);
    }
    [FunctionalFact]
    public async Task GivenRestoreBarrierClosed_WhenCheckingHealth_ThenPublicProbesReachableDetailsStillAuthorizedAndVaultBlocked()
    {
        await using var host=await _fixture.StartAsync(restore:true);using(var live=await host.Client.GetAsync("/health/live"))await Minimal(live,HttpStatusCode.OK,"healthy","live-restore");
        using(var ready=await host.Client.GetAsync("/health/ready"))await Minimal(ready,HttpStatusCode.ServiceUnavailable,"unhealthy","ready-restore");
        using(var denied=await host.Client.GetAsync("/health/details"))await Error(denied,HttpStatusCode.Unauthorized,"authentication_required","details-restore-denied");
        host.Client.DefaultRequestHeaders.Authorization=new("Bearer",_fixture.Token("operator"));using(var details=await host.Client.GetAsync("/health/details")){var body=await Details(details,HttpStatusCode.ServiceUnavailable,"unhealthy","details-restore");Assert.Equal("unhealthy",body.GetProperty("components")[2].GetProperty("status").GetString());}
        using var vault=await host.Client.GetAsync("/api/accounts/me");Assert.Equal(HttpStatusCode.ServiceUnavailable,vault.StatusCode);Assert.Contains("restore_reconciliation_required",await vault.Content.ReadAsStringAsync());
    }
    [FunctionalFact]
    public async Task GivenRestoreClosesDuringActualProviderWait_WhenCheckingReadiness_ThenFinalGateIsUnhealthy()
    {
        await using var host=await _fixture.StartAsync(restore:true);Assert.True(await host.Reconcile(true));_fixture.Mode="pause-scope";var pending=host.Client.GetAsync("/health/ready");await _fixture.Entered.Task.WaitAsync(TimeSpan.FromSeconds(3));Assert.False(await host.Reconcile(false));_fixture.Resume.SetResult();using var response=await pending;await Minimal(response,HttpStatusCode.ServiceUnavailable,"unhealthy","ready-race");
    }
    [FunctionalFact]
    public async Task GivenActualDatabaseUnavailable_WhenChecking_ThenLiveHealthyAndReadyDetailsUnhealthy()
    {
        await using var host=await _fixture.StartAsync();await _fixture.StopDatabaseAsync();
        try
        {
            using(var live=await host.Client.GetAsync("/health/live"))await Minimal(live,HttpStatusCode.OK,"healthy","live-db-down");Assert.Empty(_fixture.Calls);
            using(var ready=await host.Client.GetAsync("/health/ready"))await Minimal(ready,HttpStatusCode.ServiceUnavailable,"unhealthy","ready-db-down");
            host.Client.DefaultRequestHeaders.Authorization=new("Bearer",_fixture.Token("person"));using var details=await host.Client.GetAsync("/health/details");var body=await Details(details,HttpStatusCode.ServiceUnavailable,"unhealthy","details-db-down");Assert.Equal("unhealthy",body.GetProperty("components")[0].GetProperty("status").GetString());
        }
        finally{await _fixture.StartDatabaseAsync();}
        await using var recovered=await _fixture.StartAsync();using var healthy=await recovered.Client.GetAsync("/health/ready");await Minimal(healthy,HttpStatusCode.OK,"healthy","ready-recovered");
    }
    [FunctionalFact]
    public async Task GivenMissingAccountSchema_WhenCheckingReadiness_Then503WithNoDatabaseText()
    {
        await using var setup=_fixture.Context();await setup.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.account RENAME TO fixture_health_account");
        try{await using var host=await _fixture.StartAsync();using var response=await host.Client.GetAsync("/health/ready");await Minimal(response,HttpStatusCode.ServiceUnavailable,"unhealthy","ready-schema");}
        finally{await setup.Database.ExecuteSqlRawAsync("ALTER TABLE cerberus.fixture_health_account RENAME TO account");}
    }
    [FunctionalFact]
    public async Task GivenConnectableDatabaseRoleWithoutSelect_WhenCheckingReadiness_Then503()
    {
        await using var setup=_fixture.Context();var name="health_http_"+Guid.NewGuid().ToString("N");await Sql(setup,$"CREATE ROLE {name} LOGIN PASSWORD 'fixture-health-only'; GRANT USAGE ON SCHEMA cerberus TO {name};");
        try{var cs=new NpgsqlConnectionStringBuilder(setup.Database.GetConnectionString()){Username=name,Password="fixture-health-only",Pooling=false};await using var host=await _fixture.StartAsync(connection:cs.ConnectionString);using var response=await host.Client.GetAsync("/health/ready");await Minimal(response,HttpStatusCode.ServiceUnavailable,"unhealthy","ready-select-denied");}
        finally{await Sql(setup,$"DROP OWNED BY {name}; DROP ROLE {name};");}
    }
    [FunctionalFact]
    public async Task GivenStalledActualProviderBody_WhenCheckingReadiness_ThenBoundProbeAndCloseCallerTransport()
    {
        _fixture.Mode="stall-scope";await using var host=await _fixture.StartAsync();using var response=await host.Client.GetAsync("/health/ready");await Minimal(response,HttpStatusCode.ServiceUnavailable,"unhealthy","ready-timeout");await _fixture.Entered.Task;await _fixture.Released.Task.WaitAsync(TimeSpan.FromSeconds(3));
    }
    [FunctionalFact]
    public async Task GivenHttpDisconnectDuringActualDatabaseLockWait_WhenCheckingReadiness_ThenOwningBackendIsReleased()
    {
        await using var setup=_fixture.Context();var name="health_http_wait_"+Guid.NewGuid().ToString("N");var cs=new NpgsqlConnectionStringBuilder(setup.Database.GetConnectionString()){Pooling=false,ApplicationName=name};await using var host=await _fixture.StartAsync(connection:cs.ConnectionString);
        await using var transaction=await setup.Database.BeginTransactionAsync();await setup.Database.ExecuteSqlRawAsync("LOCK TABLE cerberus.account IN ACCESS EXCLUSIVE MODE");using var canceled=new CancellationTokenSource();var pending=host.Client.GetAsync("/health/ready",canceled.Token);
        await WaitFor(name,true);canceled.Cancel();await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>pending);await WaitFor(name,false);await transaction.RollbackAsync();
    }
    [FunctionalFact]
    public async Task GivenConditionalRequest_WhenCheckingHealth_ThenStillFreshNoStoreWithNoValidator()
    {
        await using var host=await _fixture.StartAsync();host.Client.DefaultRequestHeaders.TryAddWithoutValidation("If-None-Match","*");host.Client.DefaultRequestHeaders.IfModifiedSince=DateTimeOffset.UtcNow.AddDays(1);
        using var response=await host.Client.GetAsync("/health/live");await Minimal(response,HttpStatusCode.OK,"healthy","live-conditional");Assert.Null(response.Headers.ETag);Assert.Null(response.Content.Headers.LastModified);
    }
    private async Task<Guid> Canary()
    {var id=Guid.NewGuid();await using var c=_fixture.Context();c.Accounts.Add(new Account{PublicId=id,HeimdallPublicId=Guid.NewGuid(),Revision=7,DetailsEnvelope=[13,14,15]});await c.SaveChangesAsync();return id;}
    private async Task Unchanged(Guid id){await using var c=_fixture.Context();var a=await c.Accounts.AsNoTracking().SingleAsync(x=>x.PublicId==id);Assert.Equal(7,a.Revision);Assert.Equal(new byte[]{13,14,15},a.DetailsEnvelope);}
    private static async Task<string> Capture(HttpResponseMessage response,string sample)
    {
        Assert.True(response.Headers.CacheControl?.NoStore);Assert.Equal("application/json",response.Content.Headers.ContentType!.MediaType);var body=await response.Content.ReadAsStringAsync();var folder=Path.Combine(AppContext.BaseDirectory,"operational-health-contract-fixtures");Directory.CreateDirectory(folder);await File.WriteAllTextAsync(Path.Combine(folder,sample+".json"),body);await File.WriteAllTextAsync(Path.Combine(folder,sample+".http.json"),JsonSerializer.Serialize(new{status=(int)response.StatusCode,noStore=response.Headers.CacheControl?.NoStore==true,contentType=response.Content.Headers.ContentType!.MediaType}));Assert.DoesNotContain("private-",body);Assert.DoesNotContain("127.0.0.1",body);Assert.DoesNotContain("Password",body);return body;
    }
    private static async Task Minimal(HttpResponseMessage response,HttpStatusCode status,string expected,string sample)
    {var raw=await Capture(response,sample);Assert.Equal(status,response.StatusCode);using var doc=JsonDocument.Parse(raw);Assert.Equal(new[]{"status"},doc.RootElement.EnumerateObject().Select(x=>x.Name));Assert.Equal(expected,doc.RootElement.GetProperty("status").GetString());}
    private static async Task<JsonElement> Details(HttpResponseMessage response,HttpStatusCode status,string expected,string sample)
    {
        using var doc=JsonDocument.Parse(await Capture(response,sample));Assert.Equal(status,response.StatusCode);var body=doc.RootElement;Assert.Equal(new[]{"components","status"},body.EnumerateObject().Select(x=>x.Name).Order());Assert.Equal(expected,body.GetProperty("status").GetString());var components=body.GetProperty("components").EnumerateArray().ToArray();Assert.Equal(new[]{"database","identity","restore"},components.Select(x=>x.GetProperty("name").GetString()));foreach(var x in components){Assert.Equal(new[]{"elapsedMilliseconds","name","status"},x.EnumerateObject().Select(p=>p.Name).Order());Assert.Contains(x.GetProperty("status").GetString(),new[]{"healthy","unhealthy"});Assert.True(x.GetProperty("elapsedMilliseconds").GetInt64()>=0);}return body.Clone();
    }
    private static async Task Error(HttpResponseMessage response,HttpStatusCode status,string code,string sample)
    {Assert.Equal(status,response.StatusCode);using var doc=JsonDocument.Parse(await Capture(response,sample));Assert.False(doc.RootElement.GetProperty("success").GetBoolean());Assert.Equal(code,Assert.Single(doc.RootElement.GetProperty("errors").EnumerateArray()).GetString());Assert.False(doc.RootElement.TryGetProperty("components",out _));Assert.False(doc.RootElement.TryGetProperty("data",out _));}
    private async Task WaitFor(string name,bool waiting)
    {for(var i=0;i<150;i++){await using var c=_fixture.Context();await c.Database.OpenConnectionAsync();await using var command=c.Database.GetDbConnection().CreateCommand();command.CommandText=waiting?"SELECT count(*) FROM pg_stat_activity WHERE application_name=@name AND wait_event_type='Lock'":"SELECT count(*) FROM pg_stat_activity WHERE application_name=@name";var p=command.CreateParameter();p.ParameterName="name";p.Value=name;command.Parameters.Add(p);var count=Convert.ToInt64(await command.ExecuteScalarAsync());if(waiting?count==1:count==0)return;await Task.Delay(20);}throw new TimeoutException("Expected actual readiness database wait/cleanup boundary");}
    private static async Task Sql(ArturRios.Cerberus.Data.AppDbContext c,string text){await c.Database.OpenConnectionAsync();await using var command=c.Database.GetDbConnection().CreateCommand();command.CommandText=text;await command.ExecuteNonQueryAsync();}
}
