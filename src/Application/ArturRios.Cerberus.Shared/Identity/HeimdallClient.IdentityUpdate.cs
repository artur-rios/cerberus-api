using System.Net;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Identity;

namespace ArturRios.Cerberus.Shared.Identity;

public sealed partial class HeimdallClient
{
    private static readonly JsonSerializerOptions UpdateJson = new(JsonSerializerDefaults.Web)
    { PropertyNameCaseInsensitive = false, NumberHandling = JsonNumberHandling.Strict };

    public async Task<IdentityUpdateResult> UpdateIdentityAsync(string token, Guid identityId, string name, string email, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var principal = await tokens.ValidateAsync(token);
        if (principal is null || !Guid.TryParse(principal.FindFirst("id")?.Value, out var actor) || actor != identityId)
            return new(Error: "authentication_required");
        var response = await SendAsync(HttpMethod.Put, $"/api/persons/{identityId:D}", new { name, email }, token, cancellationToken);
        if (response.Data is null) return new(Error: response.HttpStatus switch
        {
            HttpStatusCode.BadRequest => "validation_failed",
            HttpStatusCode.Unauthorized => "authentication_required",
            HttpStatusCode.Forbidden => "identity_update_forbidden",
            HttpStatusCode.NotFound => "not_found",
            HttpStatusCode.Conflict => "identity_conflict",
            _ => "identity_unavailable"
        });
        UpdatedPerson? person;
        try { person = response.Data.Value.Deserialize<UpdatedPerson>(UpdateJson); }
        catch (JsonException) { return new(Error: "identity_unavailable"); }
        if (person is null || person.Id != identityId || person.Role != 3 || person.ScopeId != options.HeimdallScopeId
            || person.Name != name || !string.Equals(person.Email, email, StringComparison.OrdinalIgnoreCase))
            return new(Error: "identity_unavailable");
        return new(new IdentityDetails(person.Id, person.Name, person.Email, person.EmailVerified));
    }

    private sealed record UpdatedPerson([property: JsonRequired] Guid Id, [property: JsonRequired] string Name,
        [property: JsonRequired] string Email, [property: JsonRequired] int Role, [property: JsonRequired] Guid ScopeId,
        [property: JsonRequired] bool EmailVerified);
}
