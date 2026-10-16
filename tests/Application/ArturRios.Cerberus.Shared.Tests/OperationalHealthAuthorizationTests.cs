using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Cerberus.Shared.Operations;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Cerberus.Shared.Tests;

public sealed class OperationalHealthAuthorizationTests
{
    private static readonly Guid Subject=Guid.Parse("527a1001-8ef5-4c9b-a565-222222222222");
    private static readonly Guid Credential=Guid.Parse("527a1001-8ef5-4c9b-a565-444444444444");
    private static readonly Guid Other=Guid.Parse("527a1001-8ef5-4c9b-a565-555555555555");
    [UnitTheory][InlineData("person")][InlineData("operator")][InlineData("application")]
    public async Task GivenExplicitTypedGrantAndCurrentActor_WhenAuthorizing_ThenBindOnlyActualCallerToReadOnlyAuthority(string actor)
    {
        var options=Options(actor);var token=Token(options,actor);var paths=new List<string>();
        using var http=Transport(options,request=>{Assert.Equal(HttpMethod.Get,request.Method);Assert.Null(request.Content);Assert.Equal(token,request.Headers.Authorization!.Parameter);paths.Add(request.RequestUri!.AbsolutePath);return Task.FromResult(Live(options,actor,request));});
        Assert.Equal(OperationalAuthorization.Authorized,await Authorizer(http,options).AuthorizeAsync(token,default));
        Assert.Equal(actor=="application"?new[]{"/api/applications/self"}:actor=="operator"?new[]{"/api/persons/"+Subject,"/api/scopes/"+options.HeimdallScopeId}:new[]{"/api/persons/"+Subject},paths);
    }
    [UnitTheory][InlineData("person")][InlineData("operator")][InlineData("application")]
    public async Task GivenNoSeparateOperationalGrant_WhenAuthorizing_ThenDenyBeforeDependencyProbes(string actor)
    {
        var options=Options(actor);options.OperationalPersonIds=null;options.OperationalApplicationIds=null;
        using var http=Transport(options,_=>throw new InvalidOperationException("Ungrant reached provider"));
        Assert.Equal(OperationalAuthorization.Forbidden,await Authorizer(http,options).AuthorizeAsync(Token(options,actor),default));
    }
    [UnitTheory][InlineData("person")][InlineData("application")]
    public async Task GivenSameUuidGrantedOnlyToOtherActorKind_WhenAuthorizing_ThenNeverCrossTypedGrant(string actor)
    {
        var options=Options(actor);options.OperationalPersonIds=actor=="application"?Subject.ToString():null;options.OperationalApplicationIds=actor=="person"?Subject.ToString():null;
        using var http=Transport(options,_=>throw new InvalidOperationException("Wrong subject kind reached provider"));
        Assert.Equal(OperationalAuthorization.Forbidden,await Authorizer(http,options).AuthorizeAsync(Token(options,actor),default));
    }
    [UnitTheory]
    [InlineData("empty")][InlineData("malformed")][InlineData("wrong-scope")][InlineData("wrong-audience")][InlineData("expired")][InlineData("future")]
    [InlineData("mfa")][InlineData("mixed-kind")][InlineData("mixed-credential")][InlineData("alias-role")][InlineData("duplicate-id")][InlineData("noncanonical-id")]
    [InlineData("operator-wrong-scope")][InlineData("operator-duplicate-scope")][InlineData("admin")]
    public async Task GivenInvalidOrMixedPersonClaims_WhenAuthorizing_ThenRejectWithoutTransport(string kind)
    {
        var options=Options("person");using var http=Transport(options,_=>throw new InvalidOperationException("Invalid token reached provider"));
        var claims=Claims(options,"person");switch(kind)
        {
            case "wrong-scope":claims["scopeId"]=Other.ToString();break;case "wrong-audience":claims["aud"]="other";break;
            case "expired":claims["nbf"]=DateTimeOffset.UtcNow.AddMinutes(-2).ToUnixTimeSeconds();claims["exp"]=DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds();break;
            case "future":claims["nbf"]=DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds();break;
            case "mfa":claims["mfaPending"]="true";break;case "mixed-kind":claims["tokenKind"]="application";break;case "mixed-credential":claims["credentialId"]=Credential.ToString();break;
            case "alias-role":claims["roles"]="operator";break;case "noncanonical-id":claims["id"]=Subject.ToString("N");break;case "admin":claims["roleId"]="1";break;
            case "operator-wrong-scope":claims.Remove("scopeId");claims["roleId"]="2";claims["ownedScopeIds"]=Other.ToString();break;
            case "operator-duplicate-scope":claims.Remove("scopeId");claims["roleId"]="2";claims["ownedScopeIds"]=$"{options.HeimdallScopeId},{options.HeimdallScopeId}";break;
        }
        var json=JsonSerializer.Serialize(claims);if(kind=="duplicate-id")json=json.Replace("\"id\":",$"\"id\":\"{Other}\",\"id\":",StringComparison.Ordinal);
        var token=kind=="empty"?"":kind=="malformed"?"not-a-token":Sign(options,json);
        Assert.Equal(OperationalAuthorization.Unauthenticated,await Authorizer(http,options).AuthorizeAsync(token,default));
    }
    [UnitTheory][InlineData("person","deleted",OperationalAuthorization.Unauthenticated)][InlineData("person","wrong-id",OperationalAuthorization.Unauthenticated)]
    [InlineData("person","wrong-role",OperationalAuthorization.Unauthenticated)][InlineData("person","wrong-scope",OperationalAuthorization.Unauthenticated)]
    [InlineData("operator","lost-owner",OperationalAuthorization.Unauthenticated)][InlineData("operator","lost-scope",OperationalAuthorization.Unauthenticated)]
    [InlineData("operator","deleted",OperationalAuthorization.Unauthenticated)][InlineData("operator","wrong-role",OperationalAuthorization.Unauthenticated)]
    [InlineData("person","401",OperationalAuthorization.Unauthenticated)][InlineData("operator","403",OperationalAuthorization.Unauthenticated)]
    [InlineData("person","503",OperationalAuthorization.Unavailable)][InlineData("operator","503",OperationalAuthorization.Unavailable)]
    [InlineData("person","malformed",OperationalAuthorization.Unavailable)][InlineData("operator","malformed",OperationalAuthorization.Unavailable)]
    [InlineData("application","401",OperationalAuthorization.Unauthenticated)][InlineData("application","503",OperationalAuthorization.Unavailable)]
    [InlineData("application","wrong-credential",OperationalAuthorization.Unavailable)]
    public async Task GivenLiveAuthorityLossOrFailure_WhenAuthorizing_ThenFailClosed(string actor,string kind,OperationalAuthorization expected)
    {
        var options=Options(actor);using var http=Transport(options,r=>Task.FromResult(Live(options,actor,r,kind)));
        Assert.Equal(expected,await Authorizer(http,options).AuthorizeAsync(Token(options,actor),default));
    }
    [UnitTheory][InlineData(null,true)][InlineData("",true)][InlineData("527a1001-8ef5-4c9b-a565-222222222222",true)]
    [InlineData("527a1001-8ef5-4c9b-a565-222222222222,527a1001-8ef5-4c9b-a565-555555555555",true)]
    [InlineData("00000000-0000-0000-0000-000000000000",false)][InlineData("not-a-uuid",false)][InlineData(" ",false)]
    [InlineData("527a1001-8ef5-4c9b-a565-222222222222,",false)][InlineData("527a1001-8ef5-4c9b-a565-222222222222,527a1001-8ef5-4c9b-a565-222222222222",false)]
    [InlineData("527A1001-8EF5-4C9B-A565-222222222222",false)]
    public void GivenOperationalGrantSetting_WhenValidatingStartup_ThenAcceptOnlyUnambiguousTypedSubjects(string? value,bool valid)
    {
        var options=Options("person");options.OperationalPersonIds=value;Assert.Equal(valid,new CerberusOptionsValidator(false).Validate(null,options).Succeeded);
        options=Options("person");options.OperationalApplicationIds=value;Assert.Equal(valid,new CerberusOptionsValidator(false).Validate(null,options).Succeeded);
    }
    [UnitTheory][InlineData("person")][InlineData("operator")][InlineData("application")]
    public async Task GivenCallerCancellationDuringProviderBody_WhenAuthorizing_ThenPropagateWithoutDetails(string actor)
    {
        var options=Options(actor);using var canceled=new CancellationTokenSource();var stream=new PendingBody();
        using var http=Transport(options,_=>{canceled.Cancel();var r=new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(stream)};r.Content.Headers.ContentType=new("application/json");return Task.FromResult(r);});
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>Authorizer(http,options).AuthorizeAsync(Token(options,actor),canceled.Token));
    }
    [UnitTheory][InlineData("person")][InlineData("operator")]
    public async Task GivenGrantedPersonExpiresDuringAuthorityWait_WhenAuthorizing_ThenNeverReturnCurrentAuthority(string actor)
    {
        var options=Options(actor);var c=Claims(options,actor);var expires=DateTimeOffset.UtcNow.AddSeconds(2).ToUnixTimeSeconds();c["exp"]=expires;var entered=0;
        using var http=Transport(options,async r=>{Interlocked.Increment(ref entered);var remaining=DateTimeOffset.FromUnixTimeSeconds(expires).AddMilliseconds(100)-DateTimeOffset.UtcNow;if(remaining>TimeSpan.Zero)await Task.Delay(remaining);return Live(options,actor,r);});
        Assert.Equal(OperationalAuthorization.Unauthenticated,await Authorizer(http,options).AuthorizeAsync(Sign(options,JsonSerializer.Serialize(c)),default));Assert.True(entered>0,"Expiry must occur after a real provider request; initial refusal cannot satisfy this case.");
    }
    [UnitTheory][InlineData("person")][InlineData("operator")][InlineData("application")]
    public async Task GivenStalledAuthorityBody_WhenAuthorizing_ThenBoundResponseWithoutLeakingException(string actor)
    {
        var options=Options(actor);using var http=Transport(options,_=>{var r=new HttpResponseMessage(HttpStatusCode.OK){Content=new StreamContent(new PendingBody())};r.Content.Headers.ContentType=new("application/json");return Task.FromResult(r);});http.Timeout=TimeSpan.FromMilliseconds(40);
        Assert.Equal(OperationalAuthorization.Unavailable,await Authorizer(http,options).AuthorizeAsync(Token(options,actor),default).WaitAsync(TimeSpan.FromSeconds(2)));
    }
    private static CerberusOptions Options(string actor)
    {var o=CerberusOptionsValidatorTests.ValidOptions();o.OperationalPersonIds=actor=="application"?null:Subject.ToString();o.OperationalApplicationIds=actor=="application"?Subject.ToString():null;o.HeimdallServiceCredential="configured-credential-must-not-be-substituted";return o;}
    private static Dictionary<string,object> Claims(CerberusOptions o,string actor)
    {
        var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();var d=new Dictionary<string,object>{{"iss",o.AuthIssuer!},{"aud",actor=="application"?o.AuthApplicationAudience!:o.AuthAudience!},{"id",Subject.ToString()},{"iat",now},{"nbf",now-1},{"exp",now+60}};
        if(actor=="application"){d["tokenKind"]="application";d["credentialId"]=Credential.ToString();d["scopeId"]=o.HeimdallScopeId.ToString();}
        else{d["roleId"]=actor=="operator"?"2":"3";if(actor=="operator")d["ownedScopeIds"]=$"{Other},{o.HeimdallScopeId}";else d["scopeId"]=o.HeimdallScopeId.ToString();}return d;
    }
    private static string Token(CerberusOptions o,string actor)=>Sign(o,JsonSerializer.Serialize(Claims(o,actor)));
    private static string Sign(CerberusOptions o,string json)
    {var h=Base64UrlEncoder.Encode("{\"alg\":\"HS256\",\"typ\":\"JWT\"}");var p=Base64UrlEncoder.Encode(json);var data=h+"."+p;return data+"."+Base64UrlEncoder.Encode(System.Security.Cryptography.HMACSHA256.HashData(Encoding.UTF8.GetBytes(o.AuthValidationSecret!),Encoding.UTF8.GetBytes(data)));}
    private static HttpResponseMessage Live(CerberusOptions o,string actor,HttpRequestMessage r,string kind="ok")
    {
        if(int.TryParse(kind,out var status))return new((HttpStatusCode)status);
        if(kind=="malformed")return new(HttpStatusCode.OK){Content=new StringContent("private-provider-diagnostic",Encoding.UTF8,"application/json")};
        object data;if(actor=="application")data=new{applicationId=Subject,name="Fixture",scopeId=o.HeimdallScopeId,credentialId=kind=="wrong-credential"?Other:Credential,expiresAt=DateTimeOffset.FromUnixTimeSeconds(new JwtSecurityTokenHandler().ReadJwtToken(r.Headers.Authorization!.Parameter).Payload.Expiration!.Value)};
        else if(r.RequestUri!.AbsolutePath.Contains("/api/scopes/",StringComparison.Ordinal))data=new{id=o.HeimdallScopeId,isDeleted=false,ownerIds=kind=="lost-owner"?Array.Empty<Guid>():new[]{Subject},name="Fixture"};
        else data=new{id=kind=="wrong-id"?Other:Subject,scopeId=actor=="operator"?(Guid?)null:kind=="wrong-scope"?Other:o.HeimdallScopeId,role=kind=="wrong-role"?1:actor=="operator"?2:3,isDeleted=kind=="deleted",ownedScopeIds=kind=="lost-scope"?Array.Empty<Guid>():new[]{Other,o.HeimdallScopeId},name="Fixture",email="fixture@example.test",emailVerified=true,twoFactorEnabled=false};
        return new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{success=true,errors=Array.Empty<string>(),messages=Array.Empty<string>(),data}),Encoding.UTF8,"application/json")};
    }
    private static HttpClient Transport(CerberusOptions o,Func<HttpRequestMessage,Task<HttpResponseMessage>> send)=>new(new Handler(send)){BaseAddress=new Uri(o.HeimdallBaseUrl!)};
    private static OperationalHealthAuthorizer Authorizer(HttpClient c,CerberusOptions o)
    {var tokens=new HeimdallTokenValidator(o);var apps=new HeimdallApplicationTokenValidator(o);return new(o,tokens,apps,new HeimdallClient(c,o,tokens),new HeimdallApplicationClient(c,o,apps));}
    private sealed class Handler(Func<HttpRequestMessage,Task<HttpResponseMessage>> send):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)=>send(r);}
    private sealed class PendingBody:MemoryStream
    {public override async ValueTask<int> ReadAsync(Memory<byte> b,CancellationToken ct=default){await Task.Delay(Timeout.InfiniteTimeSpan,ct);return 0;}}
}
