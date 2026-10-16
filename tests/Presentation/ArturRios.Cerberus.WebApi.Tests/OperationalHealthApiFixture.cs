using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.Shared.Operations;
using ArturRios.Cerberus.Data;
using ArturRios.Cerberus.Domain.Operations;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Moq;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace ArturRios.Cerberus.WebApi.Tests;

public sealed class OperationalHealthApiFixture:IAsyncLifetime
{
    public static readonly Guid Person=Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");
    public static readonly Guid Operator=Guid.Parse("527a1001-8ef5-4c9b-a565-444444444444");
    public static readonly Guid Application=Guid.Parse("527a1001-8ef5-4c9b-a565-666666666666");
    public static readonly Guid Credential=Guid.Parse("527a1001-8ef5-4c9b-a565-777777777777");
    private const string Secret="fixture-only-signing-key-32-characters";
    private readonly RegistrationApiFixture _base=new();private WebApplication _provider=null!;private string _oldProvider=null!;private Guid _service;
    public string Mode="";
    public string ServiceToken=>Environment.GetEnvironmentVariable("CERBERUS_HEIMDALL_SERVICE_CREDENTIAL")!;
    public ConcurrentQueue<(string Method,string Path,string Bearer)> Calls{get;}=new();
    public TaskCompletionSource Entered=new(TaskCreationOptions.RunContinuationsAsynchronously),Released=new(TaskCreationOptions.RunContinuationsAsynchronously),Resume=new(TaskCreationOptions.RunContinuationsAsynchronously);
    public AppDbContext Context()=>_base.Context();public Task StopDatabaseAsync()=>_base.StopDatabaseAsync();public async Task StartDatabaseAsync()
    {
        await _base.StartDatabaseAsync();await using var context=Context();
        var configured=new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable("CERBERUS_DATA_CONNECTIONSTRING"));var actual=new NpgsqlConnectionStringBuilder(context.Database.GetConnectionString());
        await File.WriteAllTextAsync(Path.Combine(AppContext.BaseDirectory,"operational-health-restart-diagnostic.json"),System.Text.Json.JsonSerializer.Serialize(new{configuredPort=configured.Port,actualPort=actual.Port,configurationMatches=configured.ConnectionString==actual.ConnectionString}));
        Environment.SetEnvironmentVariable("CERBERUS_DATA_CONNECTIONSTRING",actual.ConnectionString);
    }
    public void Reset(){Mode="";Calls.Clear();Entered=new(TaskCreationOptions.RunContinuationsAsynchronously);Released=new(TaskCreationOptions.RunContinuationsAsynchronously);Resume=new(TaskCreationOptions.RunContinuationsAsynchronously);}
    public async Task InitializeAsync()
    {
        await _base.InitializeAsync();_oldProvider=Environment.GetEnvironmentVariable("CERBERUS_HEIMDALL_BASE_URL")!;_service=Guid.Parse(new JwtSecurityTokenHandler().ReadJwtToken(ServiceToken).Claims.Single(x=>x.Type=="id").Value);
        var builder=WebApplication.CreateSlimBuilder();builder.Logging.ClearProviders();_provider=builder.Build();_provider.Urls.Add("http://127.0.0.1:0");
        _provider.Use(async(context,next)=>{Calls.Enqueue((context.Request.Method,context.Request.Path,context.Request.Headers.Authorization.ToString()));if(Mode=="all503"||Mode=="caller503"&&context.Request.Headers.Authorization!="Bearer "+ServiceToken){await Microsoft.AspNetCore.Http.Results.Json(new{secret="private-provider-diagnostic"},statusCode:503).ExecuteAsync(context);return;}await next();});
        _provider.MapGet("/api/persons/{id:guid}",(Guid id,HttpContext context)=>
        {
            if(!Caller(context,id))return Microsoft.AspNetCore.Http.Results.StatusCode(401);
            if(id!=Person&&id!=Operator&&id!=_service)return Microsoft.AspNetCore.Http.Results.StatusCode(404);
            return Envelope(new{id,scopeId=id==Person?(Guid?)RegistrationApiFixture.Scope:null,role=id==Person?3:2,isDeleted=Mode=="deleted-person"&&id!=_service,
                ownedScopeIds=id==Person?Array.Empty<Guid>():new[]{RegistrationApiFixture.Scope},name="Fixture",email="fixture@example.test",emailVerified=true,twoFactorEnabled=false});
        });
        _provider.MapGet("/api/scopes/{id:guid}",async Task<IResult>(Guid id,HttpContext context)=>
        {
            if(id!=RegistrationApiFixture.Scope||!Caller(context,_service)&&!Caller(context,Operator))return Microsoft.AspNetCore.Http.Results.StatusCode(401);
            if(Mode=="malformed")return Microsoft.AspNetCore.Http.Results.Text("private-provider-diagnostic","application/json");
            if(Mode=="stall-scope")return new StalledBody(this);
            if(Mode=="pause-scope"){Entered.TrySetResult();await Resume.Task.WaitAsync(context.RequestAborted);}
            return Envelope(new{id,isDeleted=false,ownerIds=Mode=="lost-owner"?Array.Empty<Guid>():new[]{_service,Operator},name="Fixture"});
        });
        _provider.MapGet("/api/applications/self",(HttpContext context)=>
        {
            var bearer=context.Request.Headers.Authorization.ToString();if(Mode=="revoke-app"||!bearer.StartsWith("Bearer ",StringComparison.Ordinal))return Microsoft.AspNetCore.Http.Results.StatusCode(401);
            var token=new JwtSecurityTokenHandler().ReadJwtToken(bearer[7..]);if(token.Claims.Single(x=>x.Type=="id").Value!=Application.ToString())return Microsoft.AspNetCore.Http.Results.StatusCode(401);
            return Envelope(new{applicationId=Application,name="Fixture",scopeId=RegistrationApiFixture.Scope,credentialId=Credential,expiresAt=token.ValidTo});
        });
        await _provider.StartAsync();Environment.SetEnvironmentVariable("CERBERUS_HEIMDALL_BASE_URL",Address(_provider));
    }
    public async Task<Host> StartAsync(bool restore=false,bool grants=true,string? connection=null)
    {
        var replacements=new Dictionary<string,string?>
        { ["CERBERUS_RESTORE_REQUIRED"]=restore?"true":"false",["CERBERUS_OPERATIONAL_PERSON_IDS"]=grants?$"{Person},{Operator}":"",["CERBERUS_OPERATIONAL_APPLICATION_IDS"]=grants?Application.ToString():"" };
        if(connection is not null)replacements["CERBERUS_DATA_CONNECTIONSTRING"]=connection;
        var previous=replacements.ToDictionary(x=>x.Key,x=>Environment.GetEnvironmentVariable(x.Key));WebApplication app;
        try{foreach(var(key,value)in replacements)Environment.SetEnvironmentVariable(key,value);app=Startup.CreateApplication([]);}
        finally{foreach(var(key,value)in previous)Environment.SetEnvironmentVariable(key,value);}
        app.Urls.Add("http://127.0.0.1:0");await app.StartAsync();return new(app,new HttpClient{BaseAddress=new Uri(Address(app)),Timeout=TimeSpan.FromSeconds(15)});
    }
    public string Token(string actor,bool foreign=false)
    {
        var id=actor=="application"?Application:actor=="operator"?Operator:Person;var claims=new List<Claim>{new("id",id.ToString()),new("scopeId",(foreign?Guid.NewGuid():RegistrationApiFixture.Scope).ToString())};
        if(actor=="application"){claims.Add(new("tokenKind","application"));claims.Add(new("credentialId",Credential.ToString()));}
        else{claims.Add(new("roleId",actor=="operator"?"2":"3"));if(actor=="operator"){claims.RemoveAll(x=>x.Type=="scopeId");claims.Add(new("ownedScopeIds",$"{Guid.Parse("527a1001-8ef5-4c9b-a565-555555555555")},{RegistrationApiFixture.Scope}"));}}
        claims.Add(new("iat",DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(System.Globalization.CultureInfo.InvariantCulture),ClaimValueTypes.Integer64));
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken("fixture-issuer",actor=="application"?"fixture-applications":"fixture-audience",claims,DateTime.UtcNow.AddSeconds(-1),DateTime.UtcNow.AddMinutes(5),new SigningCredentials(new SymmetricSecurityKey(Encoding.UTF8.GetBytes(Secret)),SecurityAlgorithms.HmacSha256)));
    }
    private bool Caller(HttpContext context,Guid id)
    {
        var raw=context.Request.Headers.Authorization.ToString();if(!raw.StartsWith("Bearer ",StringComparison.Ordinal))return false;
        var token=new JwtSecurityTokenHandler().ReadJwtToken(raw[7..]);return token.Claims.Single(x=>x.Type=="id").Value==id.ToString();
    }
    private static string Address(WebApplication app)=>app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.Single();
    private static IResult Envelope(object data)=>Microsoft.AspNetCore.Http.Results.Json(new{success=true,errors=Array.Empty<string>(),messages=Array.Empty<string>(),data});
    private sealed class StalledBody(OperationalHealthApiFixture owner):IResult
    {
        public async Task ExecuteAsync(HttpContext context)
        {
            context.Response.ContentType="application/json";await context.Response.WriteAsync("{\"success\":true,",context.RequestAborted);await context.Response.Body.FlushAsync(context.RequestAborted);owner.Entered.TrySetResult();
            try{await Task.Delay(Timeout.InfiniteTimeSpan,context.RequestAborted);}catch(OperationCanceledException)when(context.RequestAborted.IsCancellationRequested){}finally{owner.Released.TrySetResult();}
        }
    }
    public sealed record Host(WebApplication Application,HttpClient Client):IAsyncDisposable
    {
        public async ValueTask DisposeAsync(){Client.Dispose();await Application.StopAsync();await Application.DisposeAsync();}
        public async Task<bool> Reconcile(bool allowed)
        {
            var authorization=new Mock<IRestoreAuthorizationVerifier>();authorization.Setup(x=>x.VerifyAsync(It.IsAny<CancellationToken>())).ReturnsAsync(allowed);
            return await new RestoreReconciler(new EmptyLedger(),Mock.Of<ITerminalErasureStore>(),authorization.Object,Application.Services.GetRequiredService<RestoreTrafficGate>()).ReconcileAsync(default);
        }
        private sealed class EmptyLedger:IErasureLedger
        {public Task RecordAsync(ErasureEntry e,CancellationToken ct)=>throw new InvalidOperationException();public async IAsyncEnumerable<ErasureEntry> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation]CancellationToken ct){await Task.CompletedTask;ct.ThrowIfCancellationRequested();yield break;}}
    }
    public async Task DisposeAsync(){await _provider.DisposeAsync();Environment.SetEnvironmentVariable("CERBERUS_HEIMDALL_BASE_URL",_oldProvider);await _base.DisposeAsync();}
}
[CollectionDefinition("Operational health host",DisableParallelization=true)]public sealed class OperationalHealthHostCollection:ICollectionFixture<OperationalHealthApiFixture>;
