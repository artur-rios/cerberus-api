using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using ArturRios.Output;

namespace ArturRios.Cerberus.WebApi.Middleware;

[AttributeUsage(AttributeTargets.Method)]
public sealed class VaultProofBodyAttribute : Attribute;

public sealed class VaultProofBodyMiddleware(RequestDelegate next)
{
    public static readonly object BodyKey = new();
    public async Task InvokeAsync(HttpContext context)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<VaultProofBodyAttribute>() is null) { await next(context); return; }
        if (context.Request.Headers.ContentEncoding.Count != 0
            || !MediaTypeHeaderValue.TryParse(context.Request.ContentType, out var contentType)
            || contentType.CharSet is { } charset && !string.Equals(charset.Trim('"'), "utf-8", StringComparison.OrdinalIgnoreCase))
        { await Invalid(context); return; }
        // The outer BoundedRequestStream enforces the configured aggregate limit.
        // Keep the original bytes in memory; never reconstruct JSON for a proof.
        using var buffer = new MemoryStream();
        await context.Request.Body.CopyToAsync(buffer, context.RequestAborted);
        var raw = buffer.ToArray();
        try { _ = new UTF8Encoding(false, true).GetString(raw); }
        catch (DecoderFallbackException) { await Invalid(context); return; }
        if (raw.AsSpan().StartsWith(new byte[] { 239, 187, 191 })) { await Invalid(context); return; }
        context.Items[BodyKey] = raw;
        var original = context.Request.Body;
        using var body = new MemoryStream(raw, writable: false);
        context.Request.Body = body;
        try { await next(context); }
        finally { context.Request.Body = original; context.Items.Remove(BodyKey); }
    }
    private static Task Invalid(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status400BadRequest;
        return context.Response.WriteAsJsonAsync(ProcessOutput.New.WithError("validation_failed"), context.RequestAborted);
    }
}

public static class FreshIdentity
{
    public static bool IsFresh(ClaimsPrincipal principal, DateTimeOffset now)
    {
        var claims = principal.FindAll("iat").ToArray();
        var seconds = now.ToUnixTimeSeconds();
        return claims.Length == 1 && long.TryParse(claims[0].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var issued)
            && issued >= 0 && issued <= seconds && seconds - issued < 60;
    }
}
