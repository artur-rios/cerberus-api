using System.Text.Json;

namespace ArturRios.Cerberus.Shared.Identity;

public interface IHeimdallOperationalClient
{
    Task<HeimdallAuthorization> RevalidateOperationalAsync(string token, CancellationToken ct);
}

public sealed partial class HeimdallClient : IHeimdallOperationalClient
{
    public async Task<HeimdallAuthorization> RevalidateOperationalAsync(string token, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var principal = await tokens.ValidateOperationalAsync(token);
        if (principal is null) return HeimdallAuthorization.Denied;
        var id = Guid.Parse(principal.FindFirst("id")!.Value);
        var role = principal.FindFirst("roleId")!.Value;
        var response = await SendAsync(HttpMethod.Get, $"/api/persons/{id:D}", null, token, ct);
        if (response.Data is null) return response.Status;
        var person = ParsePerson(response.Data, requireDeletionState: true);
        if (person is null) return HeimdallAuthorization.Unavailable;
        if (person.Id != id || person.IsDeleted || person.Role.ToString(System.Globalization.CultureInfo.InvariantCulture) != role)
            return HeimdallAuthorization.Denied;
        if (role == "3")
        {
            if (person.ScopeId != options.HeimdallScopeId) return HeimdallAuthorization.Denied;
        }
        else
        {
            if (person.ScopeId is not null || person.OwnedScopeIds?.Contains(options.HeimdallScopeId) != true) return HeimdallAuthorization.Denied;
            var scopeResponse = await SendAsync(HttpMethod.Get, $"/api/scopes/{options.HeimdallScopeId:D}", null, token, ct);
            if (scopeResponse.Data is null) return scopeResponse.Status;
            ScopeSnapshot? scope;
            try { scope = scopeResponse.Data.Value.Deserialize<ScopeSnapshot>(Json); }
            catch (JsonException) { return HeimdallAuthorization.Unavailable; }
            if (scope is null) return HeimdallAuthorization.Unavailable;
            if (scope.Id != options.HeimdallScopeId || scope.IsDeleted || scope.OwnerIds?.Contains(id) != true) return HeimdallAuthorization.Denied;
        }
        return await tokens.ValidateOperationalAsync(token) is null ? HeimdallAuthorization.Denied : HeimdallAuthorization.Authorized;
    }
}
