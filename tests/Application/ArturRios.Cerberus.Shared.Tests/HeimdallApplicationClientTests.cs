using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Shared.Identity;

namespace ArturRios.Cerberus.Shared.Tests;

public class HeimdallApplicationClientTests
{
    [UnitFact]
    public async Task GivenLiveApplicationCredential_WhenAuthenticatingTwice_ThenRevalidateEveryReleaseUsingOriginalBearerAndExactSelfRoute()
    {
        var o=ApplicationAuthFixture.Options();var p=ApplicationAuthFixture.Payload(o);var token=ApplicationAuthFixture.Token(o,p);var calls=0;
        using var http=new HttpClient(new Callback((request,_)=>
        {calls++;Assert.Equal("/api/applications/self",request.RequestUri!.AbsolutePath);Assert.Equal("Bearer "+token,request.Headers.Authorization!.ToString());return Task.FromResult(Response(p));})){BaseAddress=new(o.HeimdallBaseUrl!)};
        var client=new HeimdallApplicationClient(http,o,new(o));
        for(var i=0;i<2;i++)
        {
            var r=await client.AuthenticateAsync(token,default);Assert.Null(r.Error);Assert.NotNull(r.Identity);
            Assert.Equal(Guid.Parse(p["id"].ToString()!),r.Identity.ApplicationId);Assert.Equal(o.HeimdallScopeId,r.Identity.ScopeId);
            Assert.Equal(Guid.Parse(p["credentialId"].ToString()!),r.Identity.CredentialId);Assert.Equal(DateTimeOffset.FromUnixTimeSeconds((long)p["exp"]),r.Identity.ExpiresAt);
        }
        Assert.Equal(2,calls);
    }
    [UnitTheory][InlineData("application")][InlineData("scope")][InlineData("credential")][InlineData("expiry")]
    [InlineData("missing")][InlineData("extra")][InlineData("malformed")][InlineData("duplicate")][InlineData("falseSuccess")][InlineData("errors")]
    [InlineData("media")][InlineData("charset")][InlineData("oversized")][InlineData("unboundedOversized")]
    [InlineData("denied")][InlineData("forbidden")][InlineData("missingRoute")][InlineData("redirect")][InlineData("rate")][InlineData("outage")][InlineData("created")]
    public async Task GivenIncompatibleOrRejectedLiveResponse_WhenAuthenticating_ThenReturnOnlySafeFailureAndNoIdentity(string kind)
    {
        var o=ApplicationAuthFixture.Options();var p=ApplicationAuthFixture.Payload(o);var token=ApplicationAuthFixture.Token(o,p);var data=Snapshot(p);
        switch(kind)
        {
            case "application":data["applicationId"]=Guid.NewGuid();break;
            case "scope":data["scopeId"]=Guid.NewGuid();break;
            case "credential":data["credentialId"]=Guid.NewGuid();break;
            case "expiry":data["expiresAt"]=DateTimeOffset.FromUnixTimeSeconds((long)p["exp"]+1);break;
            case "missing":data.Remove("credentialId");break;
            case "extra":data["roleId"]="3";break;
        }
        var raw=JsonSerializer.Serialize(new{success=kind!="falseSuccess",errors=kind=="errors"?new[]{"private-provider-error"}:Array.Empty<string>(),data});
        if(kind=="malformed")raw="private-provider-error";
        if(kind=="duplicate")raw=raw.Replace("\"success\":true","\"success\":true,\"success\":true",StringComparison.Ordinal);
        var status=kind switch{"denied"=>HttpStatusCode.Unauthorized,"forbidden"=>HttpStatusCode.Forbidden,"missingRoute"=>HttpStatusCode.NotFound,"redirect"=>HttpStatusCode.Redirect,"rate"=>HttpStatusCode.TooManyRequests,"outage"=>HttpStatusCode.ServiceUnavailable,"created"=>HttpStatusCode.Created,_=>HttpStatusCode.OK};
        if(kind is "oversized" or "unboundedOversized")o.MaxRequestBytes=64;
        using var http=new HttpClient(new Callback((_,_)=>
        {
            HttpContent content=kind=="unboundedOversized"?new StreamContent(new Unseekable(Encoding.UTF8.GetBytes(raw))):new StringContent(raw,Encoding.UTF8,"application/json");
            content.Headers.ContentType=new MediaTypeHeaderValue(kind=="media"?"text/plain":"application/json"){CharSet=kind=="charset"?"utf-16":"utf-8"};
            return Task.FromResult(new HttpResponseMessage(status){Content=content});
        })){BaseAddress=new(o.HeimdallBaseUrl!)};
        var r=await new HeimdallApplicationClient(http,o,new(o)).AuthenticateAsync(token,default);
        Assert.Equal(kind=="denied"?"authentication_required":"identity_unavailable",r.Error);Assert.Null(r.Identity);
    }
    [UnitFact]
    public async Task GivenInvalidSignedToken_WhenAuthenticating_ThenNeverCallProvider()
    {
        var o=ApplicationAuthFixture.Options();using var http=new HttpClient(new Callback((_,_)=>throw new InvalidOperationException("Provider must not be reached."))){BaseAddress=new(o.HeimdallBaseUrl!)};
        var r=await new HeimdallApplicationClient(http,o,new(o)).AuthenticateAsync("invalid",default);Assert.Equal("authentication_required",r.Error);Assert.Null(r.Identity);
    }
    [UnitTheory][InlineData(false)][InlineData(true)]
    public async Task GivenTimeoutOrTransportFailure_WhenAuthenticating_ThenReturnSafeUnavailable(bool transport)
    {
        var o=ApplicationAuthFixture.Options();var token=ApplicationAuthFixture.Token(o,ApplicationAuthFixture.Payload(o));
        using var http=new HttpClient(new Callback(async(_,ct)=>{if(transport)throw new HttpRequestException("private-network-details");await Task.Delay(Timeout.InfiniteTimeSpan,ct);throw new InvalidOperationException();})){BaseAddress=new(o.HeimdallBaseUrl!),Timeout=TimeSpan.FromMilliseconds(50)};
        var r=await new HeimdallApplicationClient(http,o,new(o)).AuthenticateAsync(token,default);Assert.Equal("identity_unavailable",r.Error);Assert.Null(r.Identity);
    }
    [UnitFact]
    public async Task GivenCallerCancellationDuringLiveCheck_WhenAuthenticating_ThenPropagateCancellation()
    {
        var o=ApplicationAuthFixture.Options();var token=ApplicationAuthFixture.Token(o,ApplicationAuthFixture.Payload(o));using var ct=new CancellationTokenSource(TimeSpan.FromMilliseconds(50));
        using var http=new HttpClient(new Callback(async(_,token)=>{await Task.Delay(Timeout.InfiniteTimeSpan,token);throw new InvalidOperationException();})){BaseAddress=new(o.HeimdallBaseUrl!)};
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new HeimdallApplicationClient(http,o,new(o)).AuthenticateAsync(token,ct.Token));
    }
    [UnitFact]
    public async Task GivenTokenExpiresDuringLiveCheck_WhenAuthenticating_ThenNeverReleaseExpiredIdentity()
    {
        var o=ApplicationAuthFixture.Options();var p=ApplicationAuthFixture.Payload(o);p["exp"]=DateTimeOffset.UtcNow.ToUnixTimeSeconds()+2;var token=ApplicationAuthFixture.Token(o,p);
        using var http=new HttpClient(new Callback(async(_,ct)=>{await Task.Delay(TimeSpan.FromMilliseconds(2100),ct);return Response(p);})){BaseAddress=new(o.HeimdallBaseUrl!)};
        var r=await new HeimdallApplicationClient(http,o,new(o)).AuthenticateAsync(token,default);Assert.Equal("authentication_required",r.Error);Assert.Null(r.Identity);
    }
    private static Dictionary<string,object> Snapshot(Dictionary<string,object> p)=>new(){["applicationId"]=p["id"],["name"]=p["name"],["scopeId"]=p["scopeId"],["credentialId"]=p["credentialId"],["expiresAt"]=DateTimeOffset.FromUnixTimeSeconds((long)p["exp"])};
    private static HttpResponseMessage Response(Dictionary<string,object> p)=>new(HttpStatusCode.OK){Content=new StringContent(JsonSerializer.Serialize(new{success=true,errors=Array.Empty<string>(),data=Snapshot(p)}),Encoding.UTF8,"application/json")};
    private sealed class Callback(Func<HttpRequestMessage,CancellationToken,Task<HttpResponseMessage>> callback):HttpMessageHandler
    {protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct)=>callback(request,ct);}
    private sealed class Unseekable(byte[] bytes):MemoryStream(bytes){public override bool CanSeek=>false;}
}
