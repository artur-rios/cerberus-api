using System.IdentityModel.Tokens.Jwt;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Shared.Identity;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Cerberus.Shared.Tests;

public class HeimdallApplicationTokenValidatorTests
{
    [UnitTheory][InlineData(false)][InlineData(true)]
    public async Task GivenCurrentOrPreviousApplicationSigningKey_WhenValidating_ThenBindApplicationScopeAndCredentialWithoutPersonRole(bool previous)
    {
        var o=ApplicationAuthFixture.Options();var p=ApplicationAuthFixture.Payload(o);var token=ApplicationAuthFixture.Token(o,p,previous?o.AuthValidationSecretPrevious:null);
        var principal=await new HeimdallApplicationTokenValidator(o).ValidateAsync(token);Assert.NotNull(principal);
        Assert.Equal(p["id"],principal.FindFirst("id")!.Value);Assert.Equal(p["credentialId"],principal.FindFirst("credentialId")!.Value);Assert.Null(principal.FindFirst("roleId"));
    }
    [UnitTheory]
    [InlineData("issuer")][InlineData("audience")][InlineData("personAudience")][InlineData("scope")][InlineData("application")][InlineData("credential")]
    [InlineData("missingCredential")][InlineData("missingKind")][InlineData("kind")][InlineData("roleId")][InlineData("role")][InlineData("roles")]
    [InlineData("ownedScopeIds")][InlineData("mfaPending")][InlineData("expired")][InlineData("futureIssued")][InlineData("futureNotBefore")]
    [InlineData("missingExpiry")][InlineData("missingIssued")][InlineData("missingNotBefore")][InlineData("stringExpiry")][InlineData("fractionalIssued")][InlineData("expiryRange")]
    [InlineData("duplicateId")][InlineData("duplicateKind")][InlineData("duplicateAudience")][InlineData("signature")][InlineData("algorithm")]
    public async Task GivenUntrustedOrPersonAuthorityApplicationToken_WhenValidating_ThenReject(string kind)
    {
        var o=ApplicationAuthFixture.Options();var p=ApplicationAuthFixture.Payload(o);var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        switch(kind)
        {
            case "issuer":p["iss"]="other";break;
            case "audience":p["aud"]="other";break;
            case "personAudience":p["aud"]=o.AuthAudience!;break;
            case "scope":p["scopeId"]=Guid.NewGuid().ToString();break;
            case "application":p["id"]=Guid.Empty.ToString();break;
            case "credential":p["credentialId"]=Guid.Empty.ToString();break;
            case "missingCredential":p.Remove("credentialId");break;
            case "missingKind":p.Remove("tokenKind");break;
            case "kind":p["tokenKind"]="person";break;
            case "roleId":case "role":case "roles":case "ownedScopeIds":case "mfaPending":p[kind]="3";break;
            case "expired":p["exp"]=now-1;break;
            case "futureIssued":p["iat"]=now+30;break;
            case "futureNotBefore":p["nbf"]=now+30;break;
            case "missingExpiry":p.Remove("exp");break;
            case "missingIssued":p.Remove("iat");break;
            case "missingNotBefore":p.Remove("nbf");break;
            case "stringExpiry":p["exp"]=(now+60).ToString();break;
            case "expiryRange":p["exp"]=long.MaxValue;break;
            case "fractionalIssued":p["iat"]=now-0.5;break;
        }
        var raw=JsonSerializer.Serialize(p);
        if(kind.StartsWith("duplicate",StringComparison.Ordinal))raw=raw[..^1]+","+JsonSerializer.Serialize(kind=="duplicateId"?"id":kind=="duplicateKind"?"tokenKind":"aud")+":"+JsonSerializer.Serialize(kind=="duplicateId"?Guid.NewGuid().ToString():kind=="duplicateKind"?"application":o.AuthApplicationAudience)+"}";
        var token=ApplicationAuthFixture.Raw(raw,kind=="signature"?new string('z',40):o.AuthValidationSecret!,kind=="algorithm"?SecurityAlgorithms.HmacSha384:SecurityAlgorithms.HmacSha256);
        Assert.Null(await new HeimdallApplicationTokenValidator(o).ValidateAsync(token));
    }
    [UnitTheory][InlineData("missing")][InlineData("same")]
    public async Task GivenMissingOrPersonAudienceConfiguration_WhenValidatingApplication_ThenFailClosed(string kind)
    {
        var o=ApplicationAuthFixture.Options();var token=ApplicationAuthFixture.Token(o,ApplicationAuthFixture.Payload(o));o.AuthApplicationAudience=kind=="missing"?null:o.AuthAudience;
        Assert.Null(await new HeimdallApplicationTokenValidator(o).ValidateAsync(token));
    }
    [UnitTheory][InlineData("application")][InlineData("person")][InlineData("")][InlineData(null)]
    public async Task GivenAnyTokenKindOnPersonOrServiceAudience_WhenValidating_ThenNeverAuthorizePersonOrService(string? tokenKind)
    {
        var o=ApplicationAuthFixture.Options();var p=ApplicationAuthFixture.Payload(o);p["aud"]=o.AuthAudience!;p["tokenKind"]=tokenKind!;p["roleId"]="2";p["ownedScopeIds"]=o.HeimdallScopeId.ToString();
        var token=ApplicationAuthFixture.Token(o,p);var validator=new HeimdallTokenValidator(o);Assert.Null(await validator.ValidateAsync(token));Assert.Null(await validator.ValidateServiceAsync(token));
    }
    [UnitFact]
    public async Task GivenPreviousKeyDuringRotation_WhenValidatingPerson_ThenRetainExistingPersonAuthority()
    {
        var o=ApplicationAuthFixture.Options();var p=ApplicationAuthFixture.Payload(o);p["aud"]=o.AuthAudience!;p.Remove("tokenKind");p.Remove("credentialId");
        Assert.NotNull(await new HeimdallTokenValidator(o).ValidateAsync(ApplicationAuthFixture.Token(o,p,o.AuthValidationSecretPrevious)));
    }
}
internal static class ApplicationAuthFixture
{
    internal static CerberusOptions Options()
    {var o=CerberusOptionsValidatorTests.ValidOptions();o.AuthApplicationAudience="fixture-applications";o.AuthValidationSecretPrevious="previous-fixture-signing-key-at-least-32-bytes";return o;}
    internal static Dictionary<string,object> Payload(CerberusOptions o)
    {var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();return new(){["iss"]=o.AuthIssuer!,["aud"]=o.AuthApplicationAudience!,["id"]=Guid.NewGuid().ToString(),["scopeId"]=o.HeimdallScopeId.ToString(),["credentialId"]=Guid.NewGuid().ToString(),["tokenKind"]="application",["name"]="Fixture Application",["iat"]=now-1,["nbf"]=now-1,["exp"]=now+60};}
    internal static string Token(CerberusOptions o,Dictionary<string,object> payload,string? key=null)=>Raw(JsonSerializer.Serialize(payload),key??o.AuthValidationSecret!);
    internal static string Raw(string payload,string key,string algorithm=SecurityAlgorithms.HmacSha256)
    {
        static string B64(byte[] b)=>Convert.ToBase64String(b).TrimEnd('=').Replace('+','-').Replace('/','_');
        var input=B64(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new{alg=algorithm,typ="JWT"})))+"."+B64(Encoding.UTF8.GetBytes(payload));
        var bytes=Encoding.ASCII.GetBytes(input);var secret=Encoding.ASCII.GetBytes(key);
        var signature=algorithm==SecurityAlgorithms.HmacSha384?HMACSHA384.HashData(secret,bytes):HMACSHA256.HashData(secret,bytes);
        return input+"."+B64(signature);
    }
}
