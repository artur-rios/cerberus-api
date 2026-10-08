using ArturRios.Cerberus.Shared.Configuration;
using ArturRios.Cerberus.Domain.Accounts;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArturRios.Cerberus.Shared.Identity;

public sealed class HeimdallClient(HttpClient client, CerberusOptions options, HeimdallTokenValidator tokens) : IHeimdallClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<HeimdallAuthentication> AuthenticateAsync(string email, string password, CancellationToken cancellationToken) =>
        await AuthenticateResponseAsync(await SendAsync(HttpMethod.Post, "/api/auth/login",
            new { email, password, scopeId = options.HeimdallScopeId }, null, cancellationToken), false, cancellationToken);

    public async Task<HeimdallAuthentication> VerifyChallengeAsync(string challenge, string? code, string? recoveryCode, CancellationToken cancellationToken) =>
        await AuthenticateResponseAsync(await SendAsync(HttpMethod.Post, "/api/auth/2fa/verify",
            new { challengeToken = challenge, code, recoveryCode }, null, cancellationToken), true, cancellationToken);

    private async Task<HeimdallAuthentication> AuthenticateResponseAsync(DependencyResponse response, bool challengeCompletion, CancellationToken cancellationToken)
    {
        if (response.Data is null) return new(Error: response.HttpStatus switch
        {
            HttpStatusCode.Forbidden => "authentication_forbidden",
            HttpStatusCode.Unauthorized or HttpStatusCode.NotFound => "authentication_required",
            _ => "identity_unavailable"
        });
        var login = await ParseLoginAsync(response.Data, challengeCompletion, validateToken: false);
        if (login is null) return new(Error: "identity_unavailable");
        if (login.RequiresTwoFactor) return new(Login: login);
        var principal = await tokens.ValidateAsync(login.Token!);
        if (principal is null) return new(Error: "authentication_required");
        return await RevalidateAsync(login.Token!, cancellationToken) switch
        {
            HeimdallAuthorization.Authorized => new(Login: login, IdentityId: Guid.Parse(principal.FindFirst("id")!.Value)),
            HeimdallAuthorization.Denied => new(Error: "authentication_required"),
            _ => new(Error: "identity_unavailable")
        };
    }

    public async Task<RegistrationIdentity> EstablishRegistrationIdentityAsync(
        HeimdallRegistration registration, string? proofToken, CancellationToken cancellationToken)
    {
        if (await tokens.ValidateServiceAsync(options.HeimdallServiceCredential ?? string.Empty) is null)
            return new(RegistrationIdentityStatus.Forbidden);
        if (proofToken is null)
        {
            var loginResponse = await SendAsync(HttpMethod.Post, "/api/auth/login",
                new { registration.Email, registration.Password, scopeId = options.HeimdallScopeId }, null, cancellationToken);
            if (loginResponse.Data is not null)
            {
                var login = await ParseLoginAsync(loginResponse.Data, challengeCompletion: false);
                if (login is null) return new(RegistrationIdentityStatus.Unavailable);
                if (login.RequiresTwoFactor) return new(RegistrationIdentityStatus.Denied);
                proofToken = login.Token;
            }
            else if (loginResponse.HttpStatus == HttpStatusCode.Unauthorized)
            {
                var creation = await SendAsync(HttpMethod.Post, $"/api/scopes/{options.HeimdallScopeId:D}/persons",
                    registration, options.HeimdallServiceCredential, cancellationToken);
                var person = ParsePerson(creation.Data, requireDeletionState: false);
                if (person is { Role: 3, IsDeleted: false } && person.ScopeId == options.HeimdallScopeId)
                    return new(RegistrationIdentityStatus.Verified, person.Id);
                return new(creation.HttpStatus switch
                {
                    HttpStatusCode.Conflict => RegistrationIdentityStatus.Denied,
                    HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => RegistrationIdentityStatus.Forbidden,
                    HttpStatusCode.NotFound => RegistrationIdentityStatus.NotFound,
                    _ => RegistrationIdentityStatus.Unavailable
                });
            }
            else return new(loginResponse.HttpStatus == HttpStatusCode.Forbidden
                ? RegistrationIdentityStatus.Forbidden : RegistrationIdentityStatus.Unavailable);
        }

        var principal = await tokens.ValidateAsync(proofToken ?? string.Empty);
        if (principal is null) return new(RegistrationIdentityStatus.Denied);
        var current = await RevalidateAsync(proofToken!, cancellationToken);
        return current switch
        {
            HeimdallAuthorization.Authorized => new(RegistrationIdentityStatus.Verified, Guid.Parse(principal.FindFirst("id")!.Value)),
            HeimdallAuthorization.Denied => new(RegistrationIdentityStatus.Denied),
            _ => new(RegistrationIdentityStatus.Unavailable)
        };
    }
    public async Task<bool> VerifyScopeAsync(CancellationToken cancellationToken)
    {
        var credential = options.HeimdallServiceCredential!;
        var principal = await tokens.ValidateServiceAsync(credential);
        if (principal is null) return false;
        var id = Guid.Parse(principal.FindFirst("id")!.Value);
        var data = (await SendAsync(HttpMethod.Get, $"/api/scopes/{options.HeimdallScopeId:D}", null, credential, cancellationToken)).Data;
        if (data is null) return false;
        ScopeSnapshot? scope;
        try { scope = data.Value.Deserialize<ScopeSnapshot>(Json); }
        catch (JsonException) { return false; }
        if (scope is null || scope.Id != options.HeimdallScopeId || scope.IsDeleted || scope.OwnerIds?.Contains(id) != true) return false;
        var personData = (await SendAsync(HttpMethod.Get, $"/api/persons/{id:D}", null, credential, cancellationToken)).Data;
        var person = ParsePerson(personData, requireDeletionState: true);
        return person is { IsDeleted: false, Role: 2 } && person.Id == id && person.OwnedScopeIds?.Contains(options.HeimdallScopeId) == true;
    }

    private sealed record ScopeSnapshot([property: JsonRequired] Guid Id, [property: JsonRequired] bool IsDeleted,
        [property: JsonRequired] IReadOnlyList<Guid>? OwnerIds);

    public async Task<HeimdallLogin?> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var data = (await SendAsync(HttpMethod.Post, "/api/auth/login", new { email, password, scopeId = options.HeimdallScopeId }, null, cancellationToken)).Data;
        return await ParseLoginAsync(data, challengeCompletion: false);
    }

    public async Task<HeimdallLogin?> CompleteChallengeAsync(string challenge, string? code, string? recoveryCode, CancellationToken cancellationToken)
    {
        var data = (await SendAsync(HttpMethod.Post, "/api/auth/2fa/verify", new { challengeToken = challenge, code, recoveryCode }, null, cancellationToken)).Data;
        return await ParseLoginAsync(data, challengeCompletion: true);
    }

    public async Task<HeimdallPerson?> RegisterAsync(HeimdallRegistration registration, CancellationToken cancellationToken)
    {
        var credential = options.HeimdallServiceCredential!;
        if (await tokens.ValidateServiceAsync(credential) is null) return null;
        var data = (await SendAsync(HttpMethod.Post, $"/api/scopes/{options.HeimdallScopeId:D}/persons", registration, credential, cancellationToken)).Data;
        var person = ParsePerson(data, requireDeletionState: false);
        return person is { Role: 3 } && person.ScopeId == options.HeimdallScopeId ? person : null;
    }

    public async Task<HeimdallAuthorization> RevalidateAsync(string token, CancellationToken cancellationToken)
    {
        var principal = await tokens.ValidateAsync(token);
        if (principal is null) return HeimdallAuthorization.Denied;
        var id = Guid.Parse(principal.FindFirst("id")!.Value);
        var response = await SendAsync(HttpMethod.Get, $"/api/persons/{id:D}", null, token, cancellationToken);
        if (response.Data is null) return response.Status;
        var person = ParsePerson(response.Data, requireDeletionState: true);
        if (person is null) return HeimdallAuthorization.Unavailable;
        return person is { Role: 3, IsDeleted: false } && person.Id == id && person.ScopeId == options.HeimdallScopeId
            ? HeimdallAuthorization.Authorized : HeimdallAuthorization.Denied;
    }

    private async Task<HeimdallLogin?> ParseLoginAsync(JsonElement? data, bool challengeCompletion, bool validateToken = true)
    {
        if (data is null) return null;
        HeimdallLogin? result;
        try { result = data.Value.Deserialize<HeimdallLogin>(Json); }
        catch (JsonException) { return null; }
        if (result is null) return null;
        if (result.RequiresTwoFactor)
            return !challengeCompletion && result.Token is null && result.ExpiresAt is null && result.EmailVerified is null
                && !string.IsNullOrWhiteSpace(result.ChallengeToken) && result.AvailableMethods is { Count: > 0 }
                && result.AvailableMethods.All(x => x is "App" or "Email") ? result : null;
        if ((!challengeCompletion && !data.Value.TryGetProperty("requiresTwoFactor", out _))
            || result.ExpiresAt is null || result.ExpiresAt <= DateTimeOffset.UtcNow || result.EmailVerified is null
            || result.ChallengeToken is not null || result.Token is null
            || (validateToken && await tokens.ValidateAsync(result.Token) is null))
            return null;
        return result;
    }

    private static HeimdallPerson? ParsePerson(JsonElement? data, bool requireDeletionState)
    {
        if (data is null || (requireDeletionState && !data.Value.TryGetProperty("isDeleted", out _))) return null;
        try
        {
            var person = data.Value.Deserialize<HeimdallPerson>(Json);
            return person?.Id != Guid.Empty ? person : null;
        }
        catch (JsonException) { return null; }
    }

    private sealed record DependencyResponse(JsonElement? Data, HeimdallAuthorization Status, HttpStatusCode? HttpStatus = null);
    private static readonly DependencyResponse Unavailable = new(null, HeimdallAuthorization.Unavailable);
    private static readonly DependencyResponse Denied = new(null, HeimdallAuthorization.Denied);

    private async Task<DependencyResponse> SendAsync(HttpMethod method, string route, object? body, string? bearer, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var limit = TimeSpan.FromSeconds(10);
        timeout.CancelAfter(client.Timeout > TimeSpan.Zero && client.Timeout < limit ? client.Timeout : limit);
        var dependencyToken = timeout.Token;
        using var request = new HttpRequestMessage(method, route);
        if (body is not null) request.Content = JsonContent.Create(body, options: Json);
        if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer);
        try
        {
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, dependencyToken);
            if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden or System.Net.HttpStatusCode.NotFound)
                return Denied with { HttpStatus = response.StatusCode };
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json"
                || response.Content.Headers.ContentLength > options.MaxRequestBytes) return Unavailable with { HttpStatus = response.StatusCode };
            await using var stream = await response.Content.ReadAsStreamAsync(dependencyToken);
            using var buffer = new MemoryStream();
            var block = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(block, dependencyToken)) != 0)
            {
                if (buffer.Length > options.MaxRequestBytes - read) return Unavailable;
                await buffer.WriteAsync(block.AsMemory(0, read), dependencyToken);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return Unavailable;
            return new DependencyResponse(data.Clone(), HeimdallAuthorization.Authorized);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Unavailable; }
        catch (HttpRequestException) { return Unavailable; }
        catch (JsonException) { return Unavailable; }
        catch (IOException) { return Unavailable; }
    }
}
