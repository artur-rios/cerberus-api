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
        var principal = ValidateSigned(token);
        if (principal is null || !Guid.TryParse(principal.FindFirst("scopeId")?.Value, out var scope) || scope != options.HeimdallScopeId)
            return Task.FromResult<ClaimsPrincipal?>(null);
        return Task.FromResult<ClaimsPrincipal?>(principal);
    }

    public Task<ClaimsPrincipal?> ValidateServiceAsync(string token)
    {
        var principal = ValidateSigned(token);
        var owned = principal?.FindFirst("ownedScopeIds")?.Value.Split(',', StringSplitOptions.RemoveEmptyEntries);
        if (principal?.FindFirst("roleId")?.Value != "2" || owned is not { Length: 1 }
            || !Guid.TryParse(owned[0], out var scope) || scope != options.HeimdallScopeId)
            return Task.FromResult<ClaimsPrincipal?>(null);
        return Task.FromResult<ClaimsPrincipal?>(principal);
    }

    private ClaimsPrincipal? ValidateSigned(string token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
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
                || principal.FindFirst("mfaPending") is not null)
                return null;
            return principal;
        }
        catch (Exception exception) when (exception is SecurityTokenException or ArgumentException)
        {
            return null;
        }
    }
}
