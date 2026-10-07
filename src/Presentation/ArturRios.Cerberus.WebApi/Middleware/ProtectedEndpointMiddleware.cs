using ArturRios.Cerberus.Shared.Identity;
using ArturRios.Output;
using Microsoft.AspNetCore.Authorization;

namespace ArturRios.Cerberus.WebApi.Middleware;

public sealed class ProtectedEndpointMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context, HeimdallTokenValidator tokens, IHeimdallClient identity)
    {
        var endpoint = context.GetEndpoint();
        if (endpoint is null || endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null
            || endpoint.Metadata.GetMetadata<ArturRios.Util.WebApi.Security.Attributes.AllowAnonymousAttribute>() is not null)
        {
            await next(context);
            return;
        }
        var headers = context.Request.Headers.Authorization;
        var header = headers.Count == 1 ? headers[0] : null;
        var token = header?.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) == true ? header[7..] : string.Empty;
        var principal = await tokens.ValidateAsync(token);
        if (principal is null || !await identity.RevalidateAsync(token, context.RequestAborted))
        {
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsJsonAsync(ProcessOutput.New.WithError("authentication_required"), context.RequestAborted);
            return;
        }
        context.User = principal;
        await next(context);
    }
}
