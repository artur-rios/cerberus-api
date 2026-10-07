using ArturRios.Cerberus.Shared.Configuration;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ArturRios.Cerberus.Shared.Identity;

public sealed class HeimdallClient(HttpClient client, CerberusOptions options, HeimdallTokenValidator tokens) : IHeimdallClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public async Task<bool> VerifyScopeAsync(CancellationToken cancellationToken)
    {
        var credential = options.HeimdallServiceCredential!;
        var principal = await tokens.ValidateServiceAsync(credential);
        if (principal is null) return false;
        var id = Guid.Parse(principal.FindFirst("id")!.Value);
        var data = await SendAsync(HttpMethod.Get, $"/api/scopes/{options.HeimdallScopeId:D}", null, credential, cancellationToken);
        if (data is null) return false;
        ScopeSnapshot? scope;
        try { scope = data.Value.Deserialize<ScopeSnapshot>(Json); }
        catch (JsonException) { return false; }
        if (scope is null || scope.Id != options.HeimdallScopeId || scope.IsDeleted || scope.OwnerIds?.Contains(id) != true) return false;
        var personData = await SendAsync(HttpMethod.Get, $"/api/persons/{id:D}", null, credential, cancellationToken);
        var person = ParsePerson(personData, requireDeletionState: true);
        return person is { IsDeleted: false, Role: 2 } && person.Id == id && person.OwnedScopeIds?.Contains(options.HeimdallScopeId) == true;
    }

    private sealed record ScopeSnapshot([property: JsonRequired] Guid Id, [property: JsonRequired] bool IsDeleted,
        [property: JsonRequired] IReadOnlyList<Guid>? OwnerIds);

    public async Task<HeimdallLogin?> LoginAsync(string email, string password, CancellationToken cancellationToken)
    {
        var data = await SendAsync(HttpMethod.Post, "/api/auth/login", new { email, password, scopeId = options.HeimdallScopeId }, null, cancellationToken);
        return await ParseLoginAsync(data, challengeCompletion: false);
    }

    public async Task<HeimdallLogin?> CompleteChallengeAsync(string challenge, string? code, string? recoveryCode, CancellationToken cancellationToken)
    {
        var data = await SendAsync(HttpMethod.Post, "/api/auth/2fa/verify", new { challengeToken = challenge, code, recoveryCode }, null, cancellationToken);
        return await ParseLoginAsync(data, challengeCompletion: true);
    }

    public async Task<HeimdallPerson?> RegisterAsync(HeimdallRegistration registration, CancellationToken cancellationToken)
    {
        var credential = options.HeimdallServiceCredential!;
        if (await tokens.ValidateServiceAsync(credential) is null) return null;
        var data = await SendAsync(HttpMethod.Post, $"/api/scopes/{options.HeimdallScopeId:D}/persons", registration, credential, cancellationToken);
        var person = ParsePerson(data, requireDeletionState: false);
        return person is { Role: 3 } && person.ScopeId == options.HeimdallScopeId ? person : null;
    }

    public async Task<bool> RevalidateAsync(string token, CancellationToken cancellationToken)
    {
        var principal = await tokens.ValidateAsync(token);
        if (principal is null) return false;
        var id = Guid.Parse(principal.FindFirst("id")!.Value);
        var data = await SendAsync(HttpMethod.Get, $"/api/persons/{id:D}", null, token, cancellationToken);
        var person = ParsePerson(data, requireDeletionState: true);
        return person is { Role: 3, IsDeleted: false } && person.Id == id && person.ScopeId == options.HeimdallScopeId;
    }

    private async Task<HeimdallLogin?> ParseLoginAsync(JsonElement? data, bool challengeCompletion)
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
            || result.ChallengeToken is not null || await tokens.ValidateAsync(result.Token ?? string.Empty) is null)
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

    private async Task<JsonElement?> SendAsync(HttpMethod method, string route, object? body, string? bearer, CancellationToken cancellationToken)
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
            if (!response.IsSuccessStatusCode || response.Content.Headers.ContentType?.MediaType != "application/json"
                || response.Content.Headers.ContentLength > options.MaxRequestBytes) return null;
            await using var stream = await response.Content.ReadAsStreamAsync(dependencyToken);
            using var buffer = new MemoryStream();
            var block = new byte[4096];
            int read;
            while ((read = await stream.ReadAsync(block, dependencyToken)) != 0)
            {
                if (buffer.Length > options.MaxRequestBytes - read) return null;
                await buffer.WriteAsync(block.AsMemory(0, read), dependencyToken);
            }
            using var document = JsonDocument.Parse(buffer.ToArray());
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("success", out var success) || success.ValueKind != JsonValueKind.True
                || !root.TryGetProperty("errors", out var errors) || errors.ValueKind != JsonValueKind.Array || errors.GetArrayLength() != 0
                || !root.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object) return null;
            return data.Clone();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return null; }
        catch (HttpRequestException) { return null; }
        catch (JsonException) { return null; }
        catch (IOException) { return null; }
    }
}
