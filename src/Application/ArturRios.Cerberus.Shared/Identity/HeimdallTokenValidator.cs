using ArturRios.Cerberus.Shared.Configuration;
using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace ArturRios.Cerberus.Shared.Identity;

public sealed class HeimdallTokenValidator(CerberusOptions options)
{
    public Task<ClaimsPrincipal?> ValidateAsync(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return Task.FromResult<ClaimsPrincipal?>(null);
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(token, new TokenValidationParameters
            {
                ValidIssuer = options.AuthIssuer,
                ValidAudience = options.AuthAudience,
                IssuerSigningKey = new SymmetricSecurityKey(Encoding.ASCII.GetBytes(options.AuthValidationSecret!)),
                ValidateIssuer = true,
                ValidateAudience = true,
                ValidateLifetime = true,
                ValidateIssuerSigningKey = true,
                RequireSignedTokens = true,
                RequireExpirationTime = true,
                ValidAlgorithms = [SecurityAlgorithms.HmacSha256],
                ClockSkew = TimeSpan.Zero
            }, out _);
            if (!Guid.TryParse(principal.FindFirst("id")?.Value, out var id) || id == Guid.Empty
                || !Guid.TryParse(principal.FindFirst("scopeId")?.Value, out var scope) || scope != options.HeimdallScopeId
                || principal.FindFirst("mfaPending") is not null)
                return Task.FromResult<ClaimsPrincipal?>(null);
            return Task.FromResult<ClaimsPrincipal?>(principal);
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            return Task.FromResult<ClaimsPrincipal?>(null);
        }
    }
}
