using System.Security.Claims;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace ArturRios.Cerberus.Shared.Identity;

public sealed partial class HeimdallTokenValidator
{
    public Task<ClaimsPrincipal?> ValidateOperationalAsync(string token) => Task.FromResult(OperationalPrincipal(token));

    private ClaimsPrincipal? OperationalPrincipal(string token)
    {
        var principal = ValidateSigned(token);
        if (principal is null) return null;
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            using var header = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[0]), new JsonDocumentOptions { AllowDuplicateProperties = false });
            using var payload = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(parts[1]), new JsonDocumentOptions { AllowDuplicateProperties = false });
            var p = payload.RootElement;
            if (header.RootElement.ValueKind != JsonValueKind.Object || p.ValueKind != JsonValueKind.Object
                || Text(p, "aud") != options.AuthAudience || !CanonicalId(Text(p, "id"), out _)
                || new[] { "tokenKind", "credentialId", "mfaPending", "role", "roles", ClaimTypes.Role, "subjectRightsOnly" }.Any(k => p.TryGetProperty(k, out _))) return null;
            if (Text(p, "roleId") == "3")
                return CanonicalId(Text(p, "scopeId"), out var scope) && scope == options.HeimdallScopeId
                    && !p.TryGetProperty("ownedScopeIds", out _) ? principal : null;
            if (Text(p, "roleId") != "2" || p.TryGetProperty("scopeId", out _)) return null;
            return Operations.OperationalGrants.TryParse(Text(p, "ownedScopeIds"), out var owned)
                && owned.Contains(options.HeimdallScopeId) ? principal : null;
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException) { return null; }
    }

    private static string? Text(JsonElement p, string name) => p.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool CanonicalId(string? raw, out Guid id) => Guid.TryParseExact(raw, "D", out id) && id != Guid.Empty && raw == id.ToString("D");
}
