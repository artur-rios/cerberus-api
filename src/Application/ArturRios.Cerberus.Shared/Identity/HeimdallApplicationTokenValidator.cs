using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text.Json;
using ArturRios.Cerberus.Shared.Configuration;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Cerberus.Shared.Identity;

public sealed class HeimdallApplicationTokenValidator(CerberusOptions options)
{
    private static readonly string[] PersonAuthority=["roleId","role","roles",ClaimTypes.Role,"ownedScopeIds","mfaPending"];
    public Task<ClaimsPrincipal?> ValidateAsync(string token)=>Task.FromResult(Validate(token));
    private ClaimsPrincipal? Validate(string token)
    {
        if(string.IsNullOrWhiteSpace(token)||string.IsNullOrWhiteSpace(options.AuthApplicationAudience)
            ||options.AuthApplicationAudience==options.AuthAudience||options.HeimdallScopeId==Guid.Empty
            ||string.IsNullOrWhiteSpace(options.AuthValidationSecret))return null;
        try
        {
            var handler=new JwtSecurityTokenHandler{MapInboundClaims=false};
            var principal=handler.ValidateToken(token,new TokenValidationParameters
            {
                ValidIssuer=options.AuthIssuer,ValidAudience=options.AuthApplicationAudience,
                IssuerSigningKeys=HeimdallSigningKeys.Read(options),ValidateIssuer=true,ValidateAudience=true,
                ValidateLifetime=true,ValidateIssuerSigningKey=true,RequireSignedTokens=true,RequireExpirationTime=true,
                ValidAlgorithms=[SecurityAlgorithms.HmacSha256],ClockSkew=TimeSpan.Zero
            },out _);
            var parts=token.Split('.');if(parts.Length!=3)return null;
            using var header=JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[0]));
            using var payload=JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
            var h=header.RootElement;var p=payload.RootElement;
            if(!UniqueObject(h)||!UniqueObject(p)||Text(h,"alg")!=SecurityAlgorithms.HmacSha256
                ||Text(p,"iss")!=options.AuthIssuer||Text(p,"aud")!=options.AuthApplicationAudience
                ||Text(p,"tokenKind")!="application"||PersonAuthority.Any(x=>p.TryGetProperty(x,out _))
                ||!Id(p,"id",out _)||!Id(p,"credentialId",out _)
                ||!Id(p,"scopeId",out var scope)||scope!=options.HeimdallScopeId
                ||!Seconds(p,"iat",out var issued)||!Seconds(p,"nbf",out var notBefore)||!Seconds(p,"exp",out var expires))return null;
            var now=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            return issued<=now&&notBefore<=now&&expires>now&&expires>issued&&expires>notBefore?principal:null;
        }
        catch(Exception e)when(e is SecurityTokenException or ArgumentException or FormatException or JsonException)
        {return null;}
    }
    private static bool UniqueObject(JsonElement value)=>value.ValueKind==JsonValueKind.Object
        &&value.EnumerateObject().Select(x=>x.Name).Distinct(StringComparer.Ordinal).Count()==value.EnumerateObject().Count();
    private static string? Text(JsonElement p,string name)=>p.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
    private static bool Id(JsonElement p,string name,out Guid id)
    {var raw=Text(p,name);return Guid.TryParseExact(raw,"D",out id)&&id!=Guid.Empty&&raw==id.ToString("D");}
    private static bool Seconds(JsonElement p,string name,out long seconds)
    {seconds=0;return p.TryGetProperty(name,out var value)&&value.ValueKind==JsonValueKind.Number&&value.TryGetInt64(out seconds)&&seconds>0&&seconds<=DateTimeOffset.MaxValue.ToUnixTimeSeconds();}
}
