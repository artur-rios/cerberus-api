using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text.Json;
using System.Text.Json.Serialization;
using ArturRios.Cerberus.Domain.Software;
using ArturRios.Cerberus.Shared.Configuration;

namespace ArturRios.Cerberus.Shared.Identity;

public sealed class HeimdallApplicationClient(HttpClient client,CerberusOptions options,HeimdallApplicationTokenValidator tokens):IHeimdallApplicationClient
{
    private static readonly JsonSerializerOptions Json=new(JsonSerializerDefaults.Web)
    {PropertyNameCaseInsensitive=false,AllowDuplicateProperties=false,RespectNullableAnnotations=true};
    private static readonly HeimdallApplicationAuthentication Unavailable=new(Error:"identity_unavailable");
    private static readonly HeimdallApplicationAuthentication Denied=new(Error:"authentication_required");
    public async Task<HeimdallApplicationAuthentication> AuthenticateAsync(string token,CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();var principal=await tokens.ValidateAsync(token);if(principal is null)return Denied;
        if(options.MaxRequestBytes<=0)return Unavailable;
        var expected=Identity(principal);if(!expected.IsValid())return Denied;
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);var limit=TimeSpan.FromSeconds(10);
        timeout.CancelAfter(client.Timeout>TimeSpan.Zero&&client.Timeout<limit?client.Timeout:limit);var dependencyToken=timeout.Token;
        using var request=new HttpRequestMessage(HttpMethod.Get,"/api/applications/self");request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",token);
        try
        {
            using var response=await client.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,dependencyToken);
            if(response.StatusCode==HttpStatusCode.Unauthorized)return Denied;
            var media=response.Content.Headers.ContentType;var charset=media?.CharSet;
            if(response.StatusCode!=HttpStatusCode.OK||media is null||!string.Equals(media.MediaType,"application/json",StringComparison.OrdinalIgnoreCase)
                ||media.Parameters.Count>1||media.Parameters.Any(p=>!string.Equals(p.Name,"charset",StringComparison.OrdinalIgnoreCase))
                ||charset is not null&&!string.Equals(charset,"utf-8",StringComparison.OrdinalIgnoreCase)&&!string.Equals(charset,"\"utf-8\"",StringComparison.OrdinalIgnoreCase)
                ||response.Content.Headers.ContentEncoding.Count!=0||response.Content.Headers.ContentLength>options.MaxRequestBytes)return Unavailable;
            await using var stream=await response.Content.ReadAsStreamAsync(dependencyToken);using var buffer=new MemoryStream();var block=new byte[4096];int read;
            while((read=await stream.ReadAsync(block,dependencyToken))!=0)
            {if(buffer.Length>options.MaxRequestBytes-read)return Unavailable;await buffer.WriteAsync(block.AsMemory(0,read),dependencyToken);}
            using var document=JsonDocument.Parse(buffer.ToArray(),new JsonDocumentOptions{AllowDuplicateProperties=false});var root=document.RootElement;
            if(root.ValueKind!=JsonValueKind.Object||!root.TryGetProperty("success",out var success)||success.ValueKind!=JsonValueKind.True
                ||!root.TryGetProperty("errors",out var errors)||errors.ValueKind!=JsonValueKind.Array||errors.GetArrayLength()!=0
                ||!root.TryGetProperty("data",out var data)||data.ValueKind!=JsonValueKind.Object)return Unavailable;
            var live=data.Deserialize<LiveIdentity>(Json);
            if(live is null||string.IsNullOrWhiteSpace(live.Name)||live.ExpiresAt.Offset!=TimeSpan.Zero
                ||live.ApplicationId!=expected.ApplicationId||live.ScopeId!=expected.ScopeId||live.CredentialId!=expected.CredentialId||live.ExpiresAt!=expected.ExpiresAt)return Unavailable;
            // The network wait cannot turn an expired token into current authority.
            return await tokens.ValidateAsync(token) is null?Denied:new(expected);
        }
        catch(OperationCanceledException)when(!ct.IsCancellationRequested){return Unavailable;}
        catch(Exception e)when(e is HttpRequestException or JsonException or IOException){return Unavailable;}
    }
    private static SoftwareIdentity Identity(ClaimsPrincipal p)=>new(Guid.Parse(p.FindFirst("id")!.Value),Guid.Parse(p.FindFirst("scopeId")!.Value),
        Guid.Parse(p.FindFirst("credentialId")!.Value),DateTimeOffset.FromUnixTimeSeconds(long.Parse(p.FindFirst("exp")!.Value,CultureInfo.InvariantCulture)));
    [JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
    private sealed record LiveIdentity([property:JsonRequired]Guid ApplicationId,[property:JsonRequired]string Name,
        [property:JsonRequired]Guid ScopeId,[property:JsonRequired]Guid CredentialId,[property:JsonRequired]DateTimeOffset ExpiresAt);
}
